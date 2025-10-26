using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using static CityGenerator;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class CityPathfinder : MonoBehaviour
{
    [Header("References")]
    public CityGenerator generator;

    [Header("Path Settings")]
    public Transform startPoint;
    public Transform endPoint;
    public bool autoUpdate = false;
    public bool drawGizmos = true;

    private List<Vector3> pathPoints = new List<Vector3>();
    private List<RoadSegment> roadSegments = new List<RoadSegment>();
    private Dictionary<Vector3, List<Vector3>> graph = new Dictionary<Vector3, List<Vector3>>();

    [Header("Gizmo Settings")]
    public float lineHeight = 3f;
    public float lineWidth = 0.3f;
    public Color pathColor = Color.red;

    void OnValidate()
    {
        if (generator == null)
            generator = FindObjectOfType<CityGenerator>();
    }

    void OnEnable()
    {
        LoadRoads();
    }

    void Update()
    {
        if (!Application.isPlaying && autoUpdate)
            GeneratePath();
    }

    [ContextMenu("Reload Roads")]
    public void LoadRoads()
    {
        if (generator == null)
        {
            Debug.LogWarning("CityPathfinder: Missing CityGenerator reference!");
            return;
        }

        roadSegments = generator.GetRoadSegments();
        if (roadSegments == null || roadSegments.Count == 0)
        {
            Debug.LogWarning("CityPathfinder: No road segments found!");
            return;
        }

        graph = BuildGraph();
        Debug.Log($"CityPathfinder: Loaded {roadSegments.Count} road segments.");
    }

    [ContextMenu("Generate Path")]
    public void GeneratePath()
    {
        if (roadSegments == null || roadSegments.Count == 0)
        {
            LoadRoads();
            if (roadSegments.Count == 0)
            {
                Debug.LogWarning("CityPathfinder: No roads available!");
                return;
            }
        }

        if (startPoint == null || endPoint == null)
        {
            Debug.LogWarning("CityPathfinder: Start or End point missing!");
            return;
        }

        // Snap start/end to nearest road endpoints
        Vector3 start = SnapToNearestEndpoint(startPoint.position);
        Vector3 end = SnapToNearestEndpoint(endPoint.position);

        pathPoints = AStar(start, end);
        if (pathPoints.Count > 0)
            Debug.Log($"CityPathfinder: Generated path with {pathPoints.Count} nodes.");
        else
            Debug.LogWarning("CityPathfinder: Failed to generate path!");
    }

    [ContextMenu("Randomize Start/End")]
    public void RandomizePoints()
    {
        if (roadSegments == null || roadSegments.Count == 0)
        {
            LoadRoads();
            if (roadSegments.Count == 0)
            {
                Debug.LogWarning("CityPathfinder: No roads to randomize from!");
                return;
            }
        }

        var rnd = new System.Random();
        var segA = roadSegments[rnd.Next(roadSegments.Count)];
        var segB = roadSegments[rnd.Next(roadSegments.Count)];

        Vector3 randomA = (rnd.NextDouble() < 0.5) ? segA.start : segA.end;
        Vector3 randomB = (rnd.NextDouble() < 0.5) ? segB.start : segB.end;

        if (startPoint == null)
        {
            GameObject startObj = new GameObject("StartPoint");
            startObj.transform.parent = transform;
            startPoint = startObj.transform;
        }

        if (endPoint == null)
        {
            GameObject endObj = new GameObject("EndPoint");
            endObj.transform.parent = transform;
            endPoint = endObj.transform;
        }

        startPoint.position = randomA + Vector3.up * 2;
        endPoint.position = randomB + Vector3.up * 2;

        Debug.Log("CityPathfinder: Randomized start and end points.");
    }

    [ContextMenu("Clear Path")]
    public void ClearPath()
    {
        pathPoints.Clear();
        Debug.Log("CityPathfinder: Cleared path.");
    }

    // -----------------------------
    //  Core Pathfinding (A*)
    // -----------------------------
    private List<Vector3> AStar(Vector3 start, Vector3 goal)
    {
        if (graph == null || graph.Count == 0)
            graph = BuildGraph();

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

    private float Heuristic(Vector3 a, Vector3 b)
    {
        return Vector3.Distance(a, b);
    }

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

    // -----------------------------
    //  Utility Methods
    // -----------------------------
    private Vector3 SnapToNearestEndpoint(Vector3 position)
    {
        float minDist = float.MaxValue;
        Vector3 closest = position;

        foreach (var seg in roadSegments)
        {
            float da = Vector3.Distance(position, seg.start);
            float db = Vector3.Distance(position, seg.end);

            if (da < minDist)
            {
                minDist = da;
                closest = seg.start;
            }

            if (db < minDist)
            {
                minDist = db;
                closest = seg.end;
            }
        }

        return closest;
    }

    private Dictionary<Vector3, List<Vector3>> BuildGraph()
    {
        var graph = new Dictionary<Vector3, List<Vector3>>();

        foreach (var seg in roadSegments)
        {
            if (!graph.ContainsKey(seg.start))
                graph[seg.start] = new List<Vector3>();
            if (!graph.ContainsKey(seg.end))
                graph[seg.end] = new List<Vector3>();

            graph[seg.start].Add(seg.end);
            graph[seg.end].Add(seg.start);
        }

        return graph;
    }

    // -----------------------------
    //  Gizmos (Draw only shortest path)
    // -----------------------------
    void OnDrawGizmos()
    {
        if (!drawGizmos) return;

#if UNITY_EDITOR
        // Draw Start/End points
        if (startPoint != null)
        {
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(startPoint.position, 3f);
        }

        if (endPoint != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(endPoint.position, 3f);
        }

        // Draw only shortest path, elevated and thick
        if (pathPoints != null && pathPoints.Count > 1)
        {
            Handles.color = pathColor;
            Vector3[] elevatedPath = pathPoints.Select(p => p + Vector3.up * lineHeight).ToArray();
            Handles.DrawAAPolyLine(lineWidth, elevatedPath);
        }
#endif
    }
}
