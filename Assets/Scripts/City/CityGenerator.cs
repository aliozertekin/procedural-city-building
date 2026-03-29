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

    // Public list of sidewalk waypoints for NPC use
    [HideInInspector] public List<Vector3> pedestrianWaypoints = new List<Vector3>();
    // Sidewalk-only waypoints (subset of pedestrianWaypoints) for NPC sidewalk preference
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

        if (generateTrees)
            PlaceTrees();

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

        // Roads go full length — overlapping road meshes share the same material so no seam is visible
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
        // Clear zone = half road + gap + full sidewalk width + small safety margin
        // This prevents buildings from spawning over sidewalks too.
        float clearance = roadWidth * 0.5f + 0.25f + sidewalkWidth + 1.0f;
        foreach (var seg in roadSegments)
        {
            // Use XZ distance only — road segments span the full terrain height range
            Vector2 bc = new Vector2(b.center.x, b.center.z);
            Vector2 sa = new Vector2(seg.a.x, seg.a.z);
            Vector2 sb = new Vector2(seg.b.x, seg.b.z);
            float dist = DistancePointToSegmentXZ(bc, sa, sb);
            if (dist < clearance + b.extents.magnitude)
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

            // Pick a point 30-70% along the segment
            float t = 0.3f + (float)(rng.NextDouble() * 0.4f);
            Vector3 mid = Vector3.Lerp(seg.a, seg.b, t);

            // Sample the ACTUAL terrain surface at this XZ position
            float surfaceY = TerrainY(mid, roadSurfaceOffset);
            mid.y = surfaceY;

            // Road forward direction (XZ only — don't tilt forward vector into terrain)
            Vector3 fwdXZ = seg.b - seg.a; fwdXZ.y = 0f;
            if (fwdXZ.sqrMagnitude < 0.001f) fwdXZ = Vector3.forward;
            fwdXZ.Normalize();

            // Terrain normal at spawn point so the block sits flush on the slope
            Vector3 surfNorm = TerrainNormal(mid);

            // Build rotation: block's local Z = road direction, local Y = surface normal.
            // Then rotate -90° around local Y so the block faces ACROSS the road (blocking it).
            Quaternion alignToSlope = Quaternion.LookRotation(fwdXZ, surfNorm);
            Quaternion rot = alignToSlope * Quaternion.Euler(0f, -90f, 0f);

            // Place pivot so the bottom of the prefab bounds sits exactly on surfaceY.
            // prefabBottomToCenter = distance from pivot to bounds.min.y (measured at origin).
            // pivot.y = surfaceY - prefabBottomToCenter  →  bounds.min.y = surfaceY
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

        // Pre-compute arm counts for each grid point (needed for connectivity fill)
        int[,] armCount = new int[cols + 1, rows + 1];
        for (int i = 0; i <= cols; i++)
            for (int j = 0; j <= rows; j++)
            {
                bool n = (j < rows) && hasV[i, j];
                bool s = (j > 0) && hasV[i, j - 1];
                bool e = (i < cols) && hasH[i, j];
                bool w = (i > 0) && hasH[i - 1, j];
                armCount[i, j] = (n ? 1 : 0) + (s ? 1 : 0) + (e ? 1 : 0) + (w ? 1 : 0);
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
                int arms = armCount[i, j];
                if (arms >= 2 && generateSidewalks)
                    CreateSidewalkCorners(pts[i, j], n, s, e, w);
            }
        }

        // ── Sidewalk connectivity: fill empty grid edges ──────────────
        // For every grid edge that has NO road but has corner pads on BOTH ends,
        // build a narrow sidewalk strip on each lateral side connecting those pads.
        if (generateSidewalks)
        {
            // Vertical empty edges (run along Z): connect pts[i,k] → pts[i,k+1]
            for (int i = 0; i <= cols; i++)
                for (int k = 0; k < rows; k++)
                    if (!hasV[i, k] && armCount[i, k] >= 2 && armCount[i, k + 1] >= 2)
                    {
                        // Two strips: west side (-X) and east side (+X) of the grid line
                        CreateEdgeSidewalk(pts[i, k], pts[i, k + 1], isVertical: true, rightSide: false);
                        CreateEdgeSidewalk(pts[i, k], pts[i, k + 1], isVertical: true, rightSide: true);
                    }

            // Horizontal empty edges (run along X): connect pts[k,j] → pts[k+1,j]
            for (int j = 0; j <= rows; j++)
                for (int k = 0; k < cols; k++)
                    if (!hasH[k, j] && armCount[k, j] >= 2 && armCount[k + 1, j] >= 2)
                    {
                        // Two strips: south side (-Z) and north side (+Z) of the grid line
                        CreateEdgeSidewalk(pts[k, j], pts[k + 1, j], isVertical: false, rightSide: false);
                        CreateEdgeSidewalk(pts[k, j], pts[k + 1, j], isVertical: false, rightSide: true);
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
    // EDGE SIDEWALK (empty grid edge connectivity)
    // Builds a sidewalk strip along a grid edge that has NO road.
    // The strip is placed at (halfRoadWidth + gap) offset from the
    // grid centreline, and spans the full grid-block length so it
    // connects the corner pads at each end seamlessly.
    //
    //   isVertical = true  → edge runs along Z; offset is along X
    //   rightSide  = true  → offset in +X (vertical) or +Z (horiz)
    // =========================================================
    void CreateEdgeSidewalk(Vector3 ptA, Vector3 ptB, bool isVertical, bool rightSide)
    {
        float hw = roadWidth * 0.5f;
        float gap = 0.25f;
        float swW = sidewalkWidth;
        float sign = rightSide ? 1f : -1f;

        // Along-axis: trim hw from each end so the strip starts/ends exactly
        // where the corner pad starts (corner pad outer edge = hw + gap + swW from centre,
        // corner pad inner edge in the along-axis direction starts at hw from centre).
        float edgeLen = Vector3.Distance(ptA, ptB);
        if (edgeLen < hw * 2f + 0.1f) return;  // too short

        float tStart = hw / edgeLen;
        float tEnd = 1f - hw / edgeLen;

        // Perpendicular offsets: start at corner pad inner edge, end at corner pad outer edge
        float innerOff = hw + gap;           // matches corner pad inner X/Z coords
        float outerOff = hw + gap + swW;     // matches corner pad outer X/Z coords

        const int steps = 10;
        var innerPts = new Vector3[steps + 1];
        var outerPts = new Vector3[steps + 1];

        for (int ii = 0; ii <= steps; ii++)
        {
            float t = Mathf.Lerp(tStart, tEnd, ii / (float)steps);
            Vector3 spine = Vector3.Lerp(ptA, ptB, t);
            spine.y = TerrainY(spine, sidewalkOffset);

            // Use terrain normal so the outward vector lies on the slope surface
            Vector3 edgeFwd = (ptB - ptA); edgeFwd.y = 0; edgeFwd.Normalize();
            Vector3 terrNorm = TerrainNormal(spine);
            Vector3 sideDir = Vector3.Cross(edgeFwd, terrNorm).normalized * sign;

            Vector3 inner = spine + sideDir * innerOff;
            Vector3 outer = spine + sideDir * outerOff;
            inner.y = TerrainY(inner, sidewalkOffset);
            outer.y = TerrainY(outer, sidewalkOffset);

            innerPts[ii] = inner;
            outerPts[ii] = outer;

            Vector3 wp = spine + sideDir * (innerOff + swW * 0.5f);
            wp.y = TerrainY(wp, sidewalkOffset);
            pedestrianWaypoints.Add(wp);
            sidewalkWaypoints.Add(wp);
        }

        var verts = new Vector3[(steps + 1) * 2];
        var uvs = new Vector2[(steps + 1) * 2];
        var tris = new int[steps * 6];
        float vOff = 0f;

        for (int ii = 0; ii <= steps; ii++)
        {
            int vi = ii * 2;
            if (rightSide)
            { verts[vi] = innerPts[ii]; verts[vi + 1] = outerPts[ii]; }
            else
            { verts[vi] = outerPts[ii]; verts[vi + 1] = innerPts[ii]; }

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



    // =========================================================
    // TREE PLACEMENT
    // Scatters trees across the city using Poisson-disc-style
    // rejection sampling:
    //   - candidate point chosen randomly within city bounds
    //   - rejected if too close to a road segment, a building,
    //     another tree, or on too steep a slope
    //   - if a treePrefab is assigned, instantiate it;
    //     otherwise build a simple procedural tree from primitives
    // =========================================================
    void PlaceTrees()
    {
        if (treeCount <= 0) return;

        float halfX = citySize.x * 0.5f;
        float halfZ = citySize.y * 0.5f;

        // Cache road segment endpoints for distance checks
        var segs = roadSegments; // List<RoadSeg>

        // Already-placed tree positions for spacing checks
        var treePosns = new List<Vector3>();

        // Max attempts = 20× requested count to handle dense cities
        int attempts = treeCount * 20;
        int placed = 0;

        for (int a = 0; a < attempts && placed < treeCount; a++)
        {
            // Random point within city bounds (world space)
            float rx = (float)(rng.NextDouble() * 2 - 1) * halfX;
            float rz = (float)(rng.NextDouble() * 2 - 1) * halfZ;
            Vector3 candidate = ToTerrainCentered(new Vector3(rx, 0, rz));
            candidate.y = TerrainY(candidate);

            // ── Slope check ──────────────────────────────────
            Vector3 normal = TerrainNormal(candidate);
            float slope = Vector3.Angle(normal, Vector3.up);
            if (slope > maxSlope) continue;

            // ── Clearance from roads ──────────────────────────
            bool tooCloseToRoad = false;
            foreach (var seg in segs)
            {
                float d = DistancePointToSegmentXZ(
                    new Vector2(candidate.x, candidate.z),
                    new Vector2(seg.a.x, seg.a.z),
                    new Vector2(seg.b.x, seg.b.z));
                if (d < treeClearanceFromRoad) { tooCloseToRoad = true; break; }
            }
            if (tooCloseToRoad) continue;

            // ── Spacing from other trees ──────────────────────
            bool tooClose = false;
            foreach (var tp in treePosns)
            {
                if (Vector3.Distance(
                        new Vector3(candidate.x, 0, candidate.z),
                        new Vector3(tp.x, 0, tp.z)) < treeMinSpacing)
                { tooClose = true; break; }
            }
            if (tooClose) continue;

            // ── Place tree ────────────────────────────────────
            SpawnTree(candidate);
            treePosns.Add(candidate);
            placed++;
        }

        Debug.Log($"CityGenerator: Placed {placed} trees.");
    }

    // Point-to-segment distance in XZ plane
    float DistancePointToSegmentXZ(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float lenSq = ab.sqrMagnitude;
        if (lenSq < 0.0001f) return Vector2.Distance(p, a);
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
        Vector2 proj = a + t * ab;
        return Vector2.Distance(p, proj);
    }

    void SpawnTree(Vector3 pos)
    {
        GameObject treeGo;

        // Pick a random prefab from the list (if any are assigned)
        GameObject chosenPrefab = null;
        if (treePrefabs != null && treePrefabs.Count > 0)
        {
            // filter out nulls
            var valid = treePrefabs.FindAll(p => p != null);
            if (valid.Count > 0)
                chosenPrefab = valid[rng.Next(valid.Count)];
        }

        if (chosenPrefab != null)
        {
            float treeScale = 1.2f + (float)rng.NextDouble() * 0.8f;  // 1.2 – 2.0×
            treeGo = (GameObject)UnityEngine.Object.Instantiate(
                chosenPrefab, pos,
                Quaternion.Euler(0, (float)(rng.NextDouble() * 360), 0));
            treeGo.name = "Tree";
            treeGo.transform.SetParent(root, true);
            treeGo.transform.localScale = Vector3.one * treeScale;
        }
        else
        {
            // Procedural tree: trunk (cylinder) + canopy (sphere)
            treeGo = new GameObject("Tree");
            treeGo.transform.SetParent(root, true);
            treeGo.transform.position = pos;

            // Random size variation
            float scale = 1.2f + (float)rng.NextDouble() * 0.8f;  // 1.2 – 2.0×

            // Trunk
            var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            trunk.name = "Trunk";
            trunk.transform.SetParent(treeGo.transform, false);
            float trunkH = 2.5f * scale;
            trunk.transform.localScale = new Vector3(0.25f * scale, trunkH * 0.5f, 0.25f * scale);
            trunk.transform.localPosition = new Vector3(0, trunkH * 0.5f, 0);
            Destroy(trunk.GetComponent<Collider>());
            ApplyTreeMat(trunk, new Color(0.38f, 0.24f, 0.12f));  // brown bark

            // Canopy (layered spheres for a rounder look)
            float canopyY = trunkH + 0.5f * scale;
            SpawnCanopySphere(treeGo.transform, new Vector3(0, canopyY, 0), 1.5f * scale, RandomGreen());
            SpawnCanopySphere(treeGo.transform, new Vector3(0, canopyY + 0.9f * scale, 0), 1.1f * scale, RandomGreen());
            SpawnCanopySphere(treeGo.transform, new Vector3(0.4f * scale, canopyY + 0.3f * scale, 0), 0.9f * scale, RandomGreen());
            SpawnCanopySphere(treeGo.transform, new Vector3(-0.3f * scale, canopyY + 0.4f * scale, 0.3f * scale), 0.85f * scale, RandomGreen());

            // Random Y rotation
            treeGo.transform.rotation = Quaternion.Euler(0, (float)(rng.NextDouble() * 360), 0);
        }

        generated.Add(treeGo);
    }

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

    // Slight random variation in green so canopy layers look natural
    Color RandomGreen()
    {
        float g = 0.38f + (float)rng.NextDouble() * 0.22f;   // 0.38 – 0.60
        float r = 0.10f + (float)rng.NextDouble() * 0.12f;   // slight yellowing
        return new Color(r, g, 0.08f);
    }

    void ApplyTreeMat(GameObject go, Color color)
    {
        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;
        var mat = new Material(FindLitShader()) { color = color };
        mr.sharedMaterial = mat;
    }

}