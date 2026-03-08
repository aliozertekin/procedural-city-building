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
    public float sidewalkOffset = 0.08f;   // slightly above road surface
    public Material sidewalkMaterial;

    [Header("Pedestrian Bridges")]
    public bool generateBridges = true;
    public int bridgeCount = 3;
    public float bridgeWidth = 3f;
    public float bridgeHeight = 5f;       // height above ground
    public Material bridgeMaterial;

    // Public list of sidewalk/bridge waypoints for NPC use
    [HideInInspector] public List<Vector3> pedestrianWaypoints = new List<Vector3>();
    // Sidewalk-only waypoints (subset of pedestrianWaypoints) for NPC sidewalk preference
    [HideInInspector] public List<Vector3> sidewalkWaypoints = new List<Vector3>();

    // Road segment data with direction — used to correctly orient bridges
    private struct RoadSegFull
    {
        public Vector3 a, b;      // inset endpoints
        public Vector3 leftA, leftB, rightA, rightB; // edge midpoints
        public bool isHorizontal; // true = runs along X axis
    }
    private List<RoadSegFull> roadSegsFull = new List<RoadSegFull>();

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

    // Fired when city generation completes — NPCSpawner listens to this
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

        if (roadblockPrefab != null && numRoadblocks > 0)
            GenerateRoadblocks(numRoadblocks);

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

    public void ClearCityHard()
    {
        if (root == null) root = transform;
        for (int i = root.childCount - 1; i >= 0; i--)
        {
#if UNITY_EDITOR
            DestroyImmediate(root.GetChild(i).gameObject);
#else
            Destroy(root.GetChild(i).gameObject);
#endif
        }
        generated.Clear();
        roadSegments.Clear();
        roadBlocks.Clear();
        pedestrianWaypoints.Clear();
        sidewalkWaypoints.Clear();
        roadSegsFull.Clear();
        roadCount = buildingCount = 0;
#if UNITY_EDITOR
        UnityEditor.SceneView.RepaintAll();
#endif
    }

    // =========================================================
    // TERRAIN HELPERS
    // =========================================================

    Vector3 ToTerrainCentered(Vector3 local) => terrainCenterOffset + local;

    // World-space Y on terrain surface at XZ position.
    float TerrainY(Vector3 worldPos, float extra = 0f)
        => terrain.SampleHeight(worldPos) + terrain.GetPosition().y + extra;

    // Terrain surface normal at a world XZ position.
    Vector3 TerrainNormal(Vector3 worldPos)
    {
        TerrainData td = terrain.terrainData;
        Vector3 origin = terrain.GetPosition();
        float nx = Mathf.Clamp01((worldPos.x - origin.x) / td.size.x);
        float nz = Mathf.Clamp01((worldPos.z - origin.z) / td.size.z);
        return td.GetInterpolatedNormal(nx, nz);
    }

    // Find whatever lit shader is available in this render pipeline
    static Shader FindLitShader()
    {
        // URP
        Shader s = Shader.Find("Universal Render Pipeline/Lit");
        if (s != null) return s;
        // HDRP
        s = Shader.Find("HDRP/Lit");
        if (s != null) return s;
        // Built-in
        s = Shader.Find("Standard");
        if (s != null) return s;
        // Absolute fallback
        return Shader.Find("Diffuse");
    }

    void CreateDefaultRoadMaterial()
    {
        roadMaterial = new Material(FindLitShader());
        roadMaterial.color = new Color(0.22f, 0.22f, 0.22f);
        // Try to turn off backface culling (works on Standard; URP ignores unknown props safely)
        roadMaterial.SetFloat("_Cull", 0f);
    }

    // =========================================================
    // TRUE TERRAIN-CONFORMING ROAD MESH
    //
    // For every road edge (start -> end) we:
    //   1. Divide the path into `steps` evenly-spaced spine points.
    //   2. At EVERY spine point we query TerrainY() for the exact
    //      surface height at that XZ.
    //   3. We also query the surface NORMAL so the road width vector
    //      lies flat on the slope instead of pointing sideways into
    //      a hill.
    //   4. The left/right edge vertices are themselves re-snapped to
    //      TerrainY so even on a cross-slope the edges hug the ground.
    //   5. We assemble all of this into a single Mesh and assign it
    //      to a new GameObject — no cube prefabs, no floating quads.
    // =========================================================

    void CreateTerrainRoadMesh(Vector3 start, Vector3 end, float width, int steps = -1)
    {
        if (steps < 0) steps = roadSegmentsPerBlock;
        float segLen = Vector3.Distance(start, end);
        if (segLen < 0.1f) return;

        float halfW = width * 0.5f;

        // Roads go full length — intersection gaps are filled by CreateIntersectionQuad()
        Vector3 dir = (end - start).normalized;

        // ── 1. Build spine ──────────────────────────────────────
        var spine = new Vector3[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            Vector3 p = Vector3.Lerp(start, end, t);
            p.y = TerrainY(p, roadSurfaceOffset);
            spine[i] = p;
        }

        // ── 2. Build vertex / UV arrays ────────────────────────
        int vCount = (steps + 1) * 2;
        var verts = new Vector3[vCount];
        var uvs = new Vector2[vCount];
        var tris = new int[steps * 6];
        var leftEdge = new Vector3[steps + 1];
        var rightEdge = new Vector3[steps + 1];

        float cumLen = 0f;

        for (int i = 0; i <= steps; i++)
        {
            Vector3 fwd;
            if (i < steps) fwd = spine[i + 1] - spine[i];
            else fwd = spine[i] - spine[i - 1];
            fwd.Normalize();

            Vector3 surfNorm = TerrainNormal(spine[i]);
            Vector3 right = Vector3.Cross(fwd, surfNorm).normalized;

            Vector3 lp = spine[i] - right * halfW;
            Vector3 rp = spine[i] + right * halfW;
            lp.y = TerrainY(lp, roadSurfaceOffset);
            rp.y = TerrainY(rp, roadSurfaceOffset);

            leftEdge[i] = lp;
            rightEdge[i] = rp;

            int vi = i * 2;
            verts[vi] = lp;
            verts[vi + 1] = rp;

            if (i > 0) cumLen += Vector3.Distance(spine[i - 1], spine[i]);
            float vCoord = (cumLen / width) * 0.5f;
            uvs[vi] = new Vector2(0f, vCoord);
            uvs[vi + 1] = new Vector2(1f, vCoord);
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
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = roadMaterial;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;

        // Use original (non-inset) endpoints so the pathfinder graph connects correctly at junctions
        roadSegments.Add(new RoadSeg { a = start, b = end, id = roadCount });

        // Store full segment data for bridge placement
        Vector3 segDir = (end - start).normalized;
        bool isHoriz = Mathf.Abs(segDir.x) > Mathf.Abs(segDir.z);
        roadSegsFull.Add(new RoadSegFull
        {
            a = spine[0],
            b = spine[steps],
            leftA = leftEdge[0],
            leftB = leftEdge[steps],
            rightA = rightEdge[0],
            rightB = rightEdge[steps],
            isHorizontal = isHoriz
        });

        roadCount++;

        if (generateSidewalks)
            CreateSidewalkMesh(leftEdge, rightEdge, steps);
    }

    // =========================================================
    // SIDEWALK MESH
    // Builds two pavement strips beside the road.
    // Trims `trimDist` metres from both ends so the sidewalk never
    // overlaps the intersection square (which is halfRoadWidth wide).
    // =========================================================
    void CreateSidewalkMesh(Vector3[] leftEdge, Vector3[] rightEdge, int steps)
    {
        float swOff = sidewalkOffset;
        float swW = sidewalkWidth;
        float gap = 0.25f;
        float trimDist = roadWidth * 0.5f;   // pull back by half road width at each end

        for (int side = 0; side < 2; side++)
        {
            Vector3[] roadEdge = side == 0 ? leftEdge : rightEdge;

            // ── compute cumulative length along the edge ──
            float[] cumLen = new float[steps + 1];
            cumLen[0] = 0f;
            for (int i = 1; i <= steps; i++)
                cumLen[i] = cumLen[i - 1] + Vector3.Distance(roadEdge[i - 1], roadEdge[i]);
            float totalLen = cumLen[steps];

            float startDist = trimDist;
            float endDist = totalLen - trimDist;
            if (endDist <= startDist) continue;   // segment too short to sidewalk

            // ── resample the edge at uniform positions from startDist to endDist ──
            // We keep the same step count for simplicity; skip points outside range.
            var trimmedPts = new System.Collections.Generic.List<Vector3>();
            // add interpolated start point
            trimmedPts.Add(SampleEdgeAt(roadEdge, cumLen, steps, startDist));
            // add all original points within range
            for (int i = 0; i <= steps; i++)
                if (cumLen[i] > startDist && cumLen[i] < endDist)
                    trimmedPts.Add(roadEdge[i]);
            // add interpolated end point
            trimmedPts.Add(SampleEdgeAt(roadEdge, cumLen, steps, endDist));

            int tCount = trimmedPts.Count - 1;
            if (tCount < 1) continue;

            var verts = new Vector3[(tCount + 1) * 2];
            var uvs = new Vector2[(tCount + 1) * 2];
            var tris = new int[tCount * 6];
            float vOff = 0f;

            for (int ii = 0; ii <= tCount; ii++)
            {
                Vector3 ep = trimmedPts[ii];
                Vector3 fwd = ii < tCount ? trimmedPts[ii + 1] - ep
                                          : ep - trimmedPts[ii - 1];
                fwd.Normalize();

                Vector3 norm = TerrainNormal(ep);
                Vector3 right = Vector3.Cross(fwd, norm).normalized;
                Vector3 outward = side == 0 ? -right : right;

                Vector3 inner = ep + outward * gap;
                inner.y = TerrainY(inner, swOff);
                Vector3 outer = ep + outward * (gap + swW);
                outer.y = TerrainY(outer, swOff);

                int vi = ii * 2;
                if (side == 0) { verts[vi] = outer; verts[vi + 1] = inner; }
                else { verts[vi] = inner; verts[vi + 1] = outer; }

                if (ii > 0) vOff += Vector3.Distance(trimmedPts[ii - 1], trimmedPts[ii]);
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
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterial =
                sidewalkMaterial != null ? sidewalkMaterial : MakeSidewalkMat();
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }
    }

    // Linearly interpolate along a polyline (roadEdge with cumLen lookup) at dist `d`
    Vector3 SampleEdgeAt(Vector3[] edge, float[] cum, int steps, float d)
    {
        for (int i = 1; i <= steps; i++)
        {
            if (cum[i] >= d)
            {
                float t = (d - cum[i - 1]) / (cum[i] - cum[i - 1]);
                return Vector3.Lerp(edge[i - 1], edge[i], t);
            }
        }
        return edge[steps];
    }

    Material _sidewalkMatCache;
    Material MakeSidewalkMat()
    {
        if (_sidewalkMatCache != null) return _sidewalkMatCache;
        _sidewalkMatCache = new Material(FindLitShader());
        _sidewalkMatCache.color = new Color(0.72f, 0.72f, 0.68f); // pale concrete
        return _sidewalkMatCache;
    }

    Material _bridgeMatCache;
    Material MakeBridgeMat()
    {
        if (_bridgeMatCache != null) return _bridgeMatCache;
        _bridgeMatCache = new Material(FindLitShader());
        _bridgeMatCache.color = new Color(0.6f, 0.58f, 0.55f); // stone grey
        return _bridgeMatCache;
    }

    // =========================================================
    // BUILDINGS
    // =========================================================

    float PopulationDensity(Vector3 worldPos)
    {
        var flat = new Vector2(worldPos.x - terrainCenterOffset.x,
                                  worldPos.z - terrainCenterOffset.z);
        float dist = flat.magnitude;
        float maxDist = Mathf.Min(citySize.x, citySize.y) * 0.5f;
        float t = Mathf.Clamp01(dist / maxDist);
        float base_ = Mathf.Lerp(centerDensity, edgeDensity,
                            Mathf.Pow(Mathf.Max(t, 0.0001f), densityFalloffPower));
        float nx = (worldPos.x + seed * 13) * densityNoiseScale;
        float nz = (worldPos.z + seed * 79) * densityNoiseScale;
        float noise = Mathf.PerlinNoise(nx, nz) * 2f - 1f;
        return Mathf.Clamp01(base_ + noise * densityNoiseStrength);
    }

    // Sample terrain height at the 4 corners + centre of a building footprint
    // and return the MAXIMUM so no corner clips underground.
    float BuildingGroundY(Vector3 centre, Bounds footprint)
    {
        float ex = footprint.extents.x;
        float ez = footprint.extents.z;
        float h = TerrainY(centre);
        h = Mathf.Max(h, TerrainY(centre + new Vector3(ex, 0, ez)));
        h = Mathf.Max(h, TerrainY(centre + new Vector3(-ex, 0, ez)));
        h = Mathf.Max(h, TerrainY(centre + new Vector3(ex, 0, -ez)));
        h = Mathf.Max(h, TerrainY(centre + new Vector3(-ex, 0, -ez)));
        return h;
    }

    // Return the MINIMUM terrain height at corners.
    float BuildingGroundYMin(Vector3 centre, Bounds footprint)
    {
        float ex = footprint.extents.x;
        float ez = footprint.extents.z;
        float h = TerrainY(centre);
        h = Mathf.Min(h, TerrainY(centre + new Vector3(ex, 0, ez)));
        h = Mathf.Min(h, TerrainY(centre + new Vector3(-ex, 0, ez)));
        h = Mathf.Min(h, TerrainY(centre + new Vector3(ex, 0, -ez)));
        h = Mathf.Min(h, TerrainY(centre + new Vector3(-ex, 0, -ez)));
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
            if (rotation == null)
                b.transform.rotation = Quaternion.Euler(0, rng.Next(0, 4) * 90f, 0);

            Bounds bounds = GetBounds(b);
            float pivotToBottom = b.transform.position.y - bounds.min.y;
            float minGroundY = BuildingGroundYMin(pos, bounds);

            // Sit building on the lowest terrain corner (no floating)
            pos.y = minGroundY + pivotToBottom;
            b.transform.position = pos;
            bounds = GetBounds(b);

            // ── Collision check BEFORE creating anything else ──
            if (!IsInsideTerrain(bounds) || IsCollidingRoad(bounds) || IsCollidingBuildings(bounds))
            {
                DestroyImmediate(b);
                pos.x += RandomRange(-gridBlockSize * 0.3f, gridBlockSize * 0.3f);
                pos.z += RandomRange(-gridBlockSize * 0.3f, gridBlockSize * 0.3f);
                continue;
            }

            // ── Building confirmed: now add foundation ──
            float maxGroundY = BuildingGroundY(pos, bounds);
            float foundationHeight = (maxGroundY - minGroundY) + 0.4f;

            if (foundationHeight > 0.05f)
            {
                var foundation = GameObject.CreatePrimitive(PrimitiveType.Cube);
                foundation.name = $"Foundation_{buildingCount}";
                foundation.transform.SetParent(root, true);

                // Match building footprint XZ exactly, height fills terrain gap
                foundation.transform.localScale = new Vector3(bounds.size.x, foundationHeight, bounds.size.z);

                // Bottom of foundation = minGroundY, so top = minGroundY + foundationHeight
                // Centre Y = minGroundY + foundationHeight * 0.5
                foundation.transform.position = new Vector3(
                    pos.x,
                    minGroundY + foundationHeight * 0.5f,
                    pos.z);

                var mr2 = foundation.GetComponent<MeshRenderer>();
                if (mr2 != null)
                {
                    if (foundationMaterial != null)
                        mr2.sharedMaterial = foundationMaterial;
                    else
                        mr2.sharedMaterial = new Material(Shader.Find("Diffuse") ?? Shader.Find("Standard"))
                        { color = new Color(0.55f, 0.55f, 0.55f) };
                }

                // No collider needed — building sits on top
                var col = foundation.GetComponent<Collider>();
                if (col != null) DestroyImmediate(col);

                generated.Add(foundation);
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
        var renderers = obj.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return new Bounds(obj.transform.position, Vector3.one);
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }

    bool IsInsideTerrain(Bounds b)
    {
        Vector3 tMin = terrain.GetPosition();
        Vector3 tMax = tMin + terrain.terrainData.size;
        return b.min.x >= tMin.x && b.max.x <= tMax.x &&
               b.min.z >= tMin.z && b.max.z <= tMax.z;
    }

    bool IsCollidingRoad(Bounds b)
    {
        float margin = 0.5f;
        foreach (var seg in roadSegments)
        {
            Vector3 closest = ClosestPtOnSeg(seg.a, seg.b, b.center);
            if (Vector3.Distance(closest, b.center) < roadWidth * 0.5f + margin + b.extents.magnitude)
                return true;
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

    Vector3 ClosestPtOnSeg(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
        return a + ab * t;
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

        // Measure the prefab's half-height and bottom offset at world origin.
        // Using extents.y (half the total height) is reliable regardless of pivot position.
        float prefabHalfHeight = 0f;
        float prefabBottomToCenter = 0f;
        {
            var tmp = Instantiate(roadblockPrefab, Vector3.zero, Quaternion.identity);
            Bounds tmpB = GetBounds(tmp);
            prefabHalfHeight = tmpB.extents.y;
            // center of bounds relative to pivot (pivot is at y=0, center may be offset)
            prefabBottomToCenter = tmpB.center.y - tmpB.extents.y; // = tmpB.min.y from pivot
            DestroyImmediate(tmp);
            Debug.Log($"[Roadblock] halfH={prefabHalfHeight:F3} bottomToCenter={prefabBottomToCenter:F3}");
        }

        int placed = 0, attempts = 0;
        int maxAttempts = Mathf.Max(200, count * 10);

        while (placed < count && attempts < maxAttempts)
        {
            attempts++;
            var seg = roadSegments[rng.Next(roadSegments.Count)];

            float t = 0.3f + (float)(rng.NextDouble() * 0.4f);
            Vector3 mid = Vector3.Lerp(seg.a, seg.b, t);
            float surfaceY = mid.y; // already exact road surface Y

            Vector3 fwd = (seg.b - seg.a).normalized;
            if (fwd == Vector3.zero) fwd = Vector3.forward;
            Quaternion rot = Quaternion.LookRotation(fwd) * Quaternion.Euler(0f, -90f, 0f);

            // We want bounds.min.y == surfaceY
            // bounds.min.y = pivot.y + prefabBottomToCenter
            // So: pivot.y = surfaceY - prefabBottomToCenter
            float pivotY = surfaceY - prefabBottomToCenter;
            Vector3 spawnPos = new Vector3(mid.x, pivotY, mid.z);

            var go = Instantiate(roadblockPrefab, spawnPos, rot, root);
            go.name = $"RoadBlock_{placed}";

            roadBlocks.Add(new RoadBlock
            {
                position = mid,
                radius = roadBlockRadius,
                segmentId = seg.id,
                instance = go
            });
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
        float halfX = citySize.x * 0.5f;
        float halfY = citySize.y * 0.5f;

        // Grid intersection points snapped to terrain surface
        var pts = new Vector3[cols + 1, rows + 1];
        for (int i = 0; i <= cols; i++)
            for (int j = 0; j <= rows; j++)
            {
                Vector3 w = ToTerrainCentered(
                    new Vector3(-halfX + i * gridBlockSize, 0, -halfY + j * gridBlockSize));
                w.y = TerrainY(w);
                pts[i, j] = w;
            }

        // Track which grid edges have roads (for intersection fill)
        bool[,] hasH = new bool[cols, rows + 1];   // horizontal: pts[i,j]→pts[i+1,j]
        bool[,] hasV = new bool[cols + 1, rows];   // vertical:   pts[i,j]→pts[i,j+1]

        // Vertical roads (along Z axis)
        for (int i = 0; i <= cols; i++)
        {
            int j = 0;
            while (j < rows)
            {
                int end = Mathf.Min(j + rng.Next(1, 4), rows);
                for (int k = j; k < end; k++)
                {
                    CreateTerrainRoadMesh(pts[i, k], pts[i, k + 1], roadWidth);
                    hasV[i, k] = true;
                }
                j = end + rng.Next(0, 2);
            }
        }

        // Horizontal roads (along X axis)
        for (int j = 0; j <= rows; j++)
        {
            int i = 0;
            while (i < cols)
            {
                int end = Mathf.Min(i + rng.Next(1, 4), cols);
                for (int k = i; k < end; k++)
                {
                    CreateTerrainRoadMesh(pts[k, j], pts[k + 1, j], roadWidth);
                    hasH[k, j] = true;
                }
                i = end + rng.Next(0, 2);
            }
        }

        // Fill intersection squares and sidewalk corners at every grid point
        for (int i = 0; i <= cols; i++)
        {
            for (int j = 0; j <= rows; j++)
            {
                bool n = (j < rows) && hasV[i, j];
                bool s = (j > 0) && hasV[i, j - 1];
                bool e = (i < cols) && hasH[i, j];
                bool w = (i > 0) && hasH[i - 1, j];
                int arms = (n ? 1 : 0) + (s ? 1 : 0) + (e ? 1 : 0) + (w ? 1 : 0);
                if (arms >= 2)
                {
                    CreateIntersectionQuad(pts[i, j], roadWidth);
                    if (generateSidewalks)
                        CreateSidewalkCorners(pts[i, j], n, s, e, w);
                }
            }
        }

        // Buildings per block cell
        for (int i = 0; i < cols; i++)
        {
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
                        Vector3 pos = new Vector3(
                            bMin.x + (x + 0.5f) * stepX,
                            0,
                            bMin.z + (z + 0.5f) * stepZ);
                        if (CreateBuilding(pos) != null) placed++;
                    }
            }
        }

        // Pedestrian overbridges across roads
        if (generateBridges && bridgeCount > 0)
            GenerateBridges(pts, cols, rows);
    }

    // =========================================================
    // SIDEWALK CORNERS
    // At each intersection, stamp a small sidewalk square in every corner
    // that is NOT occupied by a road arm. Roads take priority.
    //
    // The corner slots around a grid point are: NE, NW, SE, SW.
    // A corner is "blocked" (road present) when BOTH arms bordering it have roads.
    //   NE corner blocked if n && e
    //   NW corner blocked if n && w
    //   SE corner blocked if s && e
    //   SW corner blocked if s && w
    // =========================================================
    void CreateSidewalkCorners(Vector3 centre, bool n, bool s, bool e, bool w)
    {
        float hw = roadWidth * 0.5f;
        float gap = 0.25f;
        float swW = sidewalkWidth;
        float inner = hw + gap;
        float outer = hw + gap + swW;
        Material mat = sidewalkMaterial != null ? sidewalkMaterial : MakeSidewalkMat();

        // NE corner: +X, +Z quadrant — blocked only if BOTH n and e roads exist
        if (!(n && e)) CreateCornerQuad(centre, +inner, +inner, +outer, +outer, mat);
        // NW corner: -X, +Z quadrant
        if (!(n && w)) CreateCornerQuad(centre, -outer, +inner, -inner, +outer, mat);
        // SE corner: +X, -Z quadrant
        if (!(s && e)) CreateCornerQuad(centre, +inner, -outer, +outer, -inner, mat);
        // SW corner: -X, -Z quadrant
        if (!(s && w)) CreateCornerQuad(centre, -outer, -outer, -inner, -inner, mat);
    }

    void CreateCornerQuad(Vector3 centre,
                          float x0, float z0, float x1, float z1,
                          Material mat)
    {
        Vector3 A = centre + new Vector3(x0, 0, z0); A.y = TerrainY(A, sidewalkOffset);
        Vector3 B = centre + new Vector3(x1, 0, z0); B.y = TerrainY(B, sidewalkOffset);
        Vector3 C = centre + new Vector3(x0, 0, z1); C.y = TerrainY(C, sidewalkOffset);
        Vector3 D = centre + new Vector3(x1, 0, z1); D.y = TerrainY(D, sidewalkOffset);

        // Wind CCW from above so the top face is visible
        var verts = new Vector3[] { A, B, C, D };
        var uvs = new Vector2[] { new Vector2(0,0), new Vector2(1,0),
                                    new Vector2(0,1), new Vector2(1,1) };
        var tris = new int[] { 0, 2, 1, 1, 2, 3 };

        var go = new GameObject("SidewalkCorner");
        go.transform.SetParent(root, true);
        generated.Add(go);

        var mesh = new Mesh { name = "SidewalkCorner" };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
    }

    // =========================================================
    // INTERSECTION QUAD
    // Fills the road-width square at a grid intersection point.
    // Simple flat quad snapped to terrain — no curves, no complexity.
    // =========================================================
    void CreateIntersectionQuad(Vector3 centre, float w)
    {
        float hw = w * 0.5f;

        // Four corners snapped to terrain
        Vector3 SW = centre + new Vector3(-hw, 0, -hw); SW.y = TerrainY(SW, roadSurfaceOffset);
        Vector3 SE = centre + new Vector3(hw, 0, -hw); SE.y = TerrainY(SE, roadSurfaceOffset);
        Vector3 NW = centre + new Vector3(-hw, 0, hw); NW.y = TerrainY(NW, roadSurfaceOffset);
        Vector3 NE = centre + new Vector3(hw, 0, hw); NE.y = TerrainY(NE, roadSurfaceOffset);

        var verts = new Vector3[] { SW, SE, NW, NE };
        var uvs = new Vector2[] {
            new Vector2(0,0), new Vector2(1,0),
            new Vector2(0,1), new Vector2(1,1) };
        var tris = new int[] { 0, 2, 1, 1, 2, 3 };

        var go = new GameObject($"Intersection_{roadCount}");
        go.transform.SetParent(root, true);
        generated.Add(go);

        var mesh = new Mesh { name = go.name };
        mesh.vertices = verts;
        mesh.uv = uvs;
        mesh.triangles = tris;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = roadMaterial;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
    }

    // =========================================================
    // PEDESTRIAN OVERBRIDGES
    // Picks random road intersections and builds an arch bridge
    // connecting the two sidewalks on opposite sides.
    // =========================================================
    void GenerateBridges(Vector3[,] pts, int cols, int rows)
    {
        if (roadSegsFull.Count == 0) return;

        float gap = 0.25f;
        float footClear = gap + sidewalkWidth + 0.4f;  // how far past road edge the foot lands

        // Shuffle road segments so bridges are spread around
        var segs = new List<RoadSegFull>(roadSegsFull);
        for (int i = segs.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            var tmp = segs[i]; segs[i] = segs[j]; segs[j] = tmp;
        }

        int placed = 0;
        foreach (var seg in segs)
        {
            if (placed >= bridgeCount) break;

            // Midpoint of road segment spine
            Vector3 midSpine = (seg.a + seg.b) * 0.5f;
            midSpine.y = TerrainY(midSpine);

            // Road direction and perpendicular
            Vector3 roadDir = (seg.b - seg.a).normalized;
            Vector3 perpDir = Vector3.Cross(roadDir, Vector3.up).normalized;

            // Left/right midpoints of the road edges
            Vector3 leftMid = (seg.leftA + seg.leftB) * 0.5f;
            Vector3 rightMid = (seg.rightA + seg.rightB) * 0.5f;

            // Outward direction from road centre on each side
            Vector3 leftOut = (leftMid - midSpine).normalized;
            Vector3 rightOut = (rightMid - midSpine).normalized;

            // Bridge feet land on sidewalk surface, past road edge
            Vector3 halfW = (rightMid - leftMid) * 0.5f;
            float roadRadius = halfW.magnitude;

            Vector3 footA = midSpine + leftOut * (roadRadius + footClear);
            Vector3 footB = midSpine + rightOut * (roadRadius + footClear);

            float peakY = Mathf.Max(TerrainY(footA), TerrainY(footB), TerrainY(midSpine))
                          + bridgeHeight;

            CreateBridgeMesh(footA, footB, peakY, bridgeWidth);
            placed++;
        }
    }

    void CreateBridgeMesh(Vector3 fromGround, Vector3 toGround, float peakY, float width)
    {
        const int steps = 16;
        float halfW = width * 0.5f;

        // Build arch spine using a quadratic bezier lifted to peakY at midpoint
        var spine = new Vector3[steps + 1];
        Vector3 mid = (fromGround + toGround) * 0.5f;
        mid.y = peakY;

        // Ramp start/end points sit on the sidewalk surface
        fromGround.y = TerrainY(fromGround, sidewalkOffset + 0.05f);
        toGround.y = TerrainY(toGround, sidewalkOffset + 0.05f);

        for (int i = 0; i <= steps; i++)
        {
            float t = i / (float)steps;
            float it = 1f - t;
            // Quadratic bezier: from → mid(peak) → to
            spine[i] = it * it * fromGround + 2f * it * t * mid + t * t * toGround;
        }

        var verts = new Vector3[(steps + 1) * 2];
        var uvs = new Vector2[(steps + 1) * 2];
        var tris = new int[steps * 6];
        float cumLen = 0f;

        Vector3 bridgeDir = (toGround - fromGround).normalized;
        Vector3 bridgeRight = Vector3.Cross(bridgeDir, Vector3.up).normalized;

        for (int i = 0; i <= steps; i++)
        {
            Vector3 lp = spine[i] - bridgeRight * halfW;
            Vector3 rp = spine[i] + bridgeRight * halfW;

            int vi = i * 2;
            verts[vi] = lp;
            verts[vi + 1] = rp;

            if (i > 0) cumLen += Vector3.Distance(spine[i], spine[i - 1]);
            float v = cumLen / width;
            uvs[vi] = new Vector2(0f, v);
            uvs[vi + 1] = new Vector2(1f, v);

            // NPC waypoints along bridge deck
            Vector3 wp = spine[i];
            pedestrianWaypoints.Add(wp);
        }

        for (int i = 0; i < steps; i++)
        {
            int vi = i * 2, ti = i * 6;
            tris[ti] = vi; tris[ti + 1] = vi + 1; tris[ti + 2] = vi + 2;
            tris[ti + 3] = vi + 1; tris[ti + 4] = vi + 3; tris[ti + 5] = vi + 2;
        }

        var go = new GameObject($"PedestrianBridge_{roadCount}");
        go.transform.SetParent(root, true);
        generated.Add(go);

        var mesh = new Mesh { name = go.name };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = bridgeMaterial != null ? bridgeMaterial : MakeBridgeMat();
        go.AddComponent<MeshCollider>().sharedMesh = mesh;

        // Simple railing posts on each side
        CreateRailings(spine, bridgeRight, halfW);
    }

    void CreateRailings(Vector3[] spine, Vector3 right, float halfW)
    {
        Material mat = bridgeMaterial != null ? bridgeMaterial : MakeBridgeMat();
        for (int side = -1; side <= 1; side += 2)
        {
            for (int i = 0; i < spine.Length; i += 3)
            {
                var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                post.name = "Railing";
                post.transform.SetParent(root, true);
                post.transform.localScale = new Vector3(0.15f, 0.9f, 0.15f);
                Vector3 pos = spine[i] + right * (halfW + 0.1f) * side;
                pos.y += 0.45f;
                post.transform.position = pos;

                var mr = post.GetComponent<MeshRenderer>();
                if (mr) mr.sharedMaterial = mat;

                Destroy(post.GetComponent<Collider>());
                generated.Add(post);
            }
        }
    }
}