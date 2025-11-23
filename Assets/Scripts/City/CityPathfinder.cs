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
    private Camera mainCam;

    [Header("Gizmo Settings")]
    public float lineHeight = 3f;
    public float lineWidth = 0.3f;
    public Color pathColor = Color.red;
    public float sphereRadius = 2f;

    void OnValidate()
    {
        if (generator == null)
            generator = Object.FindFirstObjectByType<CityGenerator>();
    }

    void OnEnable()
    {
        LoadRoads();
    }

    void Update()
    {
        if (mainCam == null)
            mainCam = Camera.main;

        HandleKeyInput();

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

    private float Heuristic(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

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

        // Use all road segment endpoints
        foreach (var seg in roadSegments)
        {
            Vector3[] endpoints = { seg.start, seg.end };
            foreach (var pt in endpoints)
            {
                float dist = Vector3.Distance(position, pt);
                if (dist < minDist)
                {
                    minDist = dist;
                    closest = pt;
                }
            }
        }

        return closest;
    }


    private Dictionary<Vector3, List<Vector3>> BuildGraph()
    {
        var graph = new Dictionary<Vector3, List<Vector3>>();

        foreach (var seg in roadSegments)
        {
            if (IsSegmentBlocked(seg))
                continue;

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
    //  Roadblock check
    // -----------------------------
    private bool IsSegmentBlocked(RoadSegment seg)
    {
        if (generator == null || generator.roadBlocks == null) return false;

        Vector3 mid = (seg.start + seg.end) * 0.5f;

        foreach (var block in generator.roadBlocks)
        {
            if (block == null) continue;

            // use the roadblock's stored position and radius
            float blockRadius = block.radius;
            // also consider road width to be safe
            float roadWidth = 0f;
            if (generator != null) roadWidth = generator.GetRoadWidth();

            float threshold = blockRadius + (roadWidth * 0.5f);

            if (Vector3.Distance(mid, block.position) <= threshold)
                return true;
        }

        return false;
    }

    // -----------------------------
    //  Input Handling (New Input System)
    // -----------------------------
    private void HandleKeyInput()
    {
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current == null || roadSegments == null || roadSegments.Count == 0) return;
        if (mainCam == null) return;

        Vector3? nearestPoint = GetNearestRoadPointToMouse();

        if (nearestPoint.HasValue)
        {
            if (Keyboard.current.bKey.wasPressedThisFrame)
            {
                if (startPoint == null)
                {
                    GameObject go = new GameObject("StartPoint");
                    go.transform.parent = transform;
                    startPoint = go.transform;
                }
                startPoint.position = nearestPoint.Value + Vector3.up * 2f;
                Debug.Log("CityPathfinder: Start point set.");
            }

            if (Keyboard.current.nKey.wasPressedThisFrame)
            {
                if (endPoint == null)
                {
                    GameObject go = new GameObject("EndPoint");
                    go.transform.parent = transform;
                    endPoint = go.transform;
                }
                endPoint.position = nearestPoint.Value + Vector3.up * 2f;
                Debug.Log("CityPathfinder: End point set.");
            }
        }

        if (Keyboard.current.mKey.wasPressedThisFrame)
        {
            GeneratePath();
        }
#endif
    }

    private Vector3? GetNearestRoadPointToMouse()
    {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current == null || mainCam == null || roadSegments == null || roadSegments.Count == 0) return null;

        Vector3 mousePos = Mouse.current.position.ReadValue();
        Ray ray = mainCam.ScreenPointToRay(mousePos);
        Plane groundPlane = new Plane(Vector3.up, Vector3.zero);

        if (groundPlane.Raycast(ray, out float enter))
        {
            Vector3 hit = ray.GetPoint(enter);

            float minDist = float.MaxValue;
            Vector3 closest = hit;

            foreach (var seg in roadSegments)
            {
                float da = Vector3.Distance(hit, seg.start);
                float db = Vector3.Distance(hit, seg.end);

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
#endif
        return null;
    }

    // -----------------------------
    //  Gizmos
    // -----------------------------
    void OnDrawGizmos()
    {
        if (!drawGizmos) return;

#if UNITY_EDITOR
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
            Vector3[] elevatedPath = pathPoints.Select(p => p + Vector3.up * lineHeight).ToArray();
            Handles.DrawAAPolyLine(lineWidth, elevatedPath);
        }
#endif
    }
}
