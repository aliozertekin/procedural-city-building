using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using static CityGenerator;
using Debug = UnityEngine.Debug;

[ExecuteAlways]
public class CityPathfindingTester : MonoBehaviour
{
    [Header("References")]
    public CityGenerator generator;

    [Header("Run Settings")]
    public KeyCode runKey = KeyCode.T;
    public int rounds = 10;

    [Header("Limits")]
    public int depthLimit = 30;
    public int breadthLimit = 30;

    // Runtime
    private List<RoadSegment> roadSegments;
    private Dictionary<Vector3, List<Vector3>> graph;
    private int visitedNodes = 0;

    void Update()
    {
        if (!Application.isPlaying) return;

        if (Input.GetKeyDown(runKey))
            RunAllTests();
    }

    // ======================================================
    // MAIN ENTRY
    // ======================================================
    void RunAllTests()
    {
        if (generator == null)
            generator = FindFirstObjectByType<CityGenerator>();

        if (generator == null)
        {
            Debug.LogError("CityPathfindingTester: CityGenerator not found.");
            return;
        }

        roadSegments = generator.GetRoadSegments();
        graph = BuildGraph();

        if (graph.Count < 2)
        {
            Debug.LogError("CityPathfindingTester: Graph too small.");
            return;
        }

        Debug.Log("===== PATHFINDING TEST START =====");

        for (int r = 1; r <= rounds; r++)
        {
            GetRandomStartEnd(out Vector3 start, out Vector3 end);

            Debug.Log($"--- ROUND {r} ---");
            Debug.Log($"Start: {start} | End: {end}"); 

            RunTest("A*", () => AStar(start, end));
            RunTest("Dijkstra", () => Dijkstra(start, end));
            RunTest("BFS", () => BFS(start, end));
            RunTest("Greedy", () => Greedy(start, end));
            RunTest($"DepthLimited({depthLimit})", () => DepthLimited(start, end, depthLimit));
            RunTest($"BreadthLimited({breadthLimit})", () => BreadthLimited(start, end, breadthLimit));
        }

        Debug.Log("===== PATHFINDING TEST END =====");
    }

    void RunTest(string name, System.Func<List<Vector3>> algo)
    {
        Stopwatch sw = Stopwatch.StartNew();
        var path = algo.Invoke();
        sw.Stop();

        Debug.Log(
            $"{name} | Time(ms): {sw.ElapsedMilliseconds} | " +
            $"PathLen: {path.Count} | Visited: {visitedNodes}"
        );

        visitedNodes = 0;
    }

    // ======================================================
    // RANDOMIZATION
    // ======================================================
    void GetRandomStartEnd(out Vector3 start, out Vector3 end)
    {
        var nodes = graph.Keys.ToList();
        start = nodes[Random.Range(0, nodes.Count)];
        end = start;

        while (end == start)
            end = nodes[Random.Range(0, nodes.Count)];
    }

    // ======================================================
    // GRAPH BUILD
    // ======================================================
    Dictionary<Vector3, List<Vector3>> BuildGraph()
    {
        var g = new Dictionary<Vector3, List<Vector3>>();

        foreach (var seg in roadSegments)
        {
            if (IsBlocked(seg)) continue;

            if (!g.ContainsKey(seg.start)) g[seg.start] = new List<Vector3>();
            if (!g.ContainsKey(seg.end)) g[seg.end] = new List<Vector3>();

            g[seg.start].Add(seg.end);
            g[seg.end].Add(seg.start);
        }
        return g;
    }

    bool IsBlocked(RoadSegment seg)
    {
        Vector3 mid = (seg.start + seg.end) * 0.5f;

        foreach (var b in generator.roadBlocks)
        {
            float r = b.radius + generator.GetRoadWidth() * 0.5f;
            if (Vector3.Distance(mid, b.position) <= r)
                return true;
        }
        return false;
    }

