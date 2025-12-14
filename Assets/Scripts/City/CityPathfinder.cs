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

    private List<Vector3> pathPoints = new();
    private List<RoadSegment> roadSegments = new();
    private Dictionary<Vector3, List<Vector3>> graph = new();
    private Camera mainCam;

    [Header("Gizmo Settings")]
    public float lineHeight = 3f;
    public float lineWidth = 3f;
    public float sphereRadius = 2f;
    public Color pathColor = Color.red;

    // =========================
    // REQUIRED UI API
    // =========================

    public void SetStart(Vector3 pos)
    {
        if (startPoint == null)
            startPoint = new GameObject("PathStart").transform;

        startPoint.position = SnapToNearestEndpoint(pos) + Vector3.up * 2f;
        GeneratePath();
    }

    public void SetEnd(Vector3 pos)
    {
        if (endPoint == null)
            endPoint = new GameObject("PathEnd").transform;

        endPoint.position = SnapToNearestEndpoint(pos) + Vector3.up * 2f;
        GeneratePath();
    }

    public void SetAlgorithm(PathAlgo algo)
    {
        algorithm = algo;
        GeneratePath();
    }

    // =========================

    void OnEnable()
    {
        if (generator == null)
            generator = FindFirstObjectByType<CityGenerator>();

        LoadRoads();
    }

    public void LoadRoads()
    {
        if (generator == null) return;
        roadSegments = generator.GetRoadSegments();
        graph = BuildGraph();
    }

    public void GeneratePath()
    {
        if (startPoint == null || endPoint == null || graph.Count == 0)
            return;

        Vector3 start = SnapToNearestEndpoint(startPoint.position);
        Vector3 end = SnapToNearestEndpoint(endPoint.position);

        pathPoints = algorithm switch
        {
            PathAlgo.AStar => AStar(start, end),
            PathAlgo.Dijkstra => Dijkstra(start, end),
            PathAlgo.BFS => BFS(start, end),
            PathAlgo.Greedy => Greedy(start, end),
            PathAlgo.DepthLimited => DepthLimited(start, end, depthLimit),
            PathAlgo.BreadthLimited => BreadthLimited(start, end, breadthLimit),
            _ => new()
        };
    }

    // =========================
    // PATH ALGORITHMS
    // =========================

    private List<Vector3> AStar(Vector3 start, Vector3 goal)
    {
        var open = new HashSet<Vector3> { start };
        var came = new Dictionary<Vector3, Vector3>();
        var g = new Dictionary<Vector3, float> { [start] = 0 };
        var f = new Dictionary<Vector3, float> { [start] = Vector3.Distance(start, goal) };

        while (open.Count > 0)
        {
            var current = open.OrderBy(n => f.ContainsKey(n) ? f[n] : float.MaxValue).First();
            if (current == goal) return Reconstruct(came, current);

            open.Remove(current);

            foreach (var n in graph[current])
            {
                float t = g[current] + Vector3.Distance(current, n);
                if (!g.ContainsKey(n) || t < g[n])
                {
                    came[n] = current;
                    g[n] = t;
                    f[n] = t + Vector3.Distance(n, goal);
                    open.Add(n);
                }
            }
        }
        return new();
    }

    private List<Vector3> Dijkstra(Vector3 s, Vector3 g)
    {
        var d = graph.Keys.ToDictionary(k => k, _ => float.MaxValue);
        var prev = new Dictionary<Vector3, Vector3>();
        var q = new HashSet<Vector3>(graph.Keys);

        d[s] = 0;

        while (q.Count > 0)
        {
            var u = q.OrderBy(n => d[n]).First();
            q.Remove(u);
            if (u == g) return Reconstruct(prev, u);

            foreach (var v in graph[u])
            {
                float alt = d[u] + Vector3.Distance(u, v);
                if (alt < d[v])
                {
                    d[v] = alt;
                    prev[v] = u;
                }
            }
        }
        return new();
    }

    private List<Vector3> BFS(Vector3 s, Vector3 g)
    {
        var q = new Queue<Vector3>();
        var came = new Dictionary<Vector3, Vector3>();
        var vis = new HashSet<Vector3>();

        q.Enqueue(s);
        vis.Add(s);

        while (q.Count > 0)
        {
            var c = q.Dequeue();
            if (c == g) return Reconstruct(came, c);

            foreach (var n in graph[c])
            {
                if (vis.Add(n))
                {
                    came[n] = c;
                    q.Enqueue(n);
                }
            }
        }
        return new();
    }

    private List<Vector3> Greedy(Vector3 s, Vector3 g)
    {
        var open = new SortedSet<Vector3>(
            Comparer<Vector3>.Create((a, b) =>
                Vector3.Distance(a, g).CompareTo(Vector3.Distance(b, g)))
        ) { s };

        var came = new Dictionary<Vector3, Vector3>();
        var vis = new HashSet<Vector3>();

        while (open.Count > 0)
        {
            var c = open.Min;
            open.Remove(c);
            if (c == g) return Reconstruct(came, c);

            vis.Add(c);
            foreach (var n in graph[c])
                if (!vis.Contains(n))
                {
                    came.TryAdd(n, c);
                    open.Add(n);
                }
        }
        return new();
    }

    private List<Vector3> DepthLimited(Vector3 s, Vector3 g, int limit)
    {
        var path = new List<Vector3>();
        return DLS(s, g, limit, path, new HashSet<Vector3>()) ? path : new();
    }

    private bool DLS(Vector3 c, Vector3 g, int l, List<Vector3> p, HashSet<Vector3> v)
    {
        p.Add(c);
        v.Add(c);
        if (c == g) return true;
        if (l == 0) return false;

        foreach (var n in graph[c])
            if (!v.Contains(n) && DLS(n, g, l - 1, p, v))
                return true;

        p.RemoveAt(p.Count - 1);
        return false;
    }

    private List<Vector3> BreadthLimited(Vector3 s, Vector3 g, int max)
    {
        var q = new Queue<Vector3>();
        var came = new Dictionary<Vector3, Vector3>();
        var v = new HashSet<Vector3>();

        q.Enqueue(s);
        v.Add(s);

        while (q.Count > 0)
        {
            var c = q.Dequeue();
            if (c == g) return Reconstruct(came, c);

            int added = 0;
            foreach (var n in graph[c])
            {
                if (v.Add(n))
                {
                    came[n] = c;
                    q.Enqueue(n);
                    if (++added >= max) break;
                }
            }
        }
        return new();
    }

    // =========================

    private List<Vector3> Reconstruct(Dictionary<Vector3, Vector3> came, Vector3 c)
    {
        var p = new List<Vector3> { c };
        while (came.ContainsKey(c))
        {
            c = came[c];
            p.Insert(0, c);
        }
        return p;
    }

    private Vector3 SnapToNearestEndpoint(Vector3 pos)
    {
        float min = float.MaxValue;
        Vector3 best = pos;

        foreach (var s in roadSegments)
        {
            float a = Vector3.Distance(pos, s.start);
            float b = Vector3.Distance(pos, s.end);
            if (a < min) { min = a; best = s.start; }
            if (b < min) { min = b; best = s.end; }
        }
        return best;
    }

    private Dictionary<Vector3, List<Vector3>> BuildGraph()
    {
        var g = new Dictionary<Vector3, List<Vector3>>();
        foreach (var s in roadSegments)
        {
            g.TryAdd(s.start, new());
            g.TryAdd(s.end, new());
            g[s.start].Add(s.end);
            g[s.end].Add(s.start);
        }
        return g;
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (!drawGizmos) return;

        if (startPoint)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(startPoint.position, sphereRadius);
        }

        if (endPoint)
        {
            Gizmos.color = Color.blue;
            Gizmos.DrawSphere(endPoint.position, sphereRadius);
        }

        if (pathPoints.Count > 1)
        {
            Handles.color = pathColor;
            Handles.DrawAAPolyLine(lineWidth,
                pathPoints.Select(p => p + Vector3.up * lineHeight).ToArray());
        }
    }
#endif
}
