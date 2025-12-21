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

    [Header("Gizmo Settings")]
    public float lineHeight = 3f;
    public float lineWidth = 3f;
    public float sphereRadius = 2f;
    public Color pathColor = Color.red;

    void OnEnable()
    {
        if (generator == null)
            generator = Object.FindFirstObjectByType<CityGenerator>();

        LoadRoads();
    }

    void Update()
    {
        // Only auto-update during play
        if (!Application.isPlaying) return;

        if (autoUpdate)
        {
            if (mainCam == null) mainCam = Camera.main;
            // update at small interval? keep simple
            LoadRoads();
            GeneratePath();
        }
    }

    // Public API for UI
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

    // Load road geometry and build graph
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
    }

    // ---------------- algorithms ----------------

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
            if (current == goal)
                return ReconstructPath(cameFrom, current);

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
                if (alt < dist[v])
                {
                    dist[v] = alt;
                    prev[v] = u;
                }
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

        queue.Enqueue(start);
        visited.Add(start);

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
            var current = open.Min;
            open.Remove(current);

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
        path.Add(current);
        visited.Add(current);

        if (current == goal)
            return true;

        if (limit == 0)
        {
            path.RemoveAt(path.Count - 1);
            visited.Remove(current);
            return false;
        }

        if (!graph.ContainsKey(current))
        {
            path.RemoveAt(path.Count - 1);
            visited.Remove(current);
            return false;
        }

        foreach (var n in graph[current])
        {
            if (!visited.Contains(n))
            {
                if (DLS(n, goal, limit - 1, path, visited))
                    return true;
            }
        }

        path.RemoveAt(path.Count - 1);
        visited.Remove(current);
        return false;
    }

    private List<Vector3> BreadthLimited(Vector3 start, Vector3 goal, int maxWidth)
    {
        if (!graph.ContainsKey(start) || !graph.ContainsKey(goal)) return new List<Vector3>();

        var queue = new Queue<Vector3>();
        var came = new Dictionary<Vector3, Vector3>();
        var visited = new HashSet<Vector3>();

        queue.Enqueue(start);
        visited.Add(start);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (node == goal) return ReconstructPath(came, node);

            int added = 0;
            if (!graph.ContainsKey(node)) continue;

            foreach (var n in graph[node])
            {
                if (!visited.Add(n)) continue;
                came[n] = node;
                queue.Enqueue(n);

                added++;
                if (added >= maxWidth) break;
            }
        }
        return new List<Vector3>();
    }

    // ---------------- utilities ----------------

    private List<Vector3> ReconstructPath(Dictionary<Vector3, Vector3> cameFrom, Vector3 current)
    {
        var path = new List<Vector3> { current };
        while (cameFrom.ContainsKey(current))
        {
            current = cameFrom[current];
            path.Insert(0, current);
        }
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

        if (clearEndpoints)
        {
            if (startPoint) DestroyImmediate(startPoint.gameObject);
            if (endPoint) DestroyImmediate(endPoint.gameObject);
            startPoint = null;
            endPoint = null;
        }
    }

    public void ResetPathfinder()
    {
        if (startPoint) Destroy(startPoint.gameObject);
        if (endPoint) Destroy(endPoint.gameObject);

        startPoint = null;
        endPoint = null;

        pathPoints.Clear();
        graph.Clear();
    }



#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        if (startPoint != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(startPoint.position, sphereRadius);
        }

        if (endPoint != null)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawSphere(endPoint.position, sphereRadius);
        }

        if (pathPoints != null && pathPoints.Count > 1)
        {
            Handles.color = pathColor;
            Handles.DrawAAPolyLine(lineWidth, pathPoints.Select(p => p + Vector3.up * lineHeight).ToArray());
        }
    }
#endif
}
