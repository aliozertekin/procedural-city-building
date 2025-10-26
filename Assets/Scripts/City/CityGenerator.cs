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
    public GameObject buildingPrefab;
    public GameObject roadPrefab;
    public Transform root;
    public Material roadMaterial;
    public float roadWidth = 2f;
    public bool generateOnStart = false;
    public float maxSlope = 30f;

    [Header("Grid Settings")]
    public float gridBlockSize = 20f;

    [Header("Buildings")]
    public float buildingFootprintMin = 4f;
    public float buildingFootprintMax = 12f;
    public float buildingSpacing = 2.0f;
    public float buildingRoadSafetyMargin = 3.0f;
    public float buildingHeightMin = 6f;
    public float buildingHeightMax = 40f;

    [Header("Population / Density")]
    [Tooltip("Density at city center (0..1)")]
    [Range(0f, 1f)] public float centerDensity = 1.0f;
    [Tooltip("Density at city edge (0..1)")]
    [Range(0f, 1f)] public float edgeDensity = 0.15f;
    [Tooltip("How quickly density falls off from center (higher = faster falloff)")]
    public float densityFalloffPower = 1.2f;
    [Tooltip("Perlin noise scale for density variation")]
    public float densityNoiseScale = 0.01f;
    [Tooltip("Perlin noise strength for density variation (0..1)")]
    [Range(0f, 1f)] public float densityNoiseStrength = 0.25f;

    private System.Random rng;
    private List<GameObject> generated = new List<GameObject>();
    private int roadCount = 0;
    private int buildingCount = 0;

    private struct RoadSeg { public Vector3 a, b; public int id; public RoadSeg(Vector3 a, Vector3 b, int id) { this.a = a; this.b = b; this.id = id; } }
    private List<RoadSeg> roadSegments = new List<RoadSeg>();
    private Vector3 terrainCenterOffset;

    [System.Serializable]
    public struct RoadSegment
    {
        public Vector3 start;
        public Vector3 end;
        public int id;
    }

    public List<RoadSegment> GetRoadSegments()
    {
        List<RoadSegment> segments = new List<RoadSegment>();
        foreach (var seg in roadSegments)
            segments.Add(new RoadSegment { start = seg.a, end = seg.b, id = seg.id });
        return segments;
    }

    public Vector3 GetTerrainCenter() => terrainCenterOffset;
    public float GetRoadWidth() => roadWidth;

    private void Start()
    {
        if (generateOnStart) GenerateCity();
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
        Debug.Log($"CityGenerator: Finished. Roads={roadCount} Buildings={buildingCount}");
    }

    [ContextMenu("Clear City")]
    public void ClearPrevious()
    {
        for (int i = generated.Count - 1; i >= 0; i--)
        {
            var g = generated[i];
            if (g != null)
            {
#if UNITY_EDITOR
                UnityEngine.Object.DestroyImmediate(g);
#else
                Destroy(g);
#endif
            }
        }
        generated.Clear();
        roadSegments.Clear();
    }

    #region Utilities
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

    private float SampleTerrainSlope(Vector3 worldPos)
    {
        Vector3 normal = terrain.terrainData.GetInterpolatedNormal(
            (worldPos.x - terrain.GetPosition().x) / terrain.terrainData.size.x,
            (worldPos.z - terrain.GetPosition().z) / terrain.terrainData.size.z);
        return Vector3.Angle(normal, Vector3.up);
    }

    private GameObject CreateRoadObject(Vector3 a, Vector3 b, float width)
    {
        a.y = SampleTerrainHeight(a) + 2.5f; //magic number to lift road above terrain
        b.y = SampleTerrainHeight(b) + 2.5f;

        if (Vector3.Distance(a, b) < 0.01f) return null;

        GameObject road = roadPrefab != null
            ? Instantiate(roadPrefab, (a + b) * 0.5f, Quaternion.identity, root)
            : GameObject.CreatePrimitive(PrimitiveType.Cube);

        road.name = $"Road_{roadCount}";
        generated.Add(road);

        if (roadPrefab == null)
        {
            var col = road.GetComponent<Collider>();
            if (col != null)
#if UNITY_EDITOR
                UnityEngine.Object.DestroyImmediate(col);
#else
                Destroy(col);
#endif
        }

        road.transform.position = (a + b) * 0.5f;
        Vector3 delta = b - a;
        road.transform.rotation = Quaternion.LookRotation(delta.normalized, Vector3.up);
        road.transform.Rotate(-180f, 0f, 0f); // align cube length to road direction
        road.transform.localScale = new Vector3(width*0.25f, 0.01f, delta.magnitude*0.115f); // magic numbers to adjust cube size to road size

        var mr = road.GetComponent<MeshRenderer>();
        if (mr != null && roadMaterial != null) mr.sharedMaterial = roadMaterial;

        roadCount++;
        return road;
    }

    private void CreateRoadSegment(Vector3 a, Vector3 b, float width)
    {
        if (SampleTerrainSlope(a) > maxSlope || SampleTerrainSlope(b) > maxSlope) return;

        var go = CreateRoadObject(a, b, width);
        if (go != null)
            roadSegments.Add(new RoadSeg(a, b, roadCount - 1));
    }

    private float PopulationDensity(Vector3 worldPos)
    {
        Vector2 flatPos = new Vector2(worldPos.x - terrainCenterOffset.x, worldPos.z - terrainCenterOffset.z);
        float dist = flatPos.magnitude;
        float maxDist = Mathf.Min(citySize.x, citySize.y) * 0.5f;
        float t = Mathf.Clamp01(dist / maxDist);

        float baseDensity = Mathf.Lerp(centerDensity, edgeDensity, Mathf.Pow(t, Mathf.Max(0.0001f, densityFalloffPower)));

        float nx = (worldPos.x + seed * 13) * densityNoiseScale;
        float nz = (worldPos.z + seed * 79) * densityNoiseScale;
        float noise = Mathf.PerlinNoise(nx, nz) * 2f - 1f;
        return Mathf.Clamp01(baseDensity + noise * densityNoiseStrength);
    }

    private GameObject CreateBuilding(Vector3 pos, Vector2 footprint, float height, Quaternion? rotation = null)
    {
        pos.y = SampleTerrainHeight(pos);
        float safety = (Mathf.Max(footprint.x, footprint.y) * 0.5f) + buildingRoadSafetyMargin;
        if (IsPositionOnRoad(pos, safety) || SampleTerrainSlope(pos) > maxSlope) return null;

        float density = PopulationDensity(pos);
        if (rng.NextDouble() > density) return null;

        float footprintScale = Mathf.Lerp(1.0f, 0.8f, density);
        Vector2 finalFootprint = footprint * footprintScale;
        float heightScale = Mathf.Lerp(0.8f, 1.5f, density);
        float finalHeight = Mathf.Clamp(height * heightScale, buildingHeightMin, buildingHeightMax);

        GameObject b = buildingPrefab != null
            ? Instantiate(buildingPrefab, pos + new Vector3(0, finalHeight * 0.5f, 0), rotation ?? Quaternion.identity, root)
            : GameObject.CreatePrimitive(PrimitiveType.Cube);

        b.name = $"Building_{buildingCount}";
        if (buildingPrefab == null)
        {
            var col = b.GetComponent<Collider>();
            if (col != null)
#if UNITY_EDITOR
                UnityEngine.Object.DestroyImmediate(col);
#else
                Destroy(col);
#endif
        }

        b.transform.position = pos + new Vector3(0, finalHeight * 0.5f, 0);
        b.transform.rotation = rotation ?? Quaternion.identity;
        b.transform.localScale = new Vector3(finalFootprint.x, finalHeight, finalFootprint.y);

        generated.Add(b);
        buildingCount++;
        return b;
    }

    private bool IsPositionOnRoad(Vector3 worldPos, float extraMargin)
    {
        float threshold = (roadWidth * 0.5f) + extraMargin;
        foreach (var seg in roadSegments)
        {
            float d = DistancePointToSegment(worldPos, seg.a, seg.b);
            if (d <= threshold) return true;
        }
        return false;
    }

    private float DistancePointToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        Vector3 ap = p - a;
        float abLen2 = ab.sqrMagnitude;
        if (abLen2 == 0f) return Vector3.Distance(p, a);
        float t = Mathf.Clamp01(Vector3.Dot(ap, ab) / abLen2);
        Vector3 proj = a + ab * t;
        return Vector3.Distance(p, proj);
    }

    private float RandomRange(float a, float b) => (float)(rng.NextDouble() * (b - a) + a);
    #endregion

    #region Grid Generator
    private void GenerateGridCity()
    {
        int cols = Mathf.CeilToInt(citySize.x / gridBlockSize);
        int rows = Mathf.CeilToInt(citySize.y / gridBlockSize);
        float halfX = citySize.x * 0.5f;
        float halfY = citySize.y * 0.5f;

        Vector3[,] gridPoints = new Vector3[cols + 1, rows + 1];
        for (int i = 0; i <= cols; i++)
        {
            for (int j = 0; j <= rows; j++)
            {
                Vector3 local = new Vector3(-halfX + i * gridBlockSize, 0, -halfY + j * gridBlockSize);
                Vector3 world = ToTerrainCentered(local);
                world.y = SampleTerrainHeight(world);
                gridPoints[i, j] = world;
            }
        }

        // Roads
        for (int i = 0; i <= cols; i++)
            for (int j = 0; j < rows; j++)
                CreateRoadSegment(gridPoints[i, j], gridPoints[i, j + 1], roadWidth);

        for (int j = 0; j <= rows; j++)
            for (int i = 0; i < cols; i++)
                CreateRoadSegment(gridPoints[i, j], gridPoints[i + 1, j], roadWidth);

        // Buildings
        for (int i = 0; i < cols; i++)
        {
            for (int j = 0; j < rows; j++)
            {
                Vector3 center = (gridPoints[i, j] + gridPoints[i + 1, j + 1]) * 0.5f;
                float density = PopulationDensity(center);
                if (rng.NextDouble() > (0.3 + 0.7 * density)) continue;

                float margin = (roadWidth + buildingSpacing) * 0.5f;
                float availableSpace = gridBlockSize - margin * 2;
                if (availableSpace < buildingFootprintMin) continue;

                float w = RandomRange(buildingFootprintMin, Mathf.Min(buildingFootprintMax, availableSpace));
                float d = RandomRange(buildingFootprintMin, Mathf.Min(buildingFootprintMax, availableSpace));
                float h = RandomRange(buildingHeightMin, buildingHeightMax);

                Vector3 blockMin = gridPoints[i, j] + new Vector3(margin, 0, margin);
                Vector3 blockMax = gridPoints[i + 1, j + 1] - new Vector3(margin, 0, margin);

                Vector3 randomPos = new Vector3(
                    RandomRange(blockMin.x, blockMax.x),
                    0,
                    RandomRange(blockMin.z, blockMax.z)
                );
                randomPos.y = SampleTerrainHeight(randomPos);

                CreateBuilding(randomPos, new Vector2(w, d), h);
            }
        }
    }
    #endregion
}
