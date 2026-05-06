using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// =========================================================
//  NPC SPAWNER  (Animation-Clip edition)
//  ─────────────────────────────────────────────────────────
//  HOW TO USE IN THE UNITY INSPECTOR
//  ─────────────────────────────────────────────────────────
//  1. Attach this script to an empty GameObject in your scene.
//  2. Assign your CityGenerator reference (or leave blank to
//     auto-find it).
//  3. Under "Animation Clips":
//       • Walk Clip  — drag your walk AnimationClip here.
//       • Run Clip   — drag your run clip here, OR leave
//                      blank to reuse the walk clip sped up.
//  4. Under "NPC Models", add your prefabs with weights.
//     Leave the list empty to use a coloured capsule fallback.
//  5. Tweak Spawn Settings and NPC Behaviour as needed.
//
//  IMPORTANT – Legacy clip flag:
//    Select each AnimationClip in the Project window.
//    In the Inspector tick "Legacy" if it isn't already.
//    FBX sub-clips work automatically without this step.
// =========================================================

public class NPCSpawner : MonoBehaviour
{
    [Header("References")]
    public CityGenerator cityGenerator;

    [Header("Spawn Settings")]
    [Tooltip("Total number of NPCs to spawn")]
    public int npcCount = 20;
    [Tooltip("Seconds to wait after city generation before spawning")]
    public float spawnDelay = 0.5f;

    [Header("NPC Models")]
    [Tooltip("Add your NPC prefabs here with individual spawn weights.\n" +
             "Leave empty to use a coloured capsule placeholder.")]
    public List<NPCPrefabEntry> npcPrefabs = new List<NPCPrefabEntry>();
    [Tooltip("Height of the capsule fallback (used only when no prefab is assigned)")]
    public float fallbackNpcHeight = 1.8f;

    [Header("Animation Clips")]
    [Tooltip("Drag your Walk animation clip here.\n" +
             "All NPCs will share this single clip.\n\n" +
             "Make sure the clip has 'Legacy' ticked in the Import Settings\n" +
             "(select it in the Project window → Inspector → tick Legacy).")]
    public AnimationClip walkClip;

    [Tooltip("Drag your Run animation clip here (optional).\n" +
             "If left empty the Walk clip is played at a higher speed\n" +
             "whenever an NPC decides to run.")]
    public AnimationClip runClip;

    [Header("NPC Behaviour")]
    public float walkSpeed = 2f;
    public float runSpeed = 5f;
    public float waypointRadius = 2f;
    public float pauseMin = 1f;
    public float pauseMax = 4f;

    // ── private ──────────────────────────────────────────
    private readonly List<GameObject> spawnedNPCs = new List<GameObject>();
    private readonly List<GameObject> weightedTable = new List<GameObject>();

    /// <summary>Returns the list of currently spawned NPC GameObjects.</summary>
    public List<GameObject> GetSpawnedNPCs() => spawnedNPCs;

    /// <summary>
    /// Fired after all NPCs have been spawned and are ready in the scene.
    /// QuestSystem (or other systems) can subscribe to this event.
    /// </summary>
    public event System.Action OnNPCsSpawned;

    // ─────────────────────────────────────────────────────
    void Start()
    {
        if (cityGenerator == null)
            cityGenerator = FindFirstObjectByType<CityGenerator>();

        if (cityGenerator == null)
        {
            Debug.LogWarning("NPCSpawner: No CityGenerator found in scene.");
            return;
        }

        cityGenerator.OnCityGenerated += OnCityReady;

        // City may have already generated before this script started
        if (cityGenerator.pedestrianWaypoints != null &&
            cityGenerator.pedestrianWaypoints.Count > 1)
            StartCoroutine(DelayedSpawn());
    }

    void OnDestroy()
    {
        if (cityGenerator != null)
            cityGenerator.OnCityGenerated -= OnCityReady;
    }

    void OnCityReady()
    {
        StopAllCoroutines();
        StartCoroutine(DelayedSpawn());
    }

    IEnumerator DelayedSpawn()
    {
        yield return new WaitForSeconds(spawnDelay);
        BuildWeightedTable();
        SpawnAll();
    }

    // ── Weighted prefab table ─────────────────────────────

