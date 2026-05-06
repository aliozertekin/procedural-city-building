using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

[RequireComponent(typeof(Canvas))]
[RequireComponent(typeof(CanvasScaler))]
[RequireComponent(typeof(GraphicRaycaster))]
public class CityPathfinderUI : MonoBehaviour
{
    [Header("References")]
    public CityPathfinder pathfinder;
    public CityGenerator city;
    public TerrainGenerator terrain;
    public Font uiFont;

    [Header("UI")]
    public int panelWidth = 300;
    [Range(0.5f, 3f)] public float uiScale = 1.5f;

    // runtime
    private Text lengthText;
    private Dictionary<CityPathfinder.PathAlgo, Image> algoImgs = new();

    // ── Show / Hide path toggle state ──
    private bool pathVisible = true;
    private Text showHideBtnText;

    // =========================================================
    void Awake()
    {
        if (!pathfinder) pathfinder = FindFirstObjectByType<CityPathfinder>();
        if (!city) city = FindFirstObjectByType<CityGenerator>();
        if (!terrain) terrain = FindFirstObjectByType<TerrainGenerator>();

        EnsureEventSystem();
        BuildUI();
    }

    // =========================================================
    // UI BUILD
    // =========================================================
    void BuildUI()
    {
        Canvas c = GetComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay;

        CanvasScaler s = GetComponent<CanvasScaler>();
        s.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        s.referenceResolution = new Vector2(1920, 1080);

        transform.localScale = Vector3.one * uiScale;

        foreach (Transform t in transform)
            Destroy(t.gameObject);

        // ================= LEFT : PATHFINDING =================
        RectTransform left = CreatePanel(true);
        AddHeader(left, "PATHFINDING");

        foreach (CityPathfinder.PathAlgo algo in Enum.GetValues(typeof(CityPathfinder.PathAlgo)))
        {
            var a = algo;
            var btn = AddButton(left, algo.ToString(), () =>
            {
                pathfinder.SetAlgorithm(a);
                HighlightAlgo(a);
            });
            algoImgs[a] = btn.GetComponent<Image>();
        }
        HighlightAlgo(pathfinder.algorithm);

        AddSpacer(left, 8);

        // ── FIXED: RandomizePoint now works — road segments are loaded first ──
        AddButton(left, "RANDOM START", () =>
        {
            EnsureRoadsLoaded();
            RandomizePoint(true);
        });
        AddButton(left, "RANDOM END", () =>
        {
            EnsureRoadsLoaded();
            RandomizePoint(false);
        });

        AddSpacer(left, 6);

        AddButton(left, "CALCULATE PATH", () =>
        {
            pathfinder.LoadRoads();
            pathfinder.GeneratePath();
            // Make sure the path is visible after calculating
            SetPathVisible(true);
            UpdateLength();
        });

        // ── Show / Hide path — single toggle button ──
        var showHideBtn = AddButton(left, "HIDE PATH", TogglePathVisibility);
        showHideBtnText = showHideBtn.GetComponentInChildren<Text>();

        lengthText = AddLabel(left, "Length: -");

        // ================= RIGHT : SETTINGS =================
        RectTransform right = CreatePanel(false);

        // -------- CITY --------
        AddHeader(right, "CITY");

        AddIntField(right, "Seed", city.seed, v => city.seed = v);
        AddFloatField(right, "Size X", city.citySize.x, v => city.citySize.x = v);
        AddFloatField(right, "Size Z", city.citySize.y, v => city.citySize.y = v);
        AddFloatField(right, "Grid", city.gridBlockSize, v => city.gridBlockSize = v);
        AddFloatField(right, "Road Width", city.roadWidth, v => city.roadWidth = v);
        AddFloatField(right, "Center Density", city.centerDensity, v => city.centerDensity = Mathf.Clamp01(v));
        AddFloatField(right, "Edge Density", city.edgeDensity, v => city.edgeDensity = Mathf.Clamp01(v));

        AddButton(right, "GENERATE CITY", () =>
        {
            city.GenerateCity();
            pathfinder.LoadRoads();
        });

        AddButton(right, "CLEAR CITY", () =>
        {
            city.ClearCityHard();
            pathfinder.ResetPathfinder();
            UpdateLength();
        });

        AddHeader(right, "ROADBLOCKS");

        AddIntField(right, "Count", city.numRoadblocks, v =>
        {
            city.numRoadblocks = Mathf.Max(0, v);
        });

        AddFloatField(right, "Radius", city.roadBlockRadius, v =>
        {
            city.roadBlockRadius = Mathf.Max(0.1f, v);
        });

        AddButton(right, "REGENERATE BLOCKS", () =>
        {
            city.ClearPrevious();
            city.GenerateCity();
            pathfinder.LoadRoads();
        });

        // -------- TERRAIN --------
        if (terrain && terrain.settings)
        {
            AddSpacer(right, 10);
            AddHeader(right, "TERRAIN");

            AddIntField(right, "Seed", terrain.settings.seed, v => terrain.settings.seed = v);
            AddIntField(right, "Width", terrain.settings.terrainWidth, v => terrain.settings.terrainWidth = v);
            AddIntField(right, "Length", terrain.settings.terrainLength, v => terrain.settings.terrainLength = v);
            AddIntField(right, "Height", terrain.settings.terrainHeight, v => terrain.settings.terrainHeight = v);
            AddFloatField(right, "Tile Size", terrain.settings.textureTileSize, v => terrain.settings.textureTileSize = v);

            AddButton(right, "GENERATE TERRAIN + CITY", () =>
            {
                terrain.Generate();
                city.GenerateCity();
                pathfinder.LoadRoads();
            });
        }
    }

