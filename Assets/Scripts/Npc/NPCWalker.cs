using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Animations;

// =========================================================
//  NPC WALKER  —  Humanoid / Mixamo compatible
//  ─────────────────────────────────────────────────────────
//  Uses the Unity Playables API to drive animation at runtime
//  with NO Animator Controller asset required.
//  Works with Humanoid rigs (Mixamo FBX models).
//
//  Just assign Walk Clip (and optionally Run Clip) on
//  NPCSpawner and hit Play — nothing else needed.
//
//  NOTE: For best results, enable "Loop Time" on your animation
//  clips in the FBX import settings (Animations tab → select
//  clip → check Loop Time → Apply).
// =========================================================

public class NPCWalker : MonoBehaviour
{
    // ── Set by NPCSpawner ─────────────────────────────────
    [HideInInspector] public List<Vector3> waypoints;
    [HideInInspector] public List<Vector3> sidewalkWaypoints;
    [HideInInspector] public float walkSpeed = 2f;
    [HideInInspector] public float runSpeed = 5f;
    [HideInInspector] public float reachRadius = 2f;
    [HideInInspector] public float pauseMin = 1f;
    [HideInInspector] public float pauseMax = 4f;
    [HideInInspector] public AnimationClip walkClip;
    [HideInInspector] public AnimationClip runClip;   // optional

    // ── Private ───────────────────────────────────────────
    private CharacterController cc;
    private Animator anim;

    // Playables graph: Animator → Mixer → [walkPlayable, runPlayable]
    private PlayableGraph graph;
    private AnimationMixerPlayable mixer;
    private AnimationClipPlayable walkPlayable;
    private AnimationClipPlayable runPlayable;
    private bool graphReady = false;
    private bool hasRunClip = false;
    private float walkClipLength = 0f;
    private float runClipLength = 0f;

    private Vector3 target;
    private float currentSpeed;
    private float yVelocity = 0f;
    private float pauseTimer = 0f;
    private int stuckFrames = 0;
    private Vector3 lastPos;
    private bool isRunning = false;

    private enum State { Walking, Paused }
    private State state = State.Walking;

    // ─────────────────────────────────────────────────────
    void Start()
    {
        SetupCharacterController();
        // Wait one frame: prefab children aren't always ready in the same frame as Instantiate
        StartCoroutine(SetupAnimationAfterOneFrame());
        lastPos = transform.position;
        currentSpeed = walkSpeed;
        PickNewTarget();
    }

    void SetupCharacterController()
    {
        cc = GetComponent<CharacterController>();
        if (cc != null) return;
        cc = gameObject.AddComponent<CharacterController>();
        cc.height = 1.8f;
        cc.radius = 0.25f;
        cc.center = new Vector3(0f, 0.9f, 0f);
    }

    IEnumerator SetupAnimationAfterOneFrame()
    {
        yield return null;

        // ── Validate clip ─────────────────────────────────
        if (walkClip == null)
        {
            Debug.LogError($"[NPCWalker] {name}: No Walk Clip assigned on NPCSpawner!");
            yield break;
        }

        // ── Find Animator ─────────────────────────────────
        anim = GetComponentInChildren<Animator>(includeInactive: true);
        if (anim == null)
        {
            Debug.LogError($"[NPCWalker] {name}: No Animator component found in prefab hierarchy.");
            yield break;
        }

        // Enable the Animator but clear any controller so Mecanim
        // doesn't try to evaluate states — we'll drive it via Playables.
        anim.enabled = true;
        anim.runtimeAnimatorController = null;
        anim.applyRootMotion = false;

        // ── Build the Playable graph ──────────────────────
        //
        //  Graph layout:
        //
        //   AnimationClipPlayable(walk) ─┐
        //                                ├─► AnimationMixerPlayable ─► AnimationPlayableOutput
        //   AnimationClipPlayable(run)  ─┘
        //
        //  Blending: walk weight = (1 - t),  run weight = t,  where t ∈ [0,1]

        hasRunClip = (runClip != null);
        AnimationClip effectiveRun = hasRunClip ? runClip : walkClip;

        // Cache lengths for manual loop fallback in Update
        walkClipLength = walkClip.length;
        runClipLength = effectiveRun.length;

        // Clips must NOT be marked legacy when used with Animator/Playables
        walkClip.legacy = false;
        effectiveRun.legacy = false;

        // NOTE: wrapMode set at runtime is NOT reliably respected by the
        // Playables API — enable "Loop Time" in the FBX import settings instead.
        // These lines are left here as a best-effort fallback only.
        walkClip.wrapMode = WrapMode.Loop;
        effectiveRun.wrapMode = WrapMode.Loop;

        graph = PlayableGraph.Create($"NPC_Graph_{name}");
        graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

        // Two-input mixer
        mixer = AnimationMixerPlayable.Create(graph, 2);
        walkPlayable = AnimationClipPlayable.Create(graph, walkClip);
        runPlayable = AnimationClipPlayable.Create(graph, effectiveRun);

        // ── FIX: Do NOT call SetDuration(double.MaxValue) ────────────────
        // Doing so prevents the Playables system from looping the clip
        // correctly, causing it to freeze on the last frame.
        // Looping is handled by "Loop Time" in import settings +
        // the manual fallback in ManuallyLoopPlayables() below.
        // ─────────────────────────────────────────────────────────────────

        // Stagger start time so NPCs aren't all in sync
        walkPlayable.SetTime(Random.Range(0f, walkClipLength));
        runPlayable.SetTime(Random.Range(0f, runClipLength));

        graph.Connect(walkPlayable, 0, mixer, 0);
        graph.Connect(runPlayable, 0, mixer, 1);

        // Start fully on walk
        mixer.SetInputWeight(0, 1f);
        mixer.SetInputWeight(1, 0f);

        // Wire mixer to the Animator output
        var output = AnimationPlayableOutput.Create(graph, "Animation", anim);
        output.SetSourcePlayable(mixer);

        graph.Play();
        graphReady = true;

        Debug.Log($"[NPCWalker] {name}: Playable graph started. " +
                  $"Walk='{walkClip.name}' ({walkClipLength:F2}s) | " +
                  $"Run='{effectiveRun.name}' ({runClipLength:F2}s) | " +
                  $"HasRunClip={hasRunClip}");
    }

