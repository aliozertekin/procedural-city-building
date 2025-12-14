// CityPathfinderUI.cs
// Drop-in UI controller for CityPathfinder (robust runtime-created UI).
// Works with legacy Input or new Input System (compile-guarded).
// Calls into your existing CityPathfinder public API: SetStart, SetEnd, SetAlgorithm, GeneratePath.

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
using UnityEngine.InputSystem;
#endif

[RequireComponent(typeof(Canvas))]
public class CityPathfinderUI : MonoBehaviour
{
    [Header("References (optional, auto-find if null)")]
    public CityPathfinder pathfinder;
    public CityGenerator generator;
    public Font uiFont;

    [Header("Layout")]
    public int panelWidth = 340;

    // internal UI
    private Button algoButton;
    private Button placeStartButton;
    private Button placeEndButton;
    private Button calcButton;
    private Text statusText;
    private Text lengthText;

    // placement mode
    private enum PlaceMode { None, Start, End }
    private PlaceMode placeMode = PlaceMode.None;

    private Camera mainCam;

    // enum values cached
    private Array algoValues;

    void Awake()
    {
        // safe finds
        if (pathfinder == null)
            pathfinder = FindFirstObjectByType<CityPathfinder>();
        if (generator == null)
            generator = FindFirstObjectByType<CityGenerator>();

        mainCam = Camera.main;

        // prepare enum list from your PathAlgo
        algoValues = Enum.GetValues(typeof(CityPathfinder.PathAlgo));

        // ensure event system appropriate for input backend
        EnsureEventSystemForInput();

        // build UI
        BuildRuntimeUI();

        // initialize UI state
        SyncUIFromPathfinder();
    }

    void OnEnable()
    {
        // ensure main camera reference at runtime
        if (mainCam == null) mainCam = Camera.main;
    }

    void Update()
    {
        // Only handle placement when requested
        if (placeMode == PlaceMode.None) return;

        // block if pointer is over UI
        if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            return;

        bool clicked = false;

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        // New Input System: use mouse current if available
        if (Mouse.current != null)
            clicked = Mouse.current.leftButton.wasPressedThisFrame;
        else
            clicked = UnityEngine.Input.GetMouseButtonDown(0); // fallback
#else
        // Legacy input
        clicked = UnityEngine.Input.GetMouseButtonDown(0);
#endif

        if (!clicked) return;

        // Raycast against physics or use plane fallback (y=0) if no collider
        Vector3 worldPos;
        if (RaycastScene(out worldPos))
        {
            // use API on pathfinder to set points (it does snapping internally)
            if (placeMode == PlaceMode.Start)
            {
                pathfinder?.SetStart(worldPos);
                statusText.text = "Start placed";
            }
            else // End
            {
                pathfinder?.SetEnd(worldPos);
                statusText.text = "End placed";
            }
        }
        else
        {
            // fallback: use projection on y=0 plane
            Ray r = mainCam.ScreenPointToRay(GetPointerPosition());
            Plane plane = new Plane(Vector3.up, Vector3.zero);
            if (plane.Raycast(r, out float t))
            {
                Vector3 pos = r.GetPoint(t);
                if (placeMode == PlaceMode.Start)
                {
                    pathfinder?.SetStart(pos);
                    statusText.text = "Start placed (plane)";
                }
                else
                {
                    pathfinder?.SetEnd(pos);
                    statusText.text = "End placed (plane)";
                }
            }
            else
            {
                statusText.text = "Could not place point (no ray/plane)";
            }
        }

        placeMode = PlaceMode.None;
    }

    // --------------------------
    // UI Construction
    // --------------------------
    private void BuildRuntimeUI()
    {
        // Canvas (component exists due to RequireComponent)
        Canvas canvas = GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 999;

        // Add CanvasScaler + GraphicRaycaster if missing
        if (!TryGetComponent(out CanvasScaler _))
        {
            var s = gameObject.AddComponent<CanvasScaler>();
            s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            s.referenceResolution = new Vector2(1920, 1080);
        }
        if (!TryGetComponent(out GraphicRaycaster _))
            gameObject.AddComponent<GraphicRaycaster>();

        // Panel root
        GameObject panel = UIObj("PathfinderPanel", transform);
        RectTransform prt = panel.GetComponent<RectTransform>();
        prt.anchorMin = new Vector2(0, 1);
        prt.anchorMax = new Vector2(0, 1);
        prt.pivot = new Vector2(0, 1);
        prt.anchoredPosition = new Vector2(10, -10);
        prt.sizeDelta = new Vector2(panelWidth, 300);
        var img = panel.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.75f);

