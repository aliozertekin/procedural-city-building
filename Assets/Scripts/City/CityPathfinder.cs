using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static CityGenerator;

#if UNITY_EDITOR
using UnityEditor;
#endif

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[ExecuteAlways]
public class CityPathfinder : MonoBehaviour
{
    public enum PathAlgo
    {
        AStar,
        Dijkstra,
        BFS,
        Greedy,
        DepthLimited,
        BreadthLimited
    }

    [Header("References")]
    public CityGenerator generator;

    [Header("Path Settings")]
    public Transform startPoint;
    public Transform endPoint;
    public bool autoUpdate;
    public bool drawGizmos = true;

    [Header("Path Algorithm")]
    public PathAlgo algorithm = PathAlgo.AStar;

    [Header("Algorithm Limits")]
    public int depthLimit = 30;
    public int breadthLimit = 30;

    // runtime
    private List<Vector3> pathPoints = new List<Vector3>();
    private List<RoadSegment> roadSegments = new List<RoadSegment>();
    private Dictionary<Vector3, List<Vector3>> graph = new Dictionary<Vector3, List<Vector3>>();
    private Camera mainCam;

    [Header("Gizmo / Path Visual Settings")]
    public bool showArrows = true;          // gates in-game LineRenderer + runtime arrows
    public float lineHeight = 3f;
    public float lineWidth = 4f;
    public float sphereRadius = 2f;
    public Color pathColor = new Color(1f, 0.25f, 0.1f, 1f);
    [Tooltip("Colour of the arrowhead triangles drawn along the path.")]
    public Color arrowColor = new Color(1f, 0.85f, 0.1f, 1f);
    [Tooltip("World-space distance between each arrow along the path.")]
    public float arrowSpacing = 12f;
    [Tooltip("Half-width of each arrowhead in world units.")]
    public float arrowHalfWidth = 1.8f;
    [Tooltip("Length of the arrowhead tip in world units.")]
    public float arrowLength = 3.5f;

    // ── runtime in-game line renderer ─────────────────────────────
    private LineRenderer lineRenderer;
    private readonly List<GameObject> runtimeArrows = new List<GameObject>();

    void OnEnable()
    {
        if (generator == null)
            generator = Object.FindFirstObjectByType<CityGenerator>();

        LoadRoads();

        if (Application.isPlaying)
            EnsureLineRenderer();
    }

    void OnDisable()
    {
        ClearRuntimeArrows();
        if (lineRenderer != null)
            lineRenderer.positionCount = 0;
    }

    void Update()
    {
        if (!Application.isPlaying) return;

        if (autoUpdate)
        {
            if (mainCam == null) mainCam = Camera.main;
            LoadRoads();
            GeneratePath();
        }

        if (drawGizmos)
            UpdateRuntimeVisuals();
    }

    // ─────────────────────────────────────────────────────────────
    //  Runtime visuals (in-game: LineRenderer + arrow meshes)
    // ─────────────────────────────────────────────────────────────

    void EnsureLineRenderer()
    {
        if (lineRenderer != null) return;
        lineRenderer = gameObject.GetComponent<LineRenderer>();
        if (lineRenderer == null)
            lineRenderer = gameObject.AddComponent<LineRenderer>();

        lineRenderer.useWorldSpace = true;
        lineRenderer.widthMultiplier = lineWidth * 0.07f;
        lineRenderer.numCornerVertices = 4;
        lineRenderer.numCapVertices = 4;
        lineRenderer.alignment = LineAlignment.View;

        Shader sh = Shader.Find("Sprites/Default")
                 ?? Shader.Find("Unlit/Color")
                 ?? Shader.Find("Standard");
        var mat = new Material(sh) { color = pathColor };
        lineRenderer.material = mat;
        lineRenderer.startColor = pathColor;
        lineRenderer.endColor = pathColor;
    }

    void UpdateRuntimeVisuals()
    {
        EnsureLineRenderer();

        // Honour the runtime-visual toggle
        if (!showArrows)
        {
            lineRenderer.positionCount = 0;
            ClearRuntimeArrows();
            return;
        }

        if (pathPoints == null || pathPoints.Count < 2)
        {
            lineRenderer.positionCount = 0;
            ClearRuntimeArrows();
            return;
        }

        var elevated = pathPoints.Select(p => p + Vector3.up * lineHeight).ToArray();

        lineRenderer.positionCount = elevated.Length;
        lineRenderer.SetPositions(elevated);

        RebuildRuntimeArrows(elevated);
    }