    // =========================================================
    // SHOW / HIDE PATH
    // =========================================================

    /// <summary>
    /// Toggles path gizmo visibility. The button label flips between
    /// "HIDE PATH" (path is showing) and "SHOW PATH" (path is hidden).
    /// </summary>
    void TogglePathVisibility()
    {
        SetPathVisible(!pathVisible);
    }

    void SetPathVisible(bool visible)
    {
        pathVisible = visible;
        pathfinder.drawGizmos = visible;

        if (showHideBtnText != null)
            showHideBtnText.text = visible ? "HIDE PATH" : "SHOW PATH";
    }

    // =========================================================
    // RANDOM START / END
    // =========================================================

    /// <summary>
    /// Makes sure the pathfinder has road segments before we try to
    /// pick a random one. Addresses the bug where the button did
    /// nothing when the roads hadn't been loaded yet.
    /// </summary>
    void EnsureRoadsLoaded()
    {
        var segs = city.GetRoadSegments();
        if (segs == null || segs.Count == 0)
        {
            // Nothing generated yet — nothing to randomise on
            Debug.LogWarning("CityPathfinderUI: No road segments available. Generate the city first.");
            return;
        }
        pathfinder.LoadRoads();
    }

    void RandomizePoint(bool start)
    {
        var segs = city.GetRoadSegments();
        if (segs == null || segs.Count == 0)
        {
            Debug.LogWarning("CityPathfinderUI: No road segments — generate city first.");
            return;
        }

        // Pick a random segment and a random position along it
        var seg = segs[UnityEngine.Random.Range(0, segs.Count)];
        Vector3 pos = Vector3.Lerp(seg.start, seg.end, UnityEngine.Random.value);

        if (start) pathfinder.SetStart(pos);
        else pathfinder.SetEnd(pos);

        // Auto-show the path when a new point is picked
        SetPathVisible(true);
        UpdateLength();
    }

    // =========================================================
    // UI HELPERS
    // =========================================================
    RectTransform CreatePanel(bool left)
    {
        GameObject go = new GameObject(left ? "LeftPanel" : "RightPanel");
        go.transform.SetParent(transform, false);

        Image img = go.AddComponent<Image>();
        img.color = new Color(0, 0, 0, 0.85f);

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = left ? new Vector2(0, 1) : new Vector2(1, 1);
        rt.pivot = rt.anchorMin;
        rt.anchoredPosition = left ? new Vector2(10, -10) : new Vector2(-10, -10);
        rt.sizeDelta = new Vector2(panelWidth, 0);

        var v = go.AddComponent<VerticalLayoutGroup>();
        v.padding = new RectOffset(8, 8, 8, 8);
        v.spacing = 6;
        v.childControlHeight = true;
        v.childForceExpandHeight = false;

        go.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        return rt;
    }

