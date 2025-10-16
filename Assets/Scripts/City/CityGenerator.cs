using System;
using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class CityGenerator : MonoBehaviour
{
    public enum CityType { Grid, Organic, Central }
    public CityType cityType = CityType.Grid;

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

    [Header("Organic Settings")]
    public int organicInitialSeeds = 3;
    public int organicMaxIterations = 800;
    public float organicStep = 6f;
    public float organicBranchChance = 0.08f;
    [Range(0, 1)] public float organicTurnAmount = 0.4f;
    public int organicMaxBranches = 400;

    [Header("Central Settings")]
    public int centralSpokes = 8;
    public int centralRings = 6;
    public float centralRingSpacing = 12f;

    [Header("Buildings")]
    public float buildingFootprintMin = 4f;
    public float buildingFootprintMax = 12f;
    public float buildingSpacing = 1.0f;
    public float buildingRoadSafetyMargin = 0.1f;
    public float buildingHeightMin = 6f;
    public float buildingHeightMax = 40f;

    private System.Random rng;
    private List<GameObject> generated = new List<GameObject>();
    private int roadCount = 0;
    private int buildingCount = 0;

    private struct RoadSeg { public Vector3 a, b; public int id; public RoadSeg(Vector3 a, Vector3 b, int id) { this.a = a; this.b = b; this.id = id; } }
    private List<RoadSeg> roadSegments = new List<RoadSeg>();

    private Vector3 terrainCenterOffset;

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

        Debug.Log($"CityGenerator: Starting generation ({cityType}) seed={seed} size={citySize}");

        switch (cityType)
        {
            case CityType.Grid: GenerateGridCity(); break;
            case CityType.Organic: GenerateOrganicCity(); break;
            case CityType.Central: GenerateCentralCity(); break;
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

    private Vector3 ToTerrainCentered(Vector3 local)
    {
        return terrainCenterOffset + local;
    }

    private GameObject CreateRoadObject(Vector3 a, Vector3 b, float width)
    {
        a.y = SampleTerrainHeight(a) + 3;
        b.y = SampleTerrainHeight(b) + 3;
        if (Vector3.Distance(a, b) < 0.01f) return null;

        GameObject road;
        if (roadPrefab != null)
        {
            road = Instantiate(roadPrefab, (a + b) * 0.5f, Quaternion.identity, root);
            road.name = $"Road_{roadCount}";
        }
        else
        {
            road = GameObject.CreatePrimitive(PrimitiveType.Cube);
            road.name = $"Road_{roadCount}";
            road.transform.SetParent(root, true);
        }

        generated.Add(road);
        var col = road.GetComponent<Collider>();
        if (col != null && roadPrefab == null)
        {
#if UNITY_EDITOR
            UnityEngine.Object.DestroyImmediate(col);
#else
            Destroy(col);
#endif
        }

        road.transform.position = (a + b) * 0.5f;
        Vector3 delta = b - a;
        road.transform.rotation = Quaternion.LookRotation(delta.normalized, Vector3.up);
        road.transform.localScale = new Vector3(width, 0.02f, delta.magnitude);

        var mr = road.GetComponent<MeshRenderer>();
        if (mr != null && roadMaterial != null) mr.sharedMaterial = roadMaterial;

        roadCount++;
        return road;
    }

    private void CreateRoadSegment(Vector3 a, Vector3 b, float width)
    {
        var go = CreateRoadObject(a, b, width);
        if (go != null)
        {
            int id = roadCount - 1;
            roadSegments.Add(new RoadSeg(a, b, id));
        }
    }

    private GameObject CreateBuilding(Vector3 pos, Vector2 footprint, float height)
    {
        pos.y = SampleTerrainHeight(pos);
        float safety = (Mathf.Max(footprint.x, footprint.y) * 0.5f) + buildingRoadSafetyMargin;
        if (IsPositionOnRoad(pos, safety)) return null;
        if (SampleTerrainSlope(pos) > maxSlope) return null;

        GameObject b;
        if (buildingPrefab != null)
        {
            b = Instantiate(buildingPrefab, pos + new Vector3(0, height * 0.5f, 0), Quaternion.identity, root);
        }
        else
        {
            b = GameObject.CreatePrimitive(PrimitiveType.Cube);
            b.transform.SetParent(root, true);
            var col = b.GetComponent<Collider>();
            if (col != null)
            {
#if UNITY_EDITOR
                UnityEngine.Object.DestroyImmediate(col);
#else
                Destroy(col);
#endif
            }
            b.transform.position = pos + new Vector3(0, height * 0.5f, 0);
            b.transform.localScale = new Vector3(footprint.x, height, footprint.y);
        }

        b.name = $"Building_{buildingCount}";
        generated.Add(b);
        buildingCount++;
        return b;
    }

    private float RandomRange(float a, float b) => (float)(rng.NextDouble() * (b - a) + a);
    private Vector2 GetRandomInsideCity() => new Vector2(RandomRange(-citySize.x * 0.5f, citySize.x * 0.5f),
                                                          RandomRange(-citySize.y * 0.5f, citySize.y * 0.5f));

    private bool IsPositionOnRoad(Vector3 worldPos, float extraMargin)
    {
        float halfRoad = roadWidth * 0.5f;
        float threshold = halfRoad + extraMargin;
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

    private float SampleTerrainHeight(Vector3 worldPos)
    {
        return terrain.SampleHeight(worldPos) + terrain.GetPosition().y;
    }

    private float SampleTerrainSlope(Vector3 worldPos)
    {
        Vector3 normal = terrain.terrainData.GetInterpolatedNormal(
            worldPos.x / terrain.terrainData.size.x,
            worldPos.z / terrain.terrainData.size.z);
        return Vector3.Angle(normal, Vector3.up);
    }
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

        for (int i = 0; i <= cols; i++)
            for (int j = 0; j < rows; j++)
                CreateRoadSegment(gridPoints[i, j], gridPoints[i, j + 1], roadWidth);

        for (int j = 0; j <= rows; j++)
            for (int i = 0; i < cols; i++)
                CreateRoadSegment(gridPoints[i, j], gridPoints[i + 1, j], roadWidth);

        for (int i = 0; i < cols; i++)
            for (int j = 0; j < rows; j++)
            {
                Vector3 center = (gridPoints[i, j] + gridPoints[i + 1, j + 1]) * 0.5f;
                float w = RandomRange(buildingFootprintMin, Mathf.Min(buildingFootprintMax, gridBlockSize - buildingSpacing));
                float d = RandomRange(buildingFootprintMin, Mathf.Min(buildingFootprintMax, gridBlockSize - buildingSpacing));
                float h = RandomRange(buildingHeightMin, buildingHeightMax);
                CreateBuilding(center, new Vector2(w, d), h);
            }
    }
    #endregion

    #region Organic Generator
    private class RoadNode { public Vector2 pos; public Vector2 dir; public RoadNode(Vector2 p, Vector2 d) { pos = p; dir = d.normalized; } }

    private void GenerateOrganicCity()
    {
        float halfX = citySize.x * 0.5f;
        float halfY = citySize.y * 0.5f;

        List<RoadNode> active = new List<RoadNode>();
        HashSet<long> visited = new HashSet<long>();

        for (int i = 0; i < organicInitialSeeds; i++)
        {
            Vector2 p = GetRandomInsideCity();
            float angle = (float)(rng.NextDouble() * Math.PI * 2f);
            active.Add(new RoadNode(p, new Vector2(Mathf.Cos(angle), Mathf.Sin(angle))));
        }

        int iterations = 0;
        int branches = 0;
        while (active.Count > 0 && iterations < organicMaxIterations && branches < organicMaxBranches)
        {
            iterations++;
            int idx = rng.Next(active.Count);
            RoadNode n = active[idx];
            active.RemoveAt(idx);

            Vector2 next = n.pos + n.dir * organicStep;
            if (next.x < -halfX || next.x > halfX || next.y < -halfY || next.y > halfY) continue;

            long key = (((long)Mathf.RoundToInt(next.x * 10f)) << 32) ^ ((long)Mathf.RoundToInt(next.y * 10f) & 0xffffffffL);
            if (visited.Contains(key)) continue;
            visited.Add(key);

            Vector3 a = ToTerrainCentered(new Vector3(n.pos.x, 0, n.pos.y));
            Vector3 b = ToTerrainCentered(new Vector3(next.x, 0, next.y));
            a.y = SampleTerrainHeight(a);
            b.y = SampleTerrainHeight(b);

            if (SampleTerrainSlope(a) <= maxSlope && SampleTerrainSlope(b) <= maxSlope)
                CreateRoadSegment(a, b, roadWidth);

            if (rng.NextDouble() < organicBranchChance && branches < organicMaxBranches)
            {
                float spread = (float)(rng.NextDouble() * Math.PI * 0.9f);
                active.Add(new RoadNode(next, Rotate(n.dir, spread)));
                active.Add(new RoadNode(next, Rotate(n.dir, -spread)));
                branches++;
            }

            float turn = (float)((rng.NextDouble() - 0.5) * organicTurnAmount);
            active.Add(new RoadNode(next, Rotate(n.dir, turn)));

            if (rng.NextDouble() < 0.6)
                PlaceBuildingsAlongRoad(n.pos, next, n.dir);
        }
    }

    private Vector2 Rotate(Vector2 v, float angle)
    {
        float ca = Mathf.Cos(angle);
        float sa = Mathf.Sin(angle);
        return new Vector2(v.x * ca - v.y * sa, v.x * sa + v.y * ca).normalized;
    }

    private void PlaceBuildingsAlongRoad(Vector2 a2, Vector2 b2, Vector2 dir)
    {
        Vector3 a = ToTerrainCentered(new Vector3(a2.x, 0, a2.y));
        Vector3 b = ToTerrainCentered(new Vector3(b2.x, 0, b2.y));
        a.y = SampleTerrainHeight(a);
        b.y = SampleTerrainHeight(b);
        Vector3 center = (a + b) * 0.5f;
        Vector3 right = Vector3.Cross(Vector3.up, new Vector3(dir.x, 0, dir.y)).normalized;

        for (int side = -1; side <= 1; side += 2)
        {
            float offset = (roadWidth * 0.5f) + buildingSpacing + RandomRange(0f, 2f);
            Vector3 pos = center + right * side * offset;
            float fw = RandomRange(buildingFootprintMin, buildingFootprintMax);
            float fd = RandomRange(buildingFootprintMin, buildingFootprintMax);
            float h = RandomRange(buildingHeightMin * 0.3f, buildingHeightMax * 0.6f);
            CreateBuilding(pos, new Vector2(fw, fd), h);
        }
    }
    #endregion

    #region Central Generator
    private void GenerateCentralCity()
    {
        float maxRadius = Mathf.Min(citySize.x, citySize.y) * 0.5f;

        for (int r = 1; r <= centralRings; r++)
        {
            float rad = r * centralRingSpacing;
            if (rad > maxRadius) break;

            int segments = Mathf.Max(12, Mathf.CeilToInt(2 * Mathf.PI * rad / (gridBlockSize * 0.5f)));
            Vector3 prev = Vector3.zero, first = Vector3.zero;
            for (int s = 0; s < segments; s++)
            {
                float ang = s * Mathf.PI * 2f / segments;
                Vector3 p = ToTerrainCentered(new Vector3(Mathf.Cos(ang) * rad, 0, Mathf.Sin(ang) * rad));
                p.y = SampleTerrainHeight(p);
                if (s > 0) CreateRoadSegment(prev, p, roadWidth); else first = p;
                prev = p;
            }
            CreateRoadSegment(prev, first, roadWidth);

            for (int s = 0; s < segments; s++)
            {
                float ang = s * Mathf.PI * 2f / segments;
                Vector3 p = ToTerrainCentered(new Vector3(Mathf.Cos(ang) * rad, 0, Mathf.Sin(ang) * rad));
                p.y = SampleTerrainHeight(p);
                Vector3 outwards = (p - terrainCenterOffset).normalized;
                float fw = RandomRange(buildingFootprintMin * 0.6f, buildingFootprintMax * 0.9f);
                float fd = RandomRange(buildingFootprintMin * 0.6f, buildingFootprintMax * 0.9f);
                float h = RandomRange(buildingHeightMin * 0.6f + (centralRings - r), buildingHeightMax * (1f - (float)r / centralRings * 0.6f));
                CreateBuilding(p + outwards * (buildingSpacing + RandomRange(2f, 4f)), new Vector2(fw, fd), h);
            }
        }

        for (int s = 0; s < centralSpokes; s++)
        {
            float ang = s * Mathf.PI * 2f / centralSpokes;
            Vector3 dir = new Vector3(Mathf.Cos(ang), 0, Mathf.Sin(ang));
            Vector3 from = ToTerrainCentered(Vector3.zero);
            Vector3 to = ToTerrainCentered(dir * (Mathf.Min(citySize.x, citySize.y) * 0.5f));
            int segs = Mathf.CeilToInt((to - from).magnitude / (centralRingSpacing * 0.6f));
            Vector3 prev = from;
            for (int i = 1; i <= segs; i++)
            {
                Vector3 next = Vector3.Lerp(from, to, (float)i / segs);
                next.y = SampleTerrainHeight(next);
                CreateRoadSegment(prev, next, roadWidth);
                prev = next;
            }
        }
    }
    #endregion
}