        float y = -12f;
        float left = 12f;
        float w = panelWidth - 24f;

        // Title
        Text title = UIText("City Pathfinder", panel.transform, left, y, w, 26);
        title.fontStyle = FontStyle.Bold;
        y -= 34f;

        // Algorithm cycle button (stable alternative to runtime dropdown)
        algoButton = UIButton("Algorithm: ?", panel.transform, left, y, w, 34, () =>
        {
            CycleAlgorithm();
        });
        y -= 40f;

        // Place start/end buttons (side-by-side)
        float halfW = (w - 8f) * 0.5f;
        placeStartButton = UIButton("Place Start", panel.transform, left, y, halfW, 34, () =>
        {
            placeMode = PlaceMode.Start;
            statusText.text = "Click the scene to place START";
        });

        placeEndButton = UIButton("Place End", panel.transform, left + halfW + 8f, y, halfW, 34, () =>
        {
            placeMode = PlaceMode.End;
            statusText.text = "Click the scene to place END";
        });
        y -= 44f;

        // Calculate / Generate path
        calcButton = UIButton("Calculate Path", panel.transform, left, y, w, 36, () =>
        {
            if (pathfinder == null)
            {
                statusText.text = "No pathfinder assigned";
                return;
            }

            // ensure graph loaded
            TryEnsureRoadsLoaded();

            pathfinder.GeneratePath();

            // update stats
            UpdatePathStatsIntoUI();

            statusText.text = "Path calculated";
        });
        y -= 46f;

        // Path length + status text
        lengthText = UIText("Length: -", panel.transform, left, y, w, 20);
        y -= 22f;
        statusText = UIText("Status: Idle", panel.transform, left, y, w, 40);
        statusText.horizontalOverflow = HorizontalWrapMode.Wrap;

