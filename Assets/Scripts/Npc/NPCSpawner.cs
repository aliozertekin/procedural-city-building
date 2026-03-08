using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// =========================================================
//  NPC SPAWNER
//  Add this component to any GameObject in the scene.
//  Requires NPCPrefabEntry.cs and NPCWalker.cs in the project.
//
//  NPCs spawn automatically when CityGenerator fires
//  OnCityGenerated. If the city was already generated before
//  this component starts, they spawn after spawnDelay instead.
// =========================================================
public class NPCSpawner : MonoBehaviour
{
    [Header("References")]
    public CityGenerator cityGenerator;

    [Header("Spawn Settings")]
    [Tooltip("How many NPCs to spawn total")]
    public int npcCount = 20;
    [Tooltip("Extra seconds to wait after city generation before spawning " +
             "(lets physics settle)")]
    public float spawnDelay = 0.5f;

    [Header("NPC Models")]
    [Tooltip("Add as many prefabs as you want with individual spawn weights.\n" +
             "Leave empty to use the built-in capsule placeholder.")]
    public List<NPCPrefabEntry> npcPrefabs = new List<NPCPrefabEntry>();
    [Tooltip("Height used only for the capsule fallback when no prefab is assigned")]
    public float fallbackNpcHeight = 1.8f;

    [Header("NPC Behaviour")]
    public float walkSpeed = 2f;
    public float runSpeed = 5f;
    public float waypointRadius = 2f;
    public float pauseMin = 1f;
    public float pauseMax = 4f;

    [Header("Animation Parameter Names")]
    [Tooltip("Float BlendTree param: 0=idle, 1=walk, 2=run. Leave blank to ignore.")]
    public string walkSpeedParam = "WalkSpeed";
    [Tooltip("Bool param set true while walking (non-running). Leave blank to ignore.")]
    public string isWalkingParam = "IsWalking";
    [Tooltip("Bool param set true while running. Leave blank to ignore.")]
    public string isRunningParam = "IsRunning";

    private readonly List<GameObject> spawnedNPCs = new List<GameObject>();
    private readonly List<GameObject> weightedTable = new List<GameObject>();
    private bool cityReady = false;

    // ─────────────────────────────────────────
    void Start()
    {
        if (cityGenerator == null)
            cityGenerator = FindFirstObjectByType<CityGenerator>();

        if (cityGenerator == null)
        {
            Debug.LogWarning("NPCSpawner: No CityGenerator found in scene.");
            return;
        }

        // Subscribe to generation event
        cityGenerator.OnCityGenerated += OnCityReady;

        // If city already has waypoints (generated before this script started),
        // spawn immediately after a short settle delay
        if (cityGenerator.pedestrianWaypoints != null &&
            cityGenerator.pedestrianWaypoints.Count > 1)
        {
            StartCoroutine(DelayedSpawn());
        }
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

    void BuildWeightedTable()
    {
        weightedTable.Clear();
        foreach (var entry in npcPrefabs)
        {
            if (entry == null || entry.prefab == null) continue;
            int w = Mathf.Max(1, entry.weight);
            for (int i = 0; i < w; i++)
                weightedTable.Add(entry.prefab);
        }
    }

    GameObject PickPrefab() =>
        weightedTable.Count == 0 ? null : weightedTable[Random.Range(0, weightedTable.Count)];

    public void SpawnAll()
    {
        foreach (var n in spawnedNPCs)
            if (n != null) Destroy(n);
        spawnedNPCs.Clear();

        var waypoints = cityGenerator?.pedestrianWaypoints;
        if (waypoints == null || waypoints.Count < 2)
        {
            Debug.LogWarning("NPCSpawner: No pedestrian waypoints found. " +
                             "Make sure 'Generate Sidewalks' is enabled on CityGenerator.");
            return;
        }

        for (int i = 0; i < npcCount; i++)
        {
            Vector3 startPos = waypoints[Random.Range(0, waypoints.Count)];

            GameObject prefab = PickPrefab();
            GameObject npc = prefab != null
                ? Instantiate(prefab, startPos, Quaternion.identity, transform)
                : BuildCapsuleNPC(startPos);

            npc.name = $"NPC_{i}";

            var walker = npc.AddComponent<NPCWalker>();
            walker.waypoints = waypoints;
            walker.sidewalkWaypoints = cityGenerator.sidewalkWaypoints.Count > 0
                                       ? cityGenerator.sidewalkWaypoints : waypoints;
            walker.walkSpeed = walkSpeed + Random.Range(-0.4f, 0.4f);
            walker.runSpeed = runSpeed;
            walker.reachRadius = waypointRadius;
            walker.pauseMin = pauseMin;
            walker.pauseMax = pauseMax;
            walker.walkSpeedParam = walkSpeedParam;
            walker.isWalkingParam = isWalkingParam;
            walker.isRunningParam = isRunningParam;

            spawnedNPCs.Add(npc);
        }

        Debug.Log($"NPCSpawner: Spawned {spawnedNPCs.Count} NPCs " +
                  $"across {waypoints.Count} waypoints.");
    }

    GameObject BuildCapsuleNPC(Vector3 pos)
    {
        var root = new GameObject("NPCCapsule");
        root.transform.position = pos;

        var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
        body.name = "Body";
        body.transform.SetParent(root.transform, false);
        body.transform.localPosition = new Vector3(0, fallbackNpcHeight * 0.5f, 0);
        body.transform.localScale = new Vector3(0.5f, fallbackNpcHeight * 0.5f, 0.5f);
        var bodyMr = body.GetComponent<MeshRenderer>();
        if (bodyMr != null)
        {
            var mat = new Material(FindShader());
            mat.color = new Color(Random.value, Random.value, Random.value);
            bodyMr.sharedMaterial = mat;
        }

        var head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        head.name = "Head";
        head.transform.SetParent(root.transform, false);
        head.transform.localPosition = new Vector3(0, fallbackNpcHeight + 0.15f, 0);
        head.transform.localScale = Vector3.one * 0.35f;
        var headMr = head.GetComponent<MeshRenderer>();
        if (headMr != null)
        {
            var mat = new Material(FindShader());
            mat.color = new Color(0.9f, 0.75f, 0.6f);
            headMr.sharedMaterial = mat;
        }

        Destroy(body.GetComponent<Collider>());
        Destroy(head.GetComponent<Collider>());

        var cc = root.AddComponent<CharacterController>();
        cc.height = fallbackNpcHeight;
        cc.radius = 0.25f;
        cc.center = new Vector3(0, fallbackNpcHeight * 0.5f, 0);

        return root;
    }

    Shader FindShader() =>
        Shader.Find("Universal Render Pipeline/Lit")
        ?? Shader.Find("HDRP/Lit")
        ?? Shader.Find("Standard");
}