    Button AddButton(Transform parent, string label, Action onClick)
    {
        GameObject go = new GameObject(label);
        go.transform.SetParent(parent, false);

        Image img = go.AddComponent<Image>();
        img.color = new Color(0.25f, 0.25f, 0.25f);

        Button b = go.AddComponent<Button>();
        b.onClick.AddListener(() => onClick());

        Text t = new GameObject("Text").AddComponent<Text>();
        t.transform.SetParent(go.transform, false);
        t.font = GetFont();
        t.text = label;
        t.color = Color.white;
        t.alignment = TextAnchor.MiddleCenter;
        t.fontSize = 14;

        RectTransform tr = t.rectTransform;
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = tr.offsetMax = Vector2.zero;

        go.AddComponent<LayoutElement>().minHeight = 32;
        return b;
    }

    void AddHeader(Transform parent, string txt)
    {
        Text t = AddLabel(parent, txt);
        t.fontStyle = FontStyle.Bold;
        t.color = Color.yellow;
        t.fontSize = 16;
    }

    Text AddLabel(Transform parent, string txt)
    {
        Text t = new GameObject("Label").AddComponent<Text>();
        t.transform.SetParent(parent, false);
        t.font = GetFont();
        t.text = txt;
        t.color = Color.white;
        t.fontSize = 13;
        t.alignment = TextAnchor.MiddleCenter;
        return t;
    }

    void AddSpacer(Transform parent, float h)
    {
        GameObject g = new GameObject("Spacer");
        g.transform.SetParent(parent, false);
        g.AddComponent<LayoutElement>().minHeight = h;
    }

    void AddIntField(Transform parent, string label, int value, Action<int> onChange)
    {
        var row = CreateFieldRow(parent);
        var input = CreateInput(row, value.ToString());
        input.onEndEdit.AddListener(v =>
        {
            if (int.TryParse(v, out int r))
                onChange(r);
        });
        AddLabel(row, label);
    }

    void AddFloatField(Transform parent, string label, float value, Action<float> onChange)
    {
        var row = CreateFieldRow(parent);
        var input = CreateInput(row, value.ToString("0.##"));
        input.onEndEdit.AddListener(v =>
        {
            if (float.TryParse(v, out float r))
                onChange(r);
        });
        AddLabel(row, label);
    }

    Transform CreateFieldRow(Transform parent)
    {
        GameObject r = new GameObject("Row");
        r.transform.SetParent(parent, false);
        r.AddComponent<HorizontalLayoutGroup>().spacing = 6;
        r.AddComponent<LayoutElement>().minHeight = 30;
        return r.transform;
    }

    InputField CreateInput(Transform parent, string value)
    {
        GameObject go = new GameObject("Input");
        go.transform.SetParent(parent, false);

        Image img = go.AddComponent<Image>();
        img.color = new Color(0.15f, 0.15f, 0.15f);

        InputField f = go.AddComponent<InputField>();
        Text t = new GameObject("Text").AddComponent<Text>();
        t.transform.SetParent(go.transform, false);
        t.font = GetFont();
        t.text = value;
        t.color = Color.white;
        t.fontSize = 13;

        f.textComponent = t;
        f.text = value;

        RectTransform tr = t.rectTransform;
        tr.anchorMin = Vector2.zero;
        tr.anchorMax = Vector2.one;
        tr.offsetMin = tr.offsetMax = Vector2.zero;

        go.AddComponent<LayoutElement>().minWidth = 90;
        return f;
    }

    // =========================================================
    void HighlightAlgo(CityPathfinder.PathAlgo a)
    {
        foreach (var kv in algoImgs)
            kv.Value.color = kv.Key == a
                ? new Color(0, 0.5f, 1f)
                : new Color(0.25f, 0.25f, 0.25f);
    }

    void UpdateLength()
    {
        if (lengthText == null) return;

        var f = typeof(CityPathfinder).GetField("pathPoints",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

        var list = f?.GetValue(pathfinder) as List<Vector3>;
        if (list == null || list.Count < 2)
        {
            lengthText.text = "Length: -";
            return;
        }

        float d = 0f;
        for (int i = 1; i < list.Count; i++)
            d += Vector3.Distance(list[i - 1], list[i]);

        lengthText.text = $"Length: {d:F1}";
    }

    Font GetFont() =>
        uiFont ? uiFont : Resources.GetBuiltinResource<Font>("Arial.ttf");

    void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>()) return;
        var g = new GameObject("EventSystem");
        g.AddComponent<EventSystem>();
        g.AddComponent<StandaloneInputModule>();
    }
}