        // final init
        SyncUIFromPathfinder();
    }

    // --------------------------
    // Helpers: UI element builders
    // --------------------------
    GameObject UIObj(string name, Transform parent)
    {
        GameObject go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<RectTransform>();
        return go;
    }

    Text UIText(string txt, Transform p, float x, float y, float w, float h)
    {
        Text t = UIObj("Text", p).AddComponent<Text>();
        t.font = uiFont ? uiFont : Resources.GetBuiltinResource<Font>("Arial.ttf");
        t.text = txt;
        t.color = Color.white;
        t.raycastTarget = false;
        SetRect(t.rectTransform, x, y, w, h);
        return t;
    }

    Button UIButton(string label, Transform p, float x, float y, float w, float h, Action onClick)
    {
        GameObject go = UIObj("Button_" + label, p);
        Image img = go.AddComponent<Image>();
        img.color = new Color(0.2f, 0.2f, 0.2f);
        Button b = go.AddComponent<Button>();
        SetRect(go.GetComponent<RectTransform>(), x, y, w, h);

        Text t = UIText(label, go.transform, 0, 0, w, h);
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;

        b.onClick.AddListener(() => onClick());
        return b;
    }

    void SetRect(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(0, 1);
        rt.pivot = new Vector2(0, 1);
        rt.anchoredPosition = new Vector2(x, y);
        rt.sizeDelta = new Vector2(w, h);
    }

    // --------------------------
    // Algorithm handling
    // --------------------------
    void SyncUIFromPathfinder()
    {
        if (pathfinder == null) return;
        var cur = pathfinder.algorithm;
        algoButton.GetComponentInChildren<Text>().text = $"Algorithm: {cur}";
    }

    void CycleAlgorithm()
    {
        if (pathfinder == null) return;

        // find current index in enum values
        CityPathfinder.PathAlgo current = pathfinder.algorithm;
        int idx = Array.IndexOf(algoValues, current);
        idx = (idx + 1) % algoValues.Length;
        CityPathfinder.PathAlgo next = (CityPathfinder.PathAlgo)algoValues.GetValue(idx);

        pathfinder.SetAlgorithm(next);
        algoButton.GetComponentInChildren<Text>().text = $"Algorithm: {next}";
        statusText.text = $"Algorithm set to {next}";
    }

    // --------------------------
    // Path stats (reads private pathPoints field by reflection)
    // --------------------------
    void UpdatePathStatsIntoUI()
    {
        if (pathfinder == null || lengthText == null) return;

        try
        {
            FieldInfo f = typeof(CityPathfinder).GetField("pathPoints", BindingFlags.NonPublic | BindingFlags.Instance);
            var list = f?.GetValue(pathfinder) as List<Vector3>;
            if (list == null || list.Count < 2)
            {
                lengthText.text = "Length: -";
                statusText.text = "No path";
                return;
            }

            float len = 0f;
            for (int i = 1; i < list.Count; i++) len += Vector3.Distance(list[i - 1], list[i]);
            lengthText.text = $"Length: {len:F2}  Nodes: {list.Count}";
        }
        catch (Exception ex)
        {
            lengthText.text = "Length: ? (error)";
            Debug.LogWarning("CityPathfinderUI: failed to read pathPoints: " + ex);
        }
    }

    // --------------------------
    // Raycasting helper
    // --------------------------
    bool RaycastScene(out Vector3 worldPos)
    {
        worldPos = Vector3.zero;

        if (mainCam == null)
        {
            mainCam = Camera.main;
            if (mainCam == null) return false;
        }

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        Vector2 pointer = GetPointerPosition();
        Ray ray = mainCam.ScreenPointToRay(pointer);
#else
        Ray ray = mainCam.ScreenPointToRay(GetPointerPosition());
#endif

        if (Physics.Raycast(ray, out RaycastHit hit, 1000f))
        {
            worldPos = hit.point;
            return true;
        }
        return false;
    }

    Vector2 GetPointerPosition()
    {
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        if (Pointer.current != null) return Pointer.current.position.ReadValue();
        if (Mouse.current != null) return Mouse.current.position.ReadValue();
        return Vector2.zero;
#else
        return UnityEngine.Input.mousePosition;
#endif
    }

    // --------------------------
    // Road loading
    // --------------------------
    void TryEnsureRoadsLoaded()
    {
        if (pathfinder == null) return;

        // if graph seems empty try to call LoadRoads on pathfinder
        FieldInfo f = typeof(CityPathfinder).GetField("graph", BindingFlags.NonPublic | BindingFlags.Instance);
        var gObj = f?.GetValue(pathfinder) as Dictionary<Vector3, List<Vector3>>;
        if (gObj == null || gObj.Count == 0)
        {
            // invoke public LoadRoads if available
            MethodInfo m = typeof(CityPathfinder).GetMethod("LoadRoads", BindingFlags.Public | BindingFlags.Instance);
            m?.Invoke(pathfinder, null);
        }
    }

    // --------------------------
    // EventSystem / Input setup
    // --------------------------
    void EnsureEventSystemForInput()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;

#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
        // If the new Input System is in use, create an EventSystem with InputSystemUIInputModule if available
        var go = new GameObject("EventSystem");
        var es = go.AddComponent<EventSystem>();
        // Try to add InputSystemUIInputModule via reflection (to avoid compile errors when package missing)
        var asm = AppDomain.CurrentDomain.GetAssemblies();
        Type inputModuleType = null;
        foreach (var a in asm)
        {
            try
            {
                inputModuleType = a.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule");
                if (inputModuleType != null) break;
            }
            catch { }
        }
        if (inputModuleType != null)
        {
            go.AddComponent(inputModuleType);
        }
        else
        {
            // fallback to StandaloneInputModule if InputSystemUIInputModule not found
            go.AddComponent<StandaloneInputModule>();
        }
#else
        // Legacy input: StandaloneInputModule
        var go = new GameObject("EventSystem");
        go.AddComponent<EventSystem>();
        go.AddComponent<StandaloneInputModule>();
#endif
    }
}
