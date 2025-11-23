using System;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class CityGenerator : MonoBehaviour
{
    [Header("Terrain")]
    public TerrainGenerator terrainGenerator;
    private Terrain terrain;

    [Header("Common")]
    public int seed = 12345;
    public Vector2 citySize = new Vector2(200, 200);
    public GameObject roadPrefab;
    public GameObject roadblockPrefab;
    public Transform root;
    public Material roadMaterial;
    public float roadWidth = 2f;
    public bool generateOnStart = false;
    public float maxSlope = 30f;

    [Header("Grid Settings")]
    public float gridBlockSize = 20f;

    [Header("Population / Density")]
    [Range(0f, 1f)] public float centerDensity = 0.9f;
    [Range(0f, 1f)] public float edgeDensity = 0.6f;
    public float densityFalloffPower = 1.2f;
    [Range(0f, 1f)] public float densityNoiseStrength = 0.25f;
    public float densityNoiseScale = 0.01f;

    [Header("Roadblocks")]
    public int numRoadblocks = 10;
    public float roadBlockMinSpacing = 6f;
    public float roadBlockRadius = 2f;

    private System.Random rng;
    private List<GameObject> generated = new List<GameObject>();
    private int roadCount = 0;
    private int buildingCount = 0;

    private struct RoadSeg { public Vector3 a, b; public int id; public RoadSeg(Vector3 a, Vector3 b, int id) { this.a = a; this.b = b; this.id = id; } }
    private List<RoadSeg> roadSegments = new List<RoadSeg>();
    private Vector3 terrainCenterOffset;

    [System.Serializable]
    public struct RoadSegment { public Vector3 start; public Vector3 end; public int id; }
    public List<RoadSegment> GetRoadSegments()
    {
        List<RoadSegment> segments = new List<RoadSegment>();
        foreach (var seg in roadSegments)
            segments.Add(new RoadSegment { start = seg.a, end = seg.b, id = seg.id });
        return segments;
    }

    [System.Serializable]
    public class RoadBlock
    {
        public Vector3 position;
        public float radius;
        public int segmentId;
        public GameObject instance;
    }

    [Header("Runtime Roadblocks (read-only)")]
    public List<RoadBlock> roadBlocks = new List<RoadBlock>();

    public Vector3 GetTerrainCenter() => terrainCenterOffset;
    public float GetRoadWidth() => roadWidth;

    [Header("Runtime Building Prefabs")]
    public List<GameObject> buildingPrefabs = new List<GameObject>();

    private void Awake() { LoadBuildingPrefabs(); }
    private void Start() { if (generateOnStart) GenerateCity(); }

    private void LoadBuildingPrefabs()
    {
        buildingPrefabs.Clear();
        GameObject[] prefabs = Resources.LoadAll<GameObject>("BuildingsLow");
        buildingPrefabs.AddRange(prefabs);
        Debug.Log($"CityGenerator: Loaded {buildingPrefabs.Count} building prefabs from Resources.");
    }

    [ContextMenu("Generate City")]
    public void GenerateCity()
    {
        if (terrainGenerator == null || terrainGenerator.GetComponent<Terrain>() == null)
        {
            Debug.LogError("TerrainGenerator missing or no terrain found!");
            return;
        }

        terrain = terrainGenerator.GetComponent<Terrain>();
        terrainCenterOffset = terrain.GetPosition() + terrain.terrainData.size * 0.5f;

        ClearPrevious();
        rng = new System.Random(seed);
        if (root == null) root = this.transform;
        if (roadMaterial == null) CreateDefaultRoadMaterial();

        roadCount = 0;
        buildingCount = 0;
        roadSegments.Clear();

        Debug.Log($"CityGenerator: Starting grid generation seed={seed} size={citySize}");
        GenerateGridCity();

        if (roadblockPrefab != null && numRoadblocks > 0)
        {
            GenerateRoadblocks(numRoadblocks);
            Debug.Log($"CityGenerator: Roadblocks generated: {roadBlocks.Count}");
        }

        Debug.Log($"CityGenerator: Finished. Roads={roadCount} Buildings={buildingCount}");
    }

    [ContextMenu("Clear City")]
    public void ClearPrevious()
    {
        for (int i = generated.Count - 1; i >= 0; i--)
        {
            var g = generated[i];
            if (g != null)
#if UNITY_EDITOR
                DestroyImmediate(g);
#else
                Destroy(g);
#endif
        }
        generated.Clear();
        roadSegments.Clear();

        foreach (var rb in roadBlocks)
        {
            if (rb.instance != null)
#if UNITY_EDITOR
                DestroyImmediate(rb.instance);
#else
                Destroy(rb.instance);
#endif
        }
        roadBlocks.Clear();
    }

    private void CreateDefaultRoadMaterial()
    {
        Shader shader = Shader.Find("Standard");
        if (shader != null)
        {
            roadMaterial = new Material(shader);
            roadMaterial.color = Color.gray * 0.4f;
        }
    }

    private Vector3 ToTerrainCentered(Vector3 local) => terrainCenterOffset + local;
    private float SampleTerrainHeight(Vector3 worldPos) => terrain.SampleHeight(worldPos) + terrain.GetPosition().y;

    // -------------------------------
    // ROAD CREATION
    // -------------------------------

    private GameObject CreateRoadObject(Vector3 a, Vector3 b, float width)
    {
        a.y = SampleTerrainHeight(a) + 0.5f;
        b.y = SampleTerrainHeight(b) + 0.5f;
        if (Vector3.Distance(a, b) < 0.01f) return null;

        GameObject road = roadPrefab != null ? Instantiate(roadPrefab, (a + b) * 0.5f, Quaternion.identity, root)
                                           : GameObject.CreatePrimitive(PrimitiveType.Cube);
        road.name = $"Road_{roadCount}";
        generated.Add(road);

        if (roadPrefab == null)
        {
            var col = road.GetComponent<Collider>();
            if (col != null)
#if UNITY_EDITOR
                DestroyImmediate(col);
#else
                Destroy(col);
#endif
        }

        road.transform.position = (a + b) * 0.5f;
        Vector3 delta = b - a;

        road.transform.rotation = Quaternion.LookRotation(delta.normalized, Vector3.up);
        road.transform.Rotate(-180f, 0f, 0f);

        road.transform.localScale = new Vector3(width * 0.25f, 0.01f, delta.magnitude * 0.115f);

        var mr = road.GetComponent<MeshRenderer>();
        if (mr != null && roadMaterial != null) mr.sharedMaterial = roadMaterial;

        roadCount++;
        return road;
    }

    private void CreateCurvedRoad(Vector3 start, Vector3 end, float width, int segments = 8)
    {
        List<Vector3> points = new List<Vector3>();
        for (int i = 0; i <= segments; i++)
        {
            float t = i / (float)segments;
            Vector3 p = Vector3.Lerp(start, end, t);
            p.y = SampleTerrainHeight(p) + 2.5f;
            points.Add(p);
        }

        for (int i = 0; i < points.Count - 1; i++)
        {
            CreateRoadObject(points[i], points[i + 1], width);
            roadSegments.Add(new RoadSeg(points[i], points[i + 1], roadCount - 1));
        }
    }

    // -------------------------------
    // BUILDINGS
    // -------------------------------

    private float PopulationDensity(Vector3 worldPos)
    {
        Vector2 flatPos = new Vector2(worldPos.x - terrainCenterOffset.x, worldPos.z - terrainCenterOffset.z);
        float dist = flatPos.magnitude;
        float maxDist = Mathf.Min(citySize.x, citySize.y) * 0.5f;
        float t = Mathf.Clamp01(dist / maxDist);
        float baseDensity = Mathf.Lerp(centerDensity, edgeDensity, Mathf.Pow(Mathf.Max(t, 0.0001f), densityFalloffPower));
        float nx = (worldPos.x + seed * 13) * densityNoiseScale;
        float nz = (worldPos.z + seed * 79) * densityNoiseScale;
        float noise = Mathf.PerlinNoise(nx, nz) * 2f - 1f;
        return Mathf.Clamp01(baseDensity + noise * densityNoiseStrength);
    }

    private GameObject CreateBuilding(Vector3 pos, Quaternion? rotation = null)
    {
        int maxRetries = 5;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            pos.y = SampleTerrainHeight(pos);

            float density = PopulationDensity(pos);
            if (rng.NextDouble() > density) return null;

            List<GameObject> filtered = new List<GameObject>();
            foreach (var prefab in buildingPrefabs)
            {
                if (density > 0.7f && prefab.name.Contains("9et")) filtered.Add(prefab);
                else if (density > 0.5f && prefab.name.Contains("5et")) filtered.Add(prefab);
                else if (density > 0.3f && prefab.name.Contains("4et")) filtered.Add(prefab);
                else if (density <= 0.3f && prefab.name.Contains("2et")) filtered.Add(prefab);
            }
            if (filtered.Count == 0) filtered.AddRange(buildingPrefabs);

            GameObject sel = filtered[rng.Next(filtered.Count)];
            GameObject b = Instantiate(sel, pos, rotation ?? Quaternion.identity, root);

            float scaleFactor = RandomRange(0.8f, 1f);
            b.transform.localScale *= scaleFactor;

            if (rotation == null)
            {
                float yRot = rng.Next(0, 4) * 90f;
                b.transform.rotation = Quaternion.Euler(0, yRot, 0);
            }

            Bounds bBounds = GetBounds(b);

            if (!IsInsideTerrain(bBounds) ||
                IsCollidingRoad(bBounds) ||
                IsCollidingBuildings(bBounds))
            {
                DestroyImmediate(b);
                pos.x += RandomRange(-gridBlockSize * 0.3f, gridBlockSize * 0.3f);
                pos.z += RandomRange(-gridBlockSize * 0.3f, gridBlockSize * 0.3f);
                continue;
            }

            b.name = $"Building_{buildingCount}";
            generated.Add(b);
            buildingCount++;
            return b;
        }

        return null;
    }

    private Bounds GetBounds(GameObject obj)
    {
        Renderer rend = obj.GetComponentInChildren<Renderer>();
        if (rend != null) return rend.bounds;
        return new Bounds(obj.transform.position, Vector3.one);
    }

    private bool IsInsideTerrain(Bounds bounds)
    {
        Vector3 terrainMin = terrain.GetPosition();
        Vector3 terrainMax = terrainMin + terrain.terrainData.size;
        return (bounds.min.x >= terrainMin.x && bounds.max.x <= terrainMax.x &&
                bounds.min.z >= terrainMin.z && bounds.max.z <= terrainMax.z);
    }

    private bool IsCollidingRoad(Bounds bBounds)
    {
        float margin = 0.5f;
        foreach (var seg in roadSegments)
        {
            Vector3 closest = ClosestPointOnSegment(seg.a, seg.b, bBounds.center);
            if (Vector3.Distance(closest, bBounds.center) < roadWidth * 0.5f + margin + bBounds.extents.magnitude)
                return true;
        }
        return false;
    }

    private bool IsCollidingBuildings(Bounds bBounds)
    {
        foreach (var other in generated)
        {
            if (!other.CompareTag("Building")) continue;
            Bounds oBounds = GetBounds(other);
            if (oBounds.Intersects(bBounds)) return true;
        }
        return false;
    }

    private Vector3 ClosestPointOnSegment(Vector3 a, Vector3 b, Vector3 point)
    {
        Vector3 ab = b - a;
        float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / ab.sqrMagnitude);
        return a + ab * t;
    }

    private float RandomRange(float a, float b) => (float)(rng.NextDouble() * (b - a) + a);

    // -------------------------------
    // ROADBLOCK GENERATION (UPDATED)
    // -------------------------------

    private void GenerateRoadblocks(int count)
    {
        roadBlocks.Clear();
        if (roadSegments.Count == 0 || roadblockPrefab == null) return;

        int placed = 0, attempts = 0;
        int maxAttempts = Mathf.Max(200, count * 10);

        while (placed < count && attempts < maxAttempts)
        {
            attempts++;

            var seg = roadSegments[rng.Next(roadSegments.Count)];

            // Midpoint of segment
            Vector3 mid = (seg.a + seg.b) * 0.5f;

            // Raycast: get exact road height + normal
            RaycastHit hit;
            Vector3 rayStart = mid + Vector3.up * 10f;

            bool hitRoad = Physics.Raycast(
                rayStart,
                Vector3.down,
                out hit,
                40f,
                LayerMask.GetMask("Road")
            );

            Vector3 finalPos = mid;
            Quaternion finalRot;

            if (hitRoad)
            {
                finalPos = hit.point;

                // Road forward direction
                Vector3 forward = (seg.b - seg.a).normalized;

                // Look forward, standing on road slope
                finalRot = Quaternion.LookRotation(forward, hit.normal);

                // Apply Y -90° rotation offset
                finalRot *= Quaternion.Euler(0f, -90f, 0f);
            }
            else
            {
                // Fallback flat case
                finalPos.y = SampleTerrainHeight(mid);

                // Rotate forward then -90° Y
                finalRot = Quaternion.LookRotation(seg.b - seg.a) * Quaternion.Euler(0f, -90f, 0f);
            }

            // Slight lift
            finalPos += Vector3.up * 0.75f;

            // Instantiate, keep original scale
            GameObject go = Instantiate(roadblockPrefab, finalPos, finalRot, root);
            go.name = $"RoadBlock_{placed}";

            roadBlocks.Add(new RoadBlock
            {
                position = finalPos,
                radius = roadBlockRadius,
                segmentId = seg.id,
                instance = go
            });

            placed++;
        }
    }


    // -------------------------------
    // GRID GENERATION
    // -------------------------------

    private void GenerateGridCity()
    {
        int cols = Mathf.CeilToInt(citySize.x / gridBlockSize);
        int rows = Mathf.CeilToInt(citySize.y / gridBlockSize);
        float halfX = citySize.x * 0.5f;
        float halfY = citySize.y * 0.5f;

        Vector3[,] gridPoints = new Vector3[cols + 1, rows + 1];
        for (int i = 0; i <= cols; i++)
            for (int j = 0; j <= rows; j++)
            {
                Vector3 local = new Vector3(-halfX + i * gridBlockSize, 0, -halfY + j * gridBlockSize);
                Vector3 world = ToTerrainCentered(local);
                world.y = SampleTerrainHeight(world);
                gridPoints[i, j] = world;
            }

        for (int i = 0; i <= cols; i++)
        {
            int j = 0;
            while (j < rows)
            {
                int segmentLength = rng.Next(1, 4);
                int endJ = Mathf.Min(j + segmentLength, rows);

                for (int k = j; k < endJ; k++)
                    CreateCurvedRoad(gridPoints[i, k], gridPoints[i, k + 1], roadWidth);

                j = endJ + rng.Next(0, 2);
            }
        }

        for (int j = 0; j <= rows; j++)
        {
            int i = 0;
            while (i < cols)
            {
                int segmentLength = rng.Next(1, 4);
                int endI = Mathf.Min(i + segmentLength, cols);

                for (int k = i; k < endI; k++)
                    CreateCurvedRoad(gridPoints[k, j], gridPoints[k + 1, j], roadWidth);

                i = endI + rng.Next(0, 2);
            }
        }

        for (int i = 0; i < cols; i++)
            for (int j = 0; j < rows; j++)
            {
                Vector3 center = (gridPoints[i, j] + gridPoints[i + 1, j + 1]) * 0.5f;

                if (rng.NextDouble() > (0.3 + 0.7 * PopulationDensity(center)))
                    continue;

                float margin = (roadWidth + 2f) * 0.5f;
                Vector3 blockMin = gridPoints[i, j] + new Vector3(margin, 0, margin);
                Vector3 blockMax = gridPoints[i + 1, j + 1] - new Vector3(margin, 0, margin);

                for (int attempt = 0; attempt < 5; attempt++)
                {
                    Vector3 randomPos = new Vector3(
                        RandomRange(blockMin.x, blockMax.x),
                        0,
                        RandomRange(blockMin.z, blockMax.z));

                    randomPos.y = SampleTerrainHeight(randomPos);

                    if (CreateBuilding(randomPos) != null)
                        break;
                }
            }
    }
}
