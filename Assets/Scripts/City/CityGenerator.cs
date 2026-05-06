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
    public GameObject roadblockPrefab;
    public Transform root;
    public Material roadMaterial;
    public Material foundationMaterial;
    public float roadWidth = 8f;
    public bool generateOnStart = false;
    public float maxSlope = 30f;

    [Header("Grid Settings")]
    public float gridBlockSize = 50f;

    [Header("Population / Density")]
    [Range(0f, 1f)] public float centerDensity = 0.9f;
    [Range(0f, 1f)] public float edgeDensity = 0.6f;
    public float densityFalloffPower = 1.2f;
    [Range(0f, 1f)] public float densityNoiseStrength = 0.25f;
    public float densityNoiseScale = 0.01f;

    [Header("Road Mesh Settings")]
    [Tooltip("Samples per grid-block segment. More = smoother on slopes. 16-24 is good.")]
    public int roadSegmentsPerBlock = 20;
    [Tooltip("Tiny lift above terrain to prevent z-fighting. 0.02 to 0.1.")]
    public float roadSurfaceOffset = 0.05f;

    [Header("Roadblocks")]
    public int numRoadblocks = 10;
    public float roadBlockMinSpacing = 6f;
    public float roadBlockRadius = 2f;

    [Header("Sidewalks")]
    public bool generateSidewalks = true;
    public float sidewalkWidth = 2f;
    public float sidewalkOffset = 0.08f;
    public Material sidewalkMaterial;

    [Header("Trees")]
    public bool generateTrees = true;
    [Tooltip("Number of trees to attempt to place.")]
    public int treeCount = 200;
    [Tooltip("Minimum distance between any two trees.")]
    public float treeMinSpacing = 4f;
    [Tooltip("Minimum distance a tree must be from any road or building centre.")]
    public float treeClearanceFromRoad = 6f;
    [Tooltip("Assign up to 3 tree prefabs. One is picked randomly per tree. Leave empty to use the procedural tree.")]
    public List<GameObject> treePrefabs = new List<GameObject>();

    [HideInInspector] public List<Vector3> pedestrianWaypoints = new List<Vector3>();
    [HideInInspector] public List<Vector3> sidewalkWaypoints = new List<Vector3>();

    private System.Random rng;
    private List<GameObject> generated = new List<GameObject>();
    private int roadCount = 0;
    private int buildingCount = 0;

    private struct RoadSeg { public Vector3 a, b; public int id; }
    private List<RoadSeg> roadSegments = new List<RoadSeg>();
    private Vector3 terrainCenterOffset;

    [System.Serializable]
    public struct RoadSegment { public Vector3 start; public Vector3 end; public int id; }

    public List<RoadSegment> GetRoadSegments()
    {
        var list = new List<RoadSegment>();
        foreach (var s in roadSegments)
            list.Add(new RoadSegment { start = s.a, end = s.b, id = s.id });
        return list;
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

    public event System.Action OnCityGenerated;

    [Header("Runtime Building Prefabs")]
    public List<GameObject> buildingPrefabs = new List<GameObject>();

    void Awake() { LoadBuildingPrefabs(); }
    void Start() { if (generateOnStart) GenerateCity(); }

    void LoadBuildingPrefabs()
    {
        buildingPrefabs.Clear();
        var prefabs = Resources.LoadAll<GameObject>("BuildingsLow");
        buildingPrefabs.AddRange(prefabs);
        Debug.Log($"CityGenerator: Loaded {buildingPrefabs.Count} building prefabs.");
    }

    // =========================================================
    // PUBLIC ENTRY POINTS
    // =========================================================

    [ContextMenu("Generate City")]
    public void GenerateCity()
    {
        if (terrainGenerator == null || terrainGenerator.GetComponent<Terrain>() == null)
        {
            Debug.LogError("TerrainGenerator missing or no Terrain component found!");
            return;
        }

        terrain = terrainGenerator.GetComponent<Terrain>();
        terrainCenterOffset = terrain.GetPosition() + terrain.terrainData.size * 0.5f;

        ClearPrevious();
        rng = new System.Random(seed);
        if (root == null) root = transform;
        if (roadMaterial == null) CreateDefaultRoadMaterial();

        roadCount = buildingCount = 0;
        roadSegments.Clear();

        GenerateGridCity();

        if (generateTrees) PlaceTrees();
        if (roadblockPrefab != null && numRoadblocks > 0) GenerateRoadblocks(numRoadblocks);

        Debug.Log($"CityGenerator: Done. Roads={roadCount}  Buildings={buildingCount}  Waypoints={pedestrianWaypoints.Count}");
        OnCityGenerated?.Invoke();
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
            if (rb.instance != null)
#if UNITY_EDITOR
                DestroyImmediate(rb.instance);
#else
                Destroy(rb.instance);
#endif
        roadBlocks.Clear();
    }

    public void ClearCityHard()
    {
        if (root == null) root = transform;
        for (int i = root.childCount - 1; i >= 0; i--)
#if UNITY_EDITOR
            DestroyImmediate(root.GetChild(i).gameObject);
#else
            Destroy(root.GetChild(i).gameObject);
#endif
        generated.Clear();
        roadSegments.Clear();
        roadBlocks.Clear();
        pedestrianWaypoints.Clear();
        sidewalkWaypoints.Clear();
        roadCount = buildingCount = 0;
#if UNITY_EDITOR
        UnityEditor.SceneView.RepaintAll();
#endif
    }

    // =========================================================
    // TERRAIN HELPERS
    // =========================================================

    Vector3 ToTerrainCentered(Vector3 local) => terrainCenterOffset + local;

    float TerrainY(Vector3 worldPos, float extra = 0f)
        => terrain.SampleHeight(worldPos) + terrain.GetPosition().y + extra;

    Vector3 TerrainNormal(Vector3 worldPos)
    {
        TerrainData td = terrain.terrainData;
        Vector3 origin = terrain.GetPosition();
        float nx = Mathf.Clamp01((worldPos.x - origin.x) / td.size.x);
        float nz = Mathf.Clamp01((worldPos.z - origin.z) / td.size.z);
        return td.GetInterpolatedNormal(nx, nz);
    }

    static Shader FindLitShader()
    {
        Shader s = Shader.Find("Universal Render Pipeline/Lit"); if (s != null) return s;
        s = Shader.Find("HDRP/Lit"); if (s != null) return s;
        s = Shader.Find("Standard"); if (s != null) return s;
        return Shader.Find("Diffuse");
    }

    void CreateDefaultRoadMaterial()
    {
        roadMaterial = new Material(FindLitShader());
        roadMaterial.color = new Color(0.22f, 0.22f, 0.22f);
        roadMaterial.SetFloat("_Cull", 0f);
    }

    // =========================================================
    // ROAD MESH
    // =========================================================

    void CreateTerrainRoadMesh(Vector3 start, Vector3 end, float width, int steps = -1)
    {
        if (steps < 0) steps = roadSegmentsPerBlock;
        if (Vector3.Distance(start, end) < 0.1f) return;

        float halfW = width * 0.5f;
        var spine = new Vector3[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            Vector3 p = Vector3.Lerp(start, end, i / (float)steps);
            p.y = TerrainY(p, roadSurfaceOffset);
            spine[i] = p;
        }

        int vCount = (steps + 1) * 2;
        var verts = new Vector3[vCount];
        var uvs = new Vector2[vCount];
        var tris = new int[steps * 6];
        var leftEdge = new Vector3[steps + 1];
        var rightEdge = new Vector3[steps + 1];
        float cumLen = 0f;

        for (int i = 0; i <= steps; i++)
        {
            Vector3 fwd = (i < steps ? spine[i + 1] - spine[i] : spine[i] - spine[i - 1]).normalized;
            Vector3 right = Vector3.Cross(fwd, TerrainNormal(spine[i])).normalized;
            Vector3 lp = spine[i] - right * halfW; lp.y = TerrainY(lp, roadSurfaceOffset);
            Vector3 rp = spine[i] + right * halfW; rp.y = TerrainY(rp, roadSurfaceOffset);

            leftEdge[i] = lp; rightEdge[i] = rp;
            int vi = i * 2;
            verts[vi] = lp; verts[vi + 1] = rp;

            if (i > 0) cumLen += Vector3.Distance(spine[i - 1], spine[i]);
            float vc = (cumLen / width) * 0.5f;
            uvs[vi] = new Vector2(0f, vc); uvs[vi + 1] = new Vector2(1f, vc);
        }

        for (int i = 0; i < steps; i++)
        {
            int vi = i * 2, ti = i * 6;
            tris[ti] = vi; tris[ti + 1] = vi + 1; tris[ti + 2] = vi + 2;
            tris[ti + 3] = vi + 1; tris[ti + 4] = vi + 3; tris[ti + 5] = vi + 2;
        }

        var go = new GameObject($"Road_{roadCount}");
        go.transform.SetParent(root, true);
        generated.Add(go);

        var mesh = new Mesh { name = $"RoadMesh_{roadCount}" };
        mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = roadMaterial;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;

        roadSegments.Add(new RoadSeg { a = start, b = end, id = roadCount });
        roadCount++;

        if (generateSidewalks) CreateSidewalkMesh(leftEdge, rightEdge, steps);
    }

    // =========================================================
    // SIDEWALK MESH
    // =========================================================

    void CreateSidewalkMesh(Vector3[] leftEdge, Vector3[] rightEdge, int steps)
    {
        float swOff = sidewalkOffset;
        float swW = sidewalkWidth;
        float gap = 0.25f;
        float trimDist = roadWidth * 0.5f;

        for (int side = 0; side < 2; side++)
        {
            Vector3[] roadEdge = side == 0 ? leftEdge : rightEdge;

            float[] cumLen = new float[steps + 1];
            for (int i = 1; i <= steps; i++)
                cumLen[i] = cumLen[i - 1] + Vector3.Distance(roadEdge[i - 1], roadEdge[i]);
            float totalLen = cumLen[steps];

            float startDist = trimDist;
            float endDist = totalLen - trimDist;
            if (endDist <= startDist) continue;

            var pts = new List<Vector3>();
            pts.Add(SampleEdgeAt(roadEdge, cumLen, steps, startDist));
            for (int i = 0; i <= steps; i++)
                if (cumLen[i] > startDist && cumLen[i] < endDist) pts.Add(roadEdge[i]);
            pts.Add(SampleEdgeAt(roadEdge, cumLen, steps, endDist));

            int tCount = pts.Count - 1;
            if (tCount < 1) continue;

            var verts = new Vector3[(tCount + 1) * 2];
            var uvs = new Vector2[(tCount + 1) * 2];
            var tris = new int[tCount * 6];
            float vOff = 0f;

            for (int ii = 0; ii <= tCount; ii++)
            {
                Vector3 ep = pts[ii];
                Vector3 fwd = (ii < tCount ? pts[ii + 1] - ep : ep - pts[ii - 1]).normalized;
                Vector3 outward = Vector3.Cross(fwd, TerrainNormal(ep)).normalized * (side == 0 ? -1 : 1);

                Vector3 inner = ep + outward * gap; inner.y = TerrainY(inner, swOff);
                Vector3 outer = ep + outward * (gap + swW); outer.y = TerrainY(outer, swOff);

                int vi = ii * 2;
                if (side == 0) { verts[vi] = outer; verts[vi + 1] = inner; }
                else { verts[vi] = inner; verts[vi + 1] = outer; }

                if (ii > 0) vOff += Vector3.Distance(pts[ii - 1], pts[ii]);
                float v = vOff / swW;
                uvs[vi] = new Vector2(0f, v); uvs[vi + 1] = new Vector2(1f, v);

                Vector3 wp = ep + outward * (gap + swW * 0.5f);
                wp.y = TerrainY(wp, swOff);
                pedestrianWaypoints.Add(wp);
                sidewalkWaypoints.Add(wp);
            }

            for (int ii = 0; ii < tCount; ii++)
            {
                int vi = ii * 2, ti = ii * 6;
                tris[ti] = vi; tris[ti + 1] = vi + 1; tris[ti + 2] = vi + 2;
                tris[ti + 3] = vi + 1; tris[ti + 4] = vi + 3; tris[ti + 5] = vi + 2;
            }

            var go = new GameObject($"Sidewalk_{roadCount}_{side}");
            go.transform.SetParent(root, true);
            generated.Add(go);

            var mesh = new Mesh { name = go.name };
            mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial = sidewalkMaterial ?? MakeSidewalkMat();
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }
    }

    Vector3 SampleEdgeAt(Vector3[] edge, float[] cum, int steps, float d)
    {
        for (int i = 1; i <= steps; i++)
            if (cum[i] >= d)
            {
                float t = (d - cum[i - 1]) / (cum[i] - cum[i - 1]);
                return Vector3.Lerp(edge[i - 1], edge[i], t);
            }
        return edge[steps];
    }

    Material _sidewalkMatCache;
    Material MakeSidewalkMat()
    {
        if (_sidewalkMatCache != null) return _sidewalkMatCache;
        _sidewalkMatCache = new Material(FindLitShader());
        _sidewalkMatCache.color = new Color(0.72f, 0.72f, 0.68f);
        return _sidewalkMatCache;
    }

    // =========================================================
    // BUILDINGS
    // =========================================================

    float PopulationDensity(Vector3 worldPos)
    {
        var flat = new Vector2(worldPos.x - terrainCenterOffset.x, worldPos.z - terrainCenterOffset.z);
        float dist = flat.magnitude;
        float maxDist = Mathf.Min(citySize.x, citySize.y) * 0.5f;
        float t = Mathf.Clamp01(dist / maxDist);
        float base_ = Mathf.Lerp(centerDensity, edgeDensity, Mathf.Pow(Mathf.Max(t, 0.0001f), densityFalloffPower));
        float nx = (worldPos.x + seed * 13) * densityNoiseScale;
        float nz = (worldPos.z + seed * 79) * densityNoiseScale;
        return Mathf.Clamp01(base_ + (Mathf.PerlinNoise(nx, nz) * 2f - 1f) * densityNoiseStrength);
    }

    float BuildingGroundY(Vector3 c, Bounds fp)
    {
        float ex = fp.extents.x, ez = fp.extents.z;
        float h = TerrainY(c);
        h = Mathf.Max(h, TerrainY(c + new Vector3(ex, 0, ez)));
        h = Mathf.Max(h, TerrainY(c + new Vector3(-ex, 0, ez)));
        h = Mathf.Max(h, TerrainY(c + new Vector3(ex, 0, -ez)));
        h = Mathf.Max(h, TerrainY(c + new Vector3(-ex, 0, -ez)));
        return h;
    }

    float BuildingGroundYMin(Vector3 c, Bounds fp)
    {
        float ex = fp.extents.x, ez = fp.extents.z;
        float h = TerrainY(c);
        h = Mathf.Min(h, TerrainY(c + new Vector3(ex, 0, ez)));
        h = Mathf.Min(h, TerrainY(c + new Vector3(-ex, 0, ez)));
        h = Mathf.Min(h, TerrainY(c + new Vector3(ex, 0, -ez)));
        h = Mathf.Min(h, TerrainY(c + new Vector3(-ex, 0, -ez)));
        return h;
    }

    GameObject CreateBuilding(Vector3 pos, Quaternion? rotation = null)
    {
        const int maxRetries = 5;
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            float density = PopulationDensity(pos);
            if (rng.NextDouble() > density) return null;

            var filtered = new List<GameObject>();
            foreach (var p in buildingPrefabs)
            {
                if (density > 0.7f && p.name.Contains("9et")) filtered.Add(p);
                else if (density > 0.5f && p.name.Contains("5et")) filtered.Add(p);
                else if (density > 0.3f && p.name.Contains("4et")) filtered.Add(p);
                else if (density <= 0.3f && p.name.Contains("2et")) filtered.Add(p);
            }
            if (filtered.Count == 0) filtered.AddRange(buildingPrefabs);

            pos.y = TerrainY(pos);
            var sel = filtered[rng.Next(filtered.Count)];
            var b = Instantiate(sel, pos, rotation ?? Quaternion.identity, root);
            b.transform.localScale *= RandomRange(0.8f, 1f);
            if (rotation == null) b.transform.rotation = Quaternion.Euler(0, rng.Next(0, 4) * 90f, 0);

            Bounds bounds = GetBounds(b);
            float minGroundY = BuildingGroundYMin(pos, bounds);
            pos.y = minGroundY + (b.transform.position.y - bounds.min.y);
            b.transform.position = pos;
            bounds = GetBounds(b);

            if (!IsInsideTerrain(bounds) || IsCollidingRoad(bounds) || IsCollidingBuildings(bounds))
            {
                DestroyImmediate(b);
                pos.x += RandomRange(-gridBlockSize * 0.3f, gridBlockSize * 0.3f);
                pos.z += RandomRange(-gridBlockSize * 0.3f, gridBlockSize * 0.3f);
                continue;
            }

            // Foundation — BoxCollider is intentionally KEPT for collision
            float foundationHeight = (BuildingGroundY(pos, bounds) - minGroundY) + 0.4f;
            if (foundationHeight > 0.05f)
            {
                var fd = GameObject.CreatePrimitive(PrimitiveType.Cube);
                fd.name = $"Foundation_{buildingCount}";
                fd.transform.SetParent(root, true);
                fd.transform.localScale = new Vector3(bounds.size.x, foundationHeight, bounds.size.z);
                fd.transform.position = new Vector3(pos.x - 1, minGroundY + foundationHeight * 0.5f, pos.z - 1);

                var mr2 = fd.GetComponent<MeshRenderer>();
                if (mr2) mr2.sharedMaterial = foundationMaterial != null
                    ? foundationMaterial
                    : new Material(Shader.Find("Diffuse") ?? Shader.Find("Standard")) { color = new Color(0.55f, 0.55f, 0.55f) };

                // BoxCollider kept — no Destroy call
                generated.Add(fd);
            }

            b.name = $"Building_{buildingCount}";
            generated.Add(b);
            buildingCount++;
            return b;
        }
        return null;
    }

    // =========================================================
    // BOUNDS / COLLISION HELPERS
    // =========================================================

    Bounds GetBounds(GameObject obj)
    {
        var rs = obj.GetComponentsInChildren<Renderer>();
        if (rs.Length == 0) return new Bounds(obj.transform.position, Vector3.one);
        Bounds b = rs[0].bounds;
        for (int i = 1; i < rs.Length; i++) b.Encapsulate(rs[i].bounds);
        return b;
    }

    bool IsInsideTerrain(Bounds b)
    {
        Vector3 tMin = terrain.GetPosition();
        Vector3 tMax = tMin + terrain.terrainData.size;
        return b.min.x >= tMin.x && b.max.x <= tMax.x && b.min.z >= tMin.z && b.max.z <= tMax.z;
    }

    bool IsCollidingRoad(Bounds b)
    {
        float clearance = roadWidth * 0.5f + 0.25f + sidewalkWidth + 1.0f;
        foreach (var seg in roadSegments)
        {
            float dist = DistancePointToSegmentXZ(
                new Vector2(b.center.x, b.center.z),
                new Vector2(seg.a.x, seg.a.z),
                new Vector2(seg.b.x, seg.b.z));
            if (dist < clearance + b.extents.magnitude) return true;
        }
        return false;
    }

    bool IsCollidingBuildings(Bounds b)
    {
        foreach (var other in generated)
        {
            if (other == null || !other.name.StartsWith("Building_")) continue;
            if (GetBounds(other).Intersects(b)) return true;
        }
        return false;
    }

    float RandomRange(float a, float b) => (float)(rng.NextDouble() * (b - a) + a);

    // =========================================================
    // ROADBLOCKS
    // =========================================================

    void GenerateRoadblocks(int count)
    {
        roadBlocks.Clear();
        if (roadSegments.Count == 0 || roadblockPrefab == null) return;
        Physics.SyncTransforms();

        float prefabBottomToCenter = 0f;
        {
            var tmp = Instantiate(roadblockPrefab, Vector3.zero, Quaternion.identity);
            Bounds b = GetBounds(tmp);
            prefabBottomToCenter = b.center.y - b.extents.y;
            DestroyImmediate(tmp);
        }

        int placed = 0, attempts = 0, maxAttempts = Mathf.Max(200, count * 10);
        while (placed < count && attempts < maxAttempts)
        {
            attempts++;
            var seg = roadSegments[rng.Next(roadSegments.Count)];
            float t = 0.3f + (float)(rng.NextDouble() * 0.4f);
            Vector3 mid = Vector3.Lerp(seg.a, seg.b, t);
            float surfaceY = TerrainY(mid, roadSurfaceOffset);
            mid.y = surfaceY;

            Vector3 fwdXZ = seg.b - seg.a; fwdXZ.y = 0f;
            if (fwdXZ.sqrMagnitude < 0.001f) fwdXZ = Vector3.forward;
            fwdXZ.Normalize();

            Quaternion rot = Quaternion.LookRotation(fwdXZ, TerrainNormal(mid)) * Quaternion.Euler(0f, -90f, 0f);

            var go = Instantiate(roadblockPrefab,
                new Vector3(mid.x, surfaceY - prefabBottomToCenter, mid.z), rot, root);
            go.name = $"RoadBlock_{placed}";
            roadBlocks.Add(new RoadBlock { position = mid, radius = roadBlockRadius, segmentId = seg.id, instance = go });
            placed++;
        }
    }

    // =========================================================
    // GRID CITY LAYOUT
    // =========================================================

    void GenerateGridCity()
    {
        int cols = Mathf.CeilToInt(citySize.x / gridBlockSize);
        int rows = Mathf.CeilToInt(citySize.y / gridBlockSize);
        float halfX = citySize.x * 0.5f, halfY = citySize.y * 0.5f;

        var pts = new Vector3[cols + 1, rows + 1];
        for (int i = 0; i <= cols; i++)
            for (int j = 0; j <= rows; j++)
            {
                Vector3 w = ToTerrainCentered(new Vector3(-halfX + i * gridBlockSize, 0, -halfY + j * gridBlockSize));
                w.y = TerrainY(w);
                pts[i, j] = w;
            }

        bool[,] hasH = new bool[cols, rows + 1];
        bool[,] hasV = new bool[cols + 1, rows];

        for (int i = 0; i <= cols; i++)
        {
            int j = 0;
            while (j < rows)
            {
                int end = Mathf.Min(j + rng.Next(1, 4), rows);
                for (int k = j; k < end; k++) { CreateTerrainRoadMesh(pts[i, k], pts[i, k + 1], roadWidth); hasV[i, k] = true; }
                j = end + rng.Next(0, 2);
            }
        }

        for (int j = 0; j <= rows; j++)
        {
            int i = 0;
            while (i < cols)
            {
                int end = Mathf.Min(i + rng.Next(1, 4), cols);
                for (int k = i; k < end; k++) { CreateTerrainRoadMesh(pts[k, j], pts[k + 1, j], roadWidth); hasH[k, j] = true; }
                i = end + rng.Next(0, 2);
            }
        }

        int[,] armCount = new int[cols + 1, rows + 1];
        for (int i = 0; i <= cols; i++)
            for (int j = 0; j <= rows; j++)
            {
                bool n = (j < rows) && hasV[i, j], s = (j > 0) && hasV[i, j - 1];
                bool e = (i < cols) && hasH[i, j], w = (i > 0) && hasH[i - 1, j];
                armCount[i, j] = (n ? 1 : 0) + (s ? 1 : 0) + (e ? 1 : 0) + (w ? 1 : 0);
            }

        for (int i = 0; i <= cols; i++)
            for (int j = 0; j <= rows; j++)
            {
                bool n = (j < rows) && hasV[i, j], s = (j > 0) && hasV[i, j - 1];
                bool e = (i < cols) && hasH[i, j], w = (i > 0) && hasH[i - 1, j];
                if (armCount[i, j] >= 2 && generateSidewalks) CreateSidewalkCorners(pts[i, j], n, s, e, w);
            }

        if (generateSidewalks)
        {
            for (int i = 0; i <= cols; i++)
                for (int k = 0; k < rows; k++)
                    if (!hasV[i, k] && armCount[i, k] >= 2 && armCount[i, k + 1] >= 2)
                    {
                        CreateEdgeSidewalk(pts[i, k], pts[i, k + 1], true, false);
                        CreateEdgeSidewalk(pts[i, k], pts[i, k + 1], true, true);
                    }
            for (int j = 0; j <= rows; j++)
                for (int k = 0; k < cols; k++)
                    if (!hasH[k, j] && armCount[k, j] >= 2 && armCount[k + 1, j] >= 2)
                    {
                        CreateEdgeSidewalk(pts[k, j], pts[k + 1, j], false, false);
                        CreateEdgeSidewalk(pts[k, j], pts[k + 1, j], false, true);
                    }
        }

        for (int i = 0; i < cols; i++)
            for (int j = 0; j < rows; j++)
            {
                Vector3 blockCtr = (pts[i, j] + pts[i + 1, j + 1]) * 0.5f;
                float density = PopulationDensity(blockCtr);
                if (rng.NextDouble() > density) continue;

                float margin = (roadWidth + 2f) * 0.5f;
                Vector3 bMin = pts[i, j] + new Vector3(margin, 0, margin);
                Vector3 bMax = pts[i + 1, j + 1] + new Vector3(-margin, 0, -margin);
                int target = Mathf.RoundToInt(Mathf.Lerp(1, 6, density));
                int subGrid = Mathf.Clamp(Mathf.RoundToInt(gridBlockSize / 6f), 2, 6);
                float stepX = (bMax.x - bMin.x) / subGrid;
                float stepZ = (bMax.z - bMin.z) / subGrid;

                int placed = 0;
                for (int x = 0; x < subGrid && placed < target; x++)
                    for (int z = 0; z < subGrid && placed < target; z++)
                    {
                        Vector3 pos = new Vector3(bMin.x + (x + 0.5f) * stepX, 0, bMin.z + (z + 0.5f) * stepZ);
                        if (CreateBuilding(pos) != null) placed++;
                    }
            }
    }

    // =========================================================
    // SIDEWALK CORNERS
    // =========================================================

    void CreateSidewalkCorners(Vector3 centre, bool n, bool s, bool e, bool w)
    {
        float hw = roadWidth * 0.5f, gap = 0.25f, swW = sidewalkWidth;
        float inner = hw + gap, outer = hw + gap + swW;
        Material mat = sidewalkMaterial ?? MakeSidewalkMat();
        if (!(n && e)) CreateCornerQuad(centre, +inner, +inner, +outer, +outer, mat);
        if (!(n && w)) CreateCornerQuad(centre, -outer, +inner, -inner, +outer, mat);
        if (!(s && e)) CreateCornerQuad(centre, +inner, -outer, +outer, -inner, mat);
        if (!(s && w)) CreateCornerQuad(centre, -outer, -outer, -inner, -inner, mat);
    }

    void CreateCornerQuad(Vector3 centre, float x0, float z0, float x1, float z1, Material mat)
    {
        Vector3 A = centre + new Vector3(x0, 0, z0); A.y = TerrainY(A, sidewalkOffset);
        Vector3 B = centre + new Vector3(x1, 0, z0); B.y = TerrainY(B, sidewalkOffset);
        Vector3 C = centre + new Vector3(x0, 0, z1); C.y = TerrainY(C, sidewalkOffset);
        Vector3 D = centre + new Vector3(x1, 0, z1); D.y = TerrainY(D, sidewalkOffset);

        var go = new GameObject("SidewalkCorner");
        go.transform.SetParent(root, true);
        generated.Add(go);
        var mesh = new Mesh { name = "SidewalkCorner" };
        mesh.vertices = new[] { A, B, C, D };
        mesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1), new Vector2(1, 1) };
        mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
    }

    // =========================================================
    // EDGE SIDEWALK
    // =========================================================

    void CreateEdgeSidewalk(Vector3 ptA, Vector3 ptB, bool isVertical, bool rightSide)
    {
        float hw = roadWidth * 0.5f, gap = 0.25f, swW = sidewalkWidth;
        float edgeLen = Vector3.Distance(ptA, ptB);
        if (edgeLen < hw * 2f + 0.1f) return;

        float tStart = hw / edgeLen, tEnd = 1f - hw / edgeLen;
        float innerOff = hw + gap, outerOff = hw + gap + swW;
        float sign = rightSide ? 1f : -1f;

        const int steps = 10;
        var innerPts = new Vector3[steps + 1];
        var outerPts = new Vector3[steps + 1];

        for (int ii = 0; ii <= steps; ii++)
        {
            float t = Mathf.Lerp(tStart, tEnd, ii / (float)steps);
            Vector3 spine = Vector3.Lerp(ptA, ptB, t);
            spine.y = TerrainY(spine, sidewalkOffset);
            Vector3 edgeFwd = (ptB - ptA); edgeFwd.y = 0; edgeFwd.Normalize();
            Vector3 sideDir = Vector3.Cross(edgeFwd, TerrainNormal(spine)).normalized * sign;

            innerPts[ii] = spine + sideDir * innerOff; innerPts[ii].y = TerrainY(innerPts[ii], sidewalkOffset);
            outerPts[ii] = spine + sideDir * outerOff; outerPts[ii].y = TerrainY(outerPts[ii], sidewalkOffset);

            Vector3 wp = spine + sideDir * (innerOff + swW * 0.5f); wp.y = TerrainY(wp, sidewalkOffset);
            pedestrianWaypoints.Add(wp); sidewalkWaypoints.Add(wp);
        }

        var verts = new Vector3[(steps + 1) * 2];
        var uvs = new Vector2[(steps + 1) * 2];
        var tris = new int[steps * 6];
        float vOff = 0f;

        for (int ii = 0; ii <= steps; ii++)
        {
            int vi = ii * 2;
            if (rightSide) { verts[vi] = innerPts[ii]; verts[vi + 1] = outerPts[ii]; }
            else { verts[vi] = outerPts[ii]; verts[vi + 1] = innerPts[ii]; }
            if (ii > 0) vOff += Vector3.Distance(innerPts[ii - 1], innerPts[ii]);
            float v = vOff / swW;
            uvs[vi] = new Vector2(0f, v); uvs[vi + 1] = new Vector2(1f, v);
        }
        for (int ii = 0; ii < steps; ii++)
        {
            int vi = ii * 2, ti = ii * 6;
            tris[ti] = vi; tris[ti + 1] = vi + 1; tris[ti + 2] = vi + 2;
            tris[ti + 3] = vi + 1; tris[ti + 4] = vi + 3; tris[ti + 5] = vi + 2;
        }

        var go = new GameObject("SidewalkEdge");
        go.transform.SetParent(root, true);
        generated.Add(go);
        var mesh = new Mesh { name = "SidewalkEdge" };
        mesh.SetVertices(verts); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals(); mesh.RecalculateBounds();
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = sidewalkMaterial ?? MakeSidewalkMat();
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
    }

    // =========================================================
    // TREE PLACEMENT
    // =========================================================

    void PlaceTrees()
    {
        if (treeCount <= 0) return;
        float halfX = citySize.x * 0.5f, halfZ = citySize.y * 0.5f;
        var treePosns = new List<Vector3>();
        int placed = 0;

        for (int a = 0; a < treeCount * 20 && placed < treeCount; a++)
        {
            float rx = (float)(rng.NextDouble() * 2 - 1) * halfX;
            float rz = (float)(rng.NextDouble() * 2 - 1) * halfZ;
            Vector3 candidate = ToTerrainCentered(new Vector3(rx, 0, rz));
            candidate.y = TerrainY(candidate);

            if (Vector3.Angle(TerrainNormal(candidate), Vector3.up) > maxSlope) continue;

            bool bad = false;
            foreach (var seg in roadSegments)
            {
                if (DistancePointToSegmentXZ(new Vector2(candidate.x, candidate.z),
                    new Vector2(seg.a.x, seg.a.z), new Vector2(seg.b.x, seg.b.z)) < treeClearanceFromRoad)
                { bad = true; break; }
            }
            if (bad) continue;

            foreach (var tp in treePosns)
                if (Vector3.Distance(new Vector3(candidate.x, 0, candidate.z), new Vector3(tp.x, 0, tp.z)) < treeMinSpacing)
                { bad = true; break; }
            if (bad) continue;

            SpawnTree(candidate);
            treePosns.Add(candidate);
            placed++;
        }
        Debug.Log($"CityGenerator: Placed {placed} trees.");
    }

    float DistancePointToSegmentXZ(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a; float lenSq = ab.sqrMagnitude;
        if (lenSq < 0.0001f) return Vector2.Distance(p, a);
        return Vector2.Distance(p, a + Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq) * ab);
    }

    void SpawnTree(Vector3 pos)
    {
        GameObject treeGo;

        // ── Prefab tree ───────────────────────────────────────────────
        GameObject chosenPrefab = null;
        if (treePrefabs != null && treePrefabs.Count > 0)
        {
            var valid = treePrefabs.FindAll(p => p != null);
            if (valid.Count > 0) chosenPrefab = valid[rng.Next(valid.Count)];
        }

        if (chosenPrefab != null)
        {
            float scale = 1.2f + (float)rng.NextDouble() * 0.8f;
            treeGo = Instantiate(chosenPrefab, pos, Quaternion.Euler(0, (float)(rng.NextDouble() * 360), 0));
            treeGo.name = "Tree";
            treeGo.transform.SetParent(root, true);
            treeGo.transform.localScale = Vector3.one * scale;
        }
        else
        {
            // ── Procedural tree ──────────────────────────────────────────
            treeGo = new GameObject("Tree");
            treeGo.transform.SetParent(root, true);
            treeGo.transform.position = pos;
            treeGo.transform.rotation = Quaternion.Euler(0, (float)(rng.NextDouble() * 360), 0);

            float scale = 1.2f + (float)rng.NextDouble() * 0.8f;
            float trunkH = 2.5f * scale;

            // Trunk visual — primitive collider stripped; root CapsuleCollider handles it
            var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.name = "Trunk";
            trunk.transform.SetParent(treeGo.transform, false);
            trunk.transform.localScale = new Vector3(0.25f * scale, trunkH * 0.5f, 0.25f * scale);
            trunk.transform.localPosition = new Vector3(0, trunkH * 0.5f, 0);
            Destroy(trunk.GetComponent<Collider>());
            ApplyTreeMat(trunk, new Color(0.38f, 0.24f, 0.12f));

            // Canopy visuals — also no individual colliders
            float canopyY = trunkH + 0.5f * scale;
            SpawnCanopySphere(treeGo.transform, new Vector3(0, canopyY, 0), 1.5f * scale, RandomGreen());
            SpawnCanopySphere(treeGo.transform, new Vector3(0, canopyY + 0.9f * scale, 0), 1.1f * scale, RandomGreen());
            SpawnCanopySphere(treeGo.transform, new Vector3(0.4f * scale, canopyY + 0.3f * scale, 0), 0.9f * scale, RandomGreen());
            SpawnCanopySphere(treeGo.transform, new Vector3(-0.3f * scale, canopyY + 0.4f * scale, 0.3f * scale), 0.85f * scale, RandomGreen());

            // ── Single CapsuleCollider on the tree root ──────────────────
            // Covers trunk + canopy as one solid volume.
            // This reliably blocks both on-foot characters and vehicles.
            float treeTopY = canopyY + 1.5f * scale;
            var cc = treeGo.AddComponent<CapsuleCollider>();
            cc.direction = 1;                       // Y axis
            cc.height = treeTopY;
            cc.radius = 0.6f * scale;
            cc.center = new Vector3(0f, treeTopY * 0.5f, 0f);
        }

        generated.Add(treeGo);
    }

    // Canopy sphere — purely visual, collider removed
    void SpawnCanopySphere(Transform parent, Vector3 localPos, float radius, Color color)
    {
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Canopy";
        sphere.transform.SetParent(parent, false);
        sphere.transform.localPosition = localPos;
        sphere.transform.localScale = Vector3.one * radius * 2f;
        Destroy(sphere.GetComponent<Collider>());
        ApplyTreeMat(sphere, color);
    }

    Color RandomGreen()
    {
        return new Color(0.10f + (float)rng.NextDouble() * 0.12f,
                         0.38f + (float)rng.NextDouble() * 0.22f,
                         0.08f);
    }

    void ApplyTreeMat(GameObject go, Color color)
    {
        var mr = go.GetComponent<MeshRenderer>();
        if (mr) mr.sharedMaterial = new Material(FindLitShader()) { color = color };
    }
}