    void BuildWeightedTable()
    {
        weightedTable.Clear();
        foreach (var entry in npcPrefabs)
        {
            if (entry?.prefab == null) continue;
            int w = Mathf.Max(1, entry.weight);
            for (int i = 0; i < w; i++)
                weightedTable.Add(entry.prefab);
        }
    }

    GameObject PickPrefab() =>
        weightedTable.Count == 0
            ? null
            : weightedTable[Random.Range(0, weightedTable.Count)];

    // ── Spawn ─────────────────────────────────────────────

    public void SpawnAll()
    {
        // Destroy any previously spawned NPCs
        foreach (var n in spawnedNPCs) if (n) Destroy(n);
        spawnedNPCs.Clear();

        var waypoints = cityGenerator?.pedestrianWaypoints;
        if (waypoints == null || waypoints.Count < 2)
        {
            Debug.LogWarning("NPCSpawner: No pedestrian waypoints found.\n" +
                             "Enable 'Generate Sidewalks' on CityGenerator.");
            return;
        }

        var sidewalkWPs = (cityGenerator.sidewalkWaypoints?.Count > 0)
                          ? cityGenerator.sidewalkWaypoints
                          : waypoints;

        // Warn once if no animation clips have been assigned
        if (walkClip == null)
            Debug.LogWarning("NPCSpawner: No Walk Clip assigned — NPCs will " +
                             "move but play no animation.\n" +
                             "Drag an AnimationClip into the 'Walk Clip' slot.");

        for (int i = 0; i < npcCount; i++)
        {
            Vector3 spawnPt = waypoints[Random.Range(0, waypoints.Count)];
            GameObject prefab = PickPrefab();

            GameObject npc = prefab != null
                ? Instantiate(prefab, spawnPt, Quaternion.identity, transform)
                : BuildCapsuleNPC(spawnPt);

            npc.name = $"NPC_{i}";

            // ── Configure walker ──────────────────────────
            var walker = npc.AddComponent<NPCWalker>();
            walker.waypoints = waypoints;
            walker.sidewalkWaypoints = sidewalkWPs;
            walker.walkSpeed = walkSpeed + Random.Range(-0.4f, 0.4f);
            walker.runSpeed = runSpeed;
            walker.reachRadius = waypointRadius;
            walker.pauseMin = pauseMin;
            walker.pauseMax = pauseMax;

            // Hand the clips to the walker — it manages playback internally
            walker.walkClip = walkClip;
            walker.runClip = runClip;   // null is fine

            spawnedNPCs.Add(npc);
        }

        Debug.Log($"NPCSpawner: Spawned {spawnedNPCs.Count} NPCs " +
                  $"across {waypoints.Count} waypoints.");

        OnNPCsSpawned?.Invoke();
    }

    // ── Capsule fallback (no prefab assigned) ─────────────

    GameObject BuildCapsuleNPC(Vector3 pos)
    {
        var root = new GameObject("NPCCapsule");
        root.transform.position = pos;

        // Body
        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        body.transform.localPosition = new Vector3(0f, fallbackNpcHeight * 0.5f, 0f);
        body.transform.localScale = new Vector3(0.5f, fallbackNpcHeight * 0.5f, 0.5f);
        body.GetComponent<MeshRenderer>().sharedMaterial =
            new Material(FindShader()) { color = Random.ColorHSV(0f, 1f, 0.4f, 0.8f, 0.6f, 1f) };
        Destroy(body.GetComponent<Collider>());

        // Head
        var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        head.name = "Head";
        head.transform.SetParent(root.transform, false);
        head.transform.localPosition = new Vector3(0f, fallbackNpcHeight + 0.15f, 0f);
        head.transform.localScale = Vector3.one * 0.35f;
        head.GetComponent<MeshRenderer>().sharedMaterial =
            new Material(FindShader()) { color = new Color(0.9f, 0.75f, 0.6f) };
        Destroy(head.GetComponent<Collider>());

        // CharacterController
        var cc = root.AddComponent<CharacterController>();
        cc.height = fallbackNpcHeight;
        cc.radius = 0.25f;
        cc.center = new Vector3(0f, fallbackNpcHeight * 0.5f, 0f);

        return root;
    }

    Shader FindShader() =>
        Shader.Find("Universal Render Pipeline/Lit")
        ?? Shader.Find("HDRP/Lit")
        ?? Shader.Find("Standard");
}