    void OnDestroy()
    {
        if (graph.IsValid()) graph.Destroy();
    }

    // ── Unity loop ────────────────────────────────────────

    void Update()
    {
        ApplyGravity();
        ManuallyLoopPlayables(); // safety net if "Loop Time" isn't set in import settings
        switch (state)
        {
            case State.Walking: UpdateWalking(); break;
            case State.Paused: UpdatePaused(); break;
        }
    }

    // ── Manual loop fallback ──────────────────────────────
    // If the clips don't have "Loop Time" checked in their FBX import
    // settings, the Playables API will freeze on the last frame.
    // This wraps the playable time back to the start when the clip ends.
    void ManuallyLoopPlayables()
    {
        if (!graphReady) return;

        if (walkClipLength > 0f)
        {
            double wt = walkPlayable.GetTime();
            if (wt >= walkClipLength)
                walkPlayable.SetTime(wt % walkClipLength);
        }

        if (runClipLength > 0f)
        {
            double rt = runPlayable.GetTime();
            if (rt >= runClipLength)
                runPlayable.SetTime(rt % runClipLength);
        }
    }

    void ApplyGravity()
    {
        if (cc == null) return;
        yVelocity = cc.isGrounded ? -1f : yVelocity + Physics.gravity.y * Time.deltaTime;
    }

    void UpdateWalking()
    {
        if (waypoints == null || waypoints.Count == 0) return;

        Vector3 flatSelf = Flat(transform.position);
        Vector3 flatTarget = Flat(target);
        float dist = Vector3.Distance(flatSelf, flatTarget);

        if (dist < reachRadius)
        {
            BlendTo(false);
            state = State.Paused;
            pauseTimer = Random.Range(pauseMin, pauseMax);
            return;
        }

        Vector3 dir = (flatTarget - flatSelf).normalized;
        if (dir.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.Slerp(
                transform.rotation,
                Quaternion.LookRotation(dir, Vector3.up),
                8f * Time.deltaTime);

        cc?.Move((dir * currentSpeed + Vector3.up * yVelocity) * Time.deltaTime);
        BlendTo(isRunning);

        if (Vector3.Distance(transform.position, lastPos) < 0.02f)
        {
            if (++stuckFrames > 60) { PickNewTarget(); stuckFrames = 0; }
        }
        else stuckFrames = 0;

        lastPos = transform.position;
    }

    void UpdatePaused()
    {
        cc?.Move(new Vector3(0f, yVelocity, 0f) * Time.deltaTime);
        pauseTimer -= Time.deltaTime;
        if (pauseTimer <= 0f) { PickNewTarget(); state = State.Walking; }
    }

    void PickNewTarget()
    {
        var pool = (sidewalkWaypoints != null && sidewalkWaypoints.Count > 1
                    && Random.value < 0.8f) ? sidewalkWaypoints : waypoints;
        if (pool == null || pool.Count == 0) return;

        isRunning = Random.value < 0.15f;
        currentSpeed = isRunning ? runSpeed : walkSpeed;

        for (int i = 0; i < 8; i++)
        {
            Vector3 c = pool[Random.Range(0, pool.Count)];
            if (Vector3.Distance(c, transform.position) > reachRadius * 2f)
            { target = c; return; }
        }
        target = pool[Random.Range(0, pool.Count)];
    }

    // ── Animation blending ────────────────────────────────

    void BlendTo(bool running)
    {
        if (!graphReady) return;

        float targetWeight = running ? 1f : 0f;
        float current = mixer.GetInputWeight(1);
        float blended = Mathf.MoveTowards(current, targetWeight, 4f * Time.deltaTime);

        mixer.SetInputWeight(0, 1f - blended);
        mixer.SetInputWeight(1, blended);

        // If no separate run clip, speed up the walk clip instead
        if (!hasRunClip)
        {
            float speed = running ? (runSpeed / Mathf.Max(walkSpeed, 0.01f)) : 1f;
            walkPlayable.SetSpeed(speed);
        }
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, target);
        Gizmos.DrawWireSphere(target, reachRadius);
    }
}