    // ======================================================
    // PATHFINDING ALGORITHMS
    // ======================================================
    float H(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

    List<Vector3> AStar(Vector3 s, Vector3 g)
    {
        var open = new HashSet<Vector3> { s };
        var came = new Dictionary<Vector3, Vector3>();
        var gs = new Dictionary<Vector3, float> { [s] = 0 };
        var fs = new Dictionary<Vector3, float> { [s] = H(s, g) };

        while (open.Count > 0)
        {
            var c = open.OrderBy(n => fs.GetValueOrDefault(n, float.MaxValue)).First();
            visitedNodes++;

            if (c == g) return Reconstruct(came, c);
            open.Remove(c);

            foreach (var n in graph[c])
            {
                float t = gs[c] + Vector3.Distance(c, n);
                if (!gs.ContainsKey(n) || t < gs[n])
                {
                    came[n] = c;
                    gs[n] = t;
                    fs[n] = t + H(n, g);
                    open.Add(n);
                }
            }
        }
        return new();
    }

    List<Vector3> Dijkstra(Vector3 s, Vector3 g)
    {
        var dist = graph.Keys.ToDictionary(k => k, _ => float.MaxValue);
        var prev = new Dictionary<Vector3, Vector3>();
        var q = new HashSet<Vector3>(graph.Keys);

        dist[s] = 0;

        while (q.Count > 0)
        {
            var u = q.OrderBy(n => dist[n]).First();
            q.Remove(u);
            visitedNodes++;

            if (u == g) return Reconstruct(prev, u);

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
        return new();
    }

    List<Vector3> BFS(Vector3 s, Vector3 g)
    {
        var q = new Queue<Vector3>();
        var came = new Dictionary<Vector3, Vector3>();
        var vis = new HashSet<Vector3>();

        q.Enqueue(s);
        vis.Add(s);

        while (q.Count > 0)
        {
            var c = q.Dequeue();
            visitedNodes++;

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

    List<Vector3> Greedy(Vector3 s, Vector3 g)
    {
        var open = new List<Vector3> { s };
        var came = new Dictionary<Vector3, Vector3>();
        var vis = new HashSet<Vector3>();

        while (open.Count > 0)
        {
            open.Sort((a, b) => H(a, g).CompareTo(H(b, g)));
            var c = open[0];
            open.RemoveAt(0);
            visitedNodes++;

            if (c == g) return Reconstruct(came, c);
            vis.Add(c);

            foreach (var n in graph[c])
                if (!vis.Contains(n) && !open.Contains(n))
                {
                    came[n] = c;
                    open.Add(n);
                }
        }
        return new();
    }

    List<Vector3> DepthLimited(Vector3 s, Vector3 g, int lim)
    {
        var path = new List<Vector3>();
        DFS(s, g, lim, path, new HashSet<Vector3>());
        return path;
    }

    bool DFS(Vector3 c, Vector3 g, int lim, List<Vector3> path, HashSet<Vector3> vis)
    {
        path.Add(c);
        vis.Add(c);
        visitedNodes++;

        if (c == g) return true;
        if (lim == 0) return false;

        foreach (var n in graph[c])
            if (!vis.Contains(n))
                if (DFS(n, g, lim - 1, path, vis)) return true;

        path.RemoveAt(path.Count - 1);
        return false;
    }

    List<Vector3> BreadthLimited(Vector3 s, Vector3 g, int lim)
    {
        var q = new Queue<Vector3>();
        var came = new Dictionary<Vector3, Vector3>();
        var vis = new HashSet<Vector3>();

        q.Enqueue(s);
        vis.Add(s);

        while (q.Count > 0)
        {
            var c = q.Dequeue();
            visitedNodes++;

            int count = 0;
            foreach (var n in graph[c])
            {
                if (count++ >= lim) break;
                if (vis.Add(n))
                {
                    came[n] = c;
                    if (n == g) return Reconstruct(came, n);
                    q.Enqueue(n);
                }
            }
        }
        return new();
    }

    List<Vector3> Reconstruct(Dictionary<Vector3, Vector3> came, Vector3 c)
    {
        var p = new List<Vector3> { c };
        while (came.ContainsKey(c))
        {
            c = came[c];
            p.Insert(0, c);
        }
        return p;
    }
}