    void RebuildRuntimeArrows(Vector3[] pts)
    {
        ClearRuntimeArrows();

        float distAccum = 0f;
        float nextArrow = arrowSpacing * 0.5f;

        for (int i = 1; i < pts.Length; i++)
        {
            Vector3 segStart = pts[i - 1];
            Vector3 segEnd = pts[i];
            float segLen = Vector3.Distance(segStart, segEnd);
            Vector3 dir = (segEnd - segStart).normalized;

            while (distAccum + segLen >= nextArrow)
            {
                float localT = nextArrow - distAccum;
                Vector3 arrowPos = segStart + dir * localT;
                SpawnRuntimeArrow(arrowPos, dir);
                nextArrow += arrowSpacing;
            }
            distAccum += segLen;
        }
    }

    void SpawnRuntimeArrow(Vector3 pos, Vector3 forward)
    {
        var go = new GameObject("PathArrow");
        go.transform.position = pos;
        go.transform.SetParent(transform, true);
        runtimeArrows.Add(go);

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();

        float hw = arrowHalfWidth;
        float len = arrowLength;
        var verts = new Vector3[]
        {
            new Vector3(  0f, 0f,  len),
            new Vector3(-hw, 0f, -len * 0.3f),
            new Vector3( hw, 0f, -len * 0.3f),
        };
        var mesh = new Mesh { name = "ArrowMesh" };
        mesh.vertices = verts;
        mesh.triangles = new[] { 0, 1, 2 };
        mesh.RecalculateNormals();
        mf.sharedMesh = mesh;

        Vector3 flatDir = new Vector3(forward.x, 0f, forward.z);
        if (flatDir.sqrMagnitude > 0.001f)
            go.transform.rotation = Quaternion.LookRotation(flatDir, Vector3.up);

        Shader sh = Shader.Find("Sprites/Default")
                 ?? Shader.Find("Unlit/Color")
                 ?? Shader.Find("Standard");
        var mat = new Material(sh) { color = arrowColor };
        mr.sharedMaterial = mat;
    }

    void ClearRuntimeArrows()
    {
        foreach (var a in runtimeArrows)
            if (a != null) Destroy(a);
        runtimeArrows.Clear();
    }

    // ─────────────────────────────────────────────────────────────
    //  Public API for UI
    // ─────────────────────────────────────────────────────────────

    public void SetStart(Vector3 pos)
    {
        if (startPoint == null)
        {
            GameObject go = new GameObject("PathStart");
            startPoint = go.transform;
        }
        startPoint.position = SnapToNearestEndpoint(pos) + Vector3.up * 2f;
        GeneratePath();
    }

    public void SetEnd(Vector3 pos)
    {
        if (endPoint == null)
        {
            GameObject go = new GameObject("PathEnd");
            endPoint = go.transform;
        }
        endPoint.position = SnapToNearestEndpoint(pos) + Vector3.up * 2f;
        GeneratePath();
    }

    public void SetAlgorithm(PathAlgo algo)
    {
        algorithm = algo;
        GeneratePath();
    }

    // ─────────────────────────────────────────────────────────────
    //  Road loading & graph
    // ─────────────────────────────────────────────────────────────

    public void LoadRoads()
    {
        if (generator == null) generator = Object.FindFirstObjectByType<CityGenerator>();
        if (generator == null)
        {
            Debug.LogWarning("CityPathfinder: generator missing when loading roads.");
            return;
        }

        var segs = generator.GetRoadSegments();
        if (segs == null)
        {
            Debug.LogWarning("CityPathfinder: generator returned no road segments.");
            roadSegments = new List<RoadSegment>();
        }
        else
        {
            roadSegments = new List<RoadSegment>(segs);
        }

        graph = BuildGraph();
    }

