using System.Collections.Generic;
using UnityEngine;

// =========================================================
//  NPC WALKER
//  Automatically added to each NPC by NPCSpawner.
//
//  ANIMATION SETUP:
//  If your NPC prefab has an Animator, set the parameter
//  names below to match your Animator Controller:
//
//    walkSpeedParam  — Float: set to current move speed
//                     (use this to blend walk/run in a
//                      BlendTree, OR leave blank and use
//                      the bool params below)
//    isWalkingParam  — Bool: true while walking
//    isRunningParam  — Bool: true while running
//    idleParam       — Trigger or Bool: fired when pausing
//
//  In NPCSpawner Inspector you can override these names.
//  The simplest setup is a BlendTree on walkSpeedParam:
//    0   = Idle
//    1   = Walk  (your walk animation)
//    2   = Run   (your run animation)
// =========================================================
public class NPCWalker : MonoBehaviour
{
    [HideInInspector] public List<Vector3> waypoints;
    [HideInInspector] public List<Vector3> sidewalkWaypoints;
    [HideInInspector] public float walkSpeed = 2f;
    [HideInInspector] public float runSpeed = 5f;
    [HideInInspector] public float reachRadius = 2f;
    [HideInInspector] public float pauseMin = 1f;
    [HideInInspector] public float pauseMax = 4f;

    // ── Animation parameter names (set by NPCSpawner) ──
    [HideInInspector] public string walkSpeedParam = "WalkSpeed";
    [HideInInspector] public string isWalkingParam = "IsWalking";
    [HideInInspector] public string isRunningParam = "IsRunning";

    private CharacterController cc;
    private Animator anim;
    private Vector3 target;
    private float currentSpeed;
    private float yVelocity = 0f;
    private float pauseTimer = 0f;
    private int stuckFrames = 0;
    private Vector3 lastPos;
    private bool isRunning = false;

    private enum State { Walking, Paused }
    private State state = State.Walking;

    // ─────────────────────────────────────────
    void Start()
    {
        cc = GetComponent<CharacterController>();
        if (cc == null)
        {
            cc = gameObject.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.25f;
            cc.center = new Vector3(0, 0.9f, 0);
        }

        anim = GetComponentInChildren<Animator>();
        CacheAnimParams();

        lastPos = transform.position;
        currentSpeed = walkSpeed;
        PickNewTarget();
    }

    // Build a HashSet of param names that actually exist so we never
    // call SetFloat/SetBool on a missing parameter (causes log spam)
    private System.Collections.Generic.HashSet<string> _animParams
        = new System.Collections.Generic.HashSet<string>();

    void CacheAnimParams()
    {
        _animParams.Clear();
        if (anim == null) return;
        foreach (var p in anim.parameters)
            _animParams.Add(p.name);
    }

    void Update()
    {
        ApplyGravity();

        switch (state)
        {
            case State.Walking: UpdateWalking(); break;
            case State.Paused: UpdatePaused(); break;
        }
    }

    // ── Gravity ─────────────────────────────
    void ApplyGravity()
    {
        if (cc == null) return;
        yVelocity = cc.isGrounded ? -1f : yVelocity + (-12f * Time.deltaTime);
    }

    // ── Walking ─────────────────────────────
    void UpdateWalking()
    {
        if (waypoints == null || waypoints.Count == 0) return;

        Vector3 flatSelf = Flat(transform.position);
        Vector3 flatTarget = Flat(target);
        float dist = Vector3.Distance(flatSelf, flatTarget);

        if (dist < reachRadius)
        {
            SetAnimIdle();
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

        Vector3 move = dir * currentSpeed;
        move.y = yVelocity;
        cc?.Move(move * Time.deltaTime);

        SetAnimMoving(currentSpeed);

        // Stuck detection
        if (Vector3.Distance(transform.position, lastPos) < 0.02f)
        {
            stuckFrames++;
            if (stuckFrames > 60) { PickNewTarget(); stuckFrames = 0; }
        }
        else stuckFrames = 0;

        lastPos = transform.position;
    }

    // ── Paused ──────────────────────────────
    void UpdatePaused()
    {
        cc?.Move(new Vector3(0, yVelocity, 0) * Time.deltaTime);
        pauseTimer -= Time.deltaTime;
        if (pauseTimer <= 0f)
        {
            PickNewTarget();
            state = State.Walking;
        }
    }

    // ── Target selection ────────────────────
    void PickNewTarget()
    {
        List<Vector3> pool = (sidewalkWaypoints != null && sidewalkWaypoints.Count > 1
                              && Random.value < 0.8f)
                             ? sidewalkWaypoints : waypoints;

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

    // ── Animation helpers ───────────────────
    void SetAnimMoving(float speed)
    {
        if (anim == null) return;

        // Float BlendTree param (0=idle, 1=walk, 2=run)
        if (!string.IsNullOrEmpty(walkSpeedParam))
        {
            float normSpeed = isRunning
                ? Mathf.InverseLerp(0f, runSpeed, speed) * 2f   // maps to 2
                : Mathf.InverseLerp(0f, walkSpeed, speed);       // maps to 1
            SafeSetFloat(walkSpeedParam, normSpeed);
        }

        // Bool params
        SafeSetBool(isWalkingParam, !isRunning);
        SafeSetBool(isRunningParam, isRunning);
    }

    void SetAnimIdle()
    {
        if (anim == null) return;
        SafeSetFloat(walkSpeedParam, 0f);
        SafeSetBool(isWalkingParam, false);
        SafeSetBool(isRunningParam, false);
    }

    // Safe setters — only call if parameter actually exists in the Animator
    void SafeSetFloat(string name, float val)
    {
        if (anim == null || string.IsNullOrEmpty(name)) return;
        if (_animParams.Contains(name)) anim.SetFloat(name, val);
    }

    void SafeSetBool(string name, bool val)
    {
        if (anim == null || string.IsNullOrEmpty(name)) return;
        if (_animParams.Contains(name)) anim.SetBool(name, val);
    }

    static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0, v.z);

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, target);
        Gizmos.DrawWireSphere(target, reachRadius);
    }
}