    public void GeneratePath()
    {
        if (roadSegments == null || roadSegments.Count == 0)
        {
            LoadRoads();
            if (roadSegments == null || roadSegments.Count == 0) return;
        }

        if (startPoint == null || endPoint == null)
            return;

        if (graph == null || graph.Count == 0)
        {
            graph = BuildGraph();
            if (graph == null || graph.Count == 0) return;
        }

        Vector3 start = SnapToNearestEndpoint(startPoint.position);
        Vector3 end = SnapToNearestEndpoint(endPoint.position);

        switch (algorithm)
        {
            case PathAlgo.AStar: pathPoints = AStar(start, end); break;
            case PathAlgo.Dijkstra: pathPoints = Dijkstra(start, end); break;
            case PathAlgo.BFS: pathPoints = BFS(start, end); break;
            case PathAlgo.Greedy: pathPoints = Greedy(start, end); break;
            case PathAlgo.DepthLimited: pathPoints = DepthLimited(start, end, depthLimit); break;
            case PathAlgo.BreadthLimited: pathPoints = BreadthLimited(start, end, breadthLimit); break;
            default: pathPoints = new List<Vector3>(); break;
        }

#if UNITY_EDITOR
        if (!Application.isPlaying)
            SceneView.RepaintAll();
#endif
    }

    // ─────────────────────────────────────────────────────────────
    //  Algorithms
    // ─────────────────────────────────────────────────────────────

    private float Heuristic(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

    private List<Vector3> AStar(Vector3 start, Vector3 goal)
    {
        if (!graph.ContainsKey(start) || !graph.ContainsKey(goal)) return new List<Vector3>();

        var openSet = new HashSet<Vector3> { start };
        var cameFrom = new Dictionary<Vector3, Vector3>();
        var gScore = new Dictionary<Vector3, float> { [start] = 0f };
        var fScore = new Dictionary<Vector3, float> { [start] = Heuristic(start, goal) };

        while (openSet.Count > 0)
        {
            var current = openSet.OrderBy(n => fScore.ContainsKey(n) ? fScore[n] : float.MaxValue).First();
            if (current == goal) return ReconstructPath(cameFrom, current);

            openSet.Remove(current);
            if (!graph.ContainsKey(current)) continue;

            foreach (var neighbor in graph[current])
            {
                float tentative = gScore[current] + Vector3.Distance(current, neighbor);
                if (!gScore.ContainsKey(neighbor) || tentative < gScore[neighbor])
                {
                    cameFrom[neighbor] = current;
                    gScore[neighbor] = tentative;
                    fScore[neighbor] = tentative + Heuristic(neighbor, goal);
                    openSet.Add(neighbor);
                }
            }
        }
        return new List<Vector3>();
    }

    private List<Vector3> Dijkstra(Vector3 start, Vector3 goal)
    {
        if (!graph.ContainsKey(start) || !graph.ContainsKey(goal)) return new List<Vector3>();

        var dist = graph.Keys.ToDictionary(k => k, _ => float.MaxValue);
        var prev = new Dictionary<Vector3, Vector3>();
        var pq = new HashSet<Vector3>(graph.Keys);
        dist[start] = 0f;

        while (pq.Count > 0)
        {
            var u = pq.OrderBy(n => dist[n]).First();
            pq.Remove(u);
            if (u == goal) return ReconstructPath(prev, u);
            if (!graph.ContainsKey(u)) continue;

            foreach (var v in graph[u])
            {
                float alt = dist[u] + Vector3.Distance(u, v);
                if (alt < dist[v]) { dist[v] = alt; prev[v] = u; }
            }
        }
        return new List<Vector3>();
    }

    private List<Vector3> BFS(Vector3 start, Vector3 goal)
    {
        if (!graph.ContainsKey(start) || !graph.ContainsKey(goal)) return new List<Vector3>();

        var queue = new Queue<Vector3>();
        var cameFrom = new Dictionary<Vector3, Vector3>();
        var visited = new HashSet<Vector3>();

        queue.Enqueue(start); visited.Add(start);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == goal) return ReconstructPath(cameFrom, current);
            if (!graph.ContainsKey(current)) continue;

            foreach (var neighbor in graph[current])
            {
                if (!visited.Add(neighbor)) continue;
                cameFrom[neighbor] = current;
                queue.Enqueue(neighbor);
            }
        }
        return new List<Vector3>();
    }

    private List<Vector3> Greedy(Vector3 start, Vector3 goal)
    {
        if (!graph.ContainsKey(start) || !graph.ContainsKey(goal)) return new List<Vector3>();

        var open = new SortedSet<Vector3>(Comparer<Vector3>.Create((a, b) =>
            Heuristic(a, goal).CompareTo(Heuristic(b, goal)))) { start };
        var cameFrom = new Dictionary<Vector3, Vector3>();
        var visited = new HashSet<Vector3>();

        while (open.Count > 0)
        {
            var current = open.Min; open.Remove(current);
            if (current == goal) return ReconstructPath(cameFrom, current);
            visited.Add(current);
            if (!graph.ContainsKey(current)) continue;

            foreach (var n in graph[current])
            {
                if (visited.Contains(n)) continue;
                if (!cameFrom.ContainsKey(n)) cameFrom[n] = current;
                open.Add(n);
            }
        }
        return new List<Vector3>();
    }

    private List<Vector3> DepthLimited(Vector3 start, Vector3 goal, int limit)
    {
        var path = new List<Vector3>();
        var visited = new HashSet<Vector3>();
        bool ok = DLS(start, goal, limit, path, visited);
        return ok ? path : new List<Vector3>();
    }

    private bool DLS(Vector3 current, Vector3 goal, int limit, List<Vector3> path, HashSet<Vector3> visited)
    {
        path.Add(current); visited.Add(current);
        if (current == goal) return true;
        if (limit == 0 || !graph.ContainsKey(current))
        {
            path.RemoveAt(path.Count - 1); visited.Remove(current); return false;
        }
        foreach (var n in graph[current])
        {
            if (!visited.Contains(n) && DLS(n, goal, limit - 1, path, visited))
                return true;
        }
        path.RemoveAt(path.Count - 1); visited.Remove(current); return false;
    }

    private List<Vector3> BreadthLimited(Vector3 start, Vector3 goal, int maxWidth)
    {
        if (!graph.ContainsKey(start) || !graph.ContainsKey(goal)) return new List<Vector3>();

        var queue = new Queue<Vector3>();
        var came = new Dictionary<Vector3, Vector3>();
        var visited = new HashSet<Vector3>();

        queue.Enqueue(start); visited.Add(start);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node == goal) return ReconstructPath(came, node);
            int added = 0;
            if (!graph.ContainsKey(node)) continue;

            foreach (var n in graph[node])
            {
                if (!visited.Add(n)) continue;
                came[n] = node; queue.Enqueue(n);
                if (++added >= maxWidth) break;
            }
        }
        return new List<Vector3>();
    }

    // ─────────────────────────────────────────────────────────────
    //  Utilities
    // ─────────────────────────────────────────────────────────────

    private List<Vector3> ReconstructPath(Dictionary<Vector3, Vector3> cameFrom, Vector3 current)
    {
        var path = new List<Vector3> { current };
        while (cameFrom.ContainsKey(current)) { current = cameFrom[current]; path.Insert(0, current); }
        return path;
    }

    private Vector3 SnapToNearestEndpoint(Vector3 pos)
    {
        if (roadSegments == null || roadSegments.Count == 0) LoadRoads();
        float min = float.MaxValue;
        Vector3 closest = pos;
        foreach (var seg in roadSegments)
        {
            float da = Vector3.Distance(pos, seg.start);
            float db = Vector3.Distance(pos, seg.end);
            if (da < min) { min = da; closest = seg.start; }
            if (db < min) { min = db; closest = seg.end; }
        }
        return closest;
    }

    private Dictionary<Vector3, List<Vector3>> BuildGraph()
    {
        var g = new Dictionary<Vector3, List<Vector3>>();
        if (roadSegments == null) return g;
        foreach (var seg in roadSegments)
        {
            if (IsSegmentBlocked(seg)) continue;
            if (!g.ContainsKey(seg.start)) g[seg.start] = new List<Vector3>();
            if (!g.ContainsKey(seg.end)) g[seg.end] = new List<Vector3>();
            g[seg.start].Add(seg.end);
            g[seg.end].Add(seg.start);
        }
        return g;
    }

    private bool IsSegmentBlocked(RoadSegment seg)
    {
        if (generator == null || generator.roadBlocks == null) return false;
        Vector3 mid = (seg.start + seg.end) * 0.5f;
        foreach (var block in generator.roadBlocks)
        {
            float threshold = block.radius + generator.GetRoadWidth() * 0.5f;
            if (Vector3.Distance(mid, block.position) <= threshold) return true;
        }
        return false;
    }

    public void ClearPath(bool clearEndpoints = false)
    {
        pathPoints.Clear();
        ClearRuntimeArrows();
        if (lineRenderer != null) lineRenderer.positionCount = 0;

        if (clearEndpoints)
        {
            if (startPoint) DestroyImmediate(startPoint.gameObject);
            if (endPoint) DestroyImmediate(endPoint.gameObject);
            startPoint = null; endPoint = null;
        }
    }

    /// <summary>Returns a copy of the last computed path so other systems can read it.</summary>
    public List<Vector3> GetCurrentPath() =>
        pathPoints != null ? new List<Vector3>(pathPoints) : new List<Vector3>();

    public void ResetPathfinder()
    {
        if (startPoint) Destroy(startPoint.gameObject);
        if (endPoint) Destroy(endPoint.gameObject);
        startPoint = null; endPoint = null;
        pathPoints.Clear();
        graph.Clear();
        ClearRuntimeArrows();
        if (lineRenderer != null) lineRenderer.positionCount = 0;
    }

    // ─────────────────────────────────────────────────────────────
    //  Editor Gizmos
    // ─────────────────────────────────────────────────────────────

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        if (startPoint != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(startPoint.position, sphereRadius);
            Handles.color = Color.green;
            Handles.Label(startPoint.position + Vector3.up * (sphereRadius + 1f), "START",
                new GUIStyle { normal = { textColor = Color.green }, fontStyle = FontStyle.Bold, fontSize = 12 });
        }

        if (endPoint != null)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawSphere(endPoint.position, sphereRadius);
            Handles.color = Color.blue;
            Handles.Label(endPoint.position + Vector3.up * (sphereRadius + 1f), "END",
                new GUIStyle { normal = { textColor = Color.cyan }, fontStyle = FontStyle.Bold, fontSize = 12 });
        }

        if (pathPoints == null || pathPoints.Count < 2) return;

        var elevated = pathPoints.Select(p => p + Vector3.up * lineHeight).ToArray();

        Handles.color = pathColor;
        Handles.DrawAAPolyLine(lineWidth, elevated);

        DrawGizmoArrows(elevated);
    }

    void DrawGizmoArrows(Vector3[] pts)
    {
        float distAccum = 0f;
        float nextArrow = arrowSpacing * 0.5f;

        for (int i = 1; i < pts.Length; i++)
        {
            Vector3 segStart = pts[i - 1];
            Vector3 segEnd = pts[i];
            float segLen = Vector3.Distance(segStart, segEnd);
            Vector3 dir = (segEnd - segStart).normalized;

            while (distAccum + segLen >= nextArrow)
            {
                float localT = nextArrow - distAccum;
                Vector3 arrowPos = segStart + dir * localT;
                DrawSingleGizmoArrow(arrowPos, dir);
                nextArrow += arrowSpacing;
            }
            distAccum += segLen;
        }
    }

    void DrawSingleGizmoArrow(Vector3 pos, Vector3 forward)
    {
        Vector3 flatFwd = new Vector3(forward.x, 0f, forward.z).normalized;
        if (flatFwd.sqrMagnitude < 0.001f) flatFwd = Vector3.forward;

        Vector3 right = Vector3.Cross(Vector3.up, flatFwd);

        Vector3 tip = pos + flatFwd * arrowLength;
        Vector3 baseL = pos - right * arrowHalfWidth;
        Vector3 baseR = pos + right * arrowHalfWidth;

        Handles.color = arrowColor;
        Handles.DrawAAConvexPolygon(tip, baseL, baseR);

        Handles.color = new Color(0f, 0f, 0f, 0.5f);
        Handles.DrawAAPolyLine(2f, tip, baseL, baseR, tip);
    }
#endif
}