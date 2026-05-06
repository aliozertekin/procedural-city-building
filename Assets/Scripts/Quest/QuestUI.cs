using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Canvas))]
[RequireComponent(typeof(CanvasScaler))]
[RequireComponent(typeof(GraphicRaycaster))]
public class QuestUI : MonoBehaviour
{
    [Header("References")]
    public QuestSystem questSystem;
    public Font uiFont;

    // ── runtime UI refs ──
    private Canvas canvas;
    private Font font;

    // Left panel (quest list with category tabs)
    private GameObject leftPanel;
    private readonly List<GameObject> questRows = new List<GameObject>();

    // Track which category tabs are expanded (true = open).
    // Persists across Refresh() calls so the user's toggle choices are remembered.
    private readonly Dictionary<string, bool> expandedCategories =
        new Dictionary<string, bool>();

    // Right panel (active quest)
    private GameObject rightPanel;
    private Text activeTitleTxt;
    private Text activeStepTxt;
    private Text rewardTxt;
    private Text pointsTxt;
    private GameObject abandonBtnGo;

    // Notification
    private GameObject notifGo;
    private Text notifTxt;
    private float notifTimer;

    // Navigation compass arrow (bottom-centre of screen)
    private GameObject compassGo;
    private RectTransform compassArrowRT;
    private Text compassDistTxt;
    public Camera playerCamera;

    // ── Minimap ──
    public Transform playerTransform;
    private GameObject minimapPanel;
    private RawImage minimapImage;
    private GameObject playerDot;
    private GameObject questDot;
    private RenderTexture minimapRT;
    private Camera minimapCam;

    const float MINIMAP_RANGE = 120f;
    const int MINIMAP_SIZE = 256;
    const int MINIMAP_PX = 260;

    // Layout constants
    const int LEFT_X = 10;
    const int RIGHT_X = -10;
    const int PANEL_W = 310;
    const int TOP_Y = -10;
    const int PADDING = 10;
    const int SPACING = 3;

    // ─────────────────────────────────────────
    void Awake()
    {
        font = uiFont ? uiFont : Resources.GetBuiltinResource<Font>("Arial.ttf");
        canvas = GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 20;

        var scaler = GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;

        if (questSystem == null)
            questSystem = FindFirstObjectByType<QuestSystem>();

        Build();
        Subscribe();
    }

    void Start() => Refresh();

    void OnEnable()
    {
        if (leftPanel != null)
        {
            Refresh();
            Canvas.ForceUpdateCanvases();
        }
    }

    void OnDestroy()
    {
        if (questSystem == null) return;
        questSystem.OnQuestsChanged -= Refresh;
        questSystem.OnQuestAccepted -= q => ShowNotif($"Quest started: {q.title}", new Color(0.3f, 1f, 0.3f));
        questSystem.OnQuestCompleted -= q => ShowNotif($"Quest complete! +{q.rewardPoints} pts", Color.yellow);
    }

    void Subscribe()
    {
        if (questSystem == null) return;
        questSystem.OnQuestsChanged += Refresh;
        questSystem.OnQuestAccepted += q => ShowNotif($"Quest started: {q.title}", new Color(0.3f, 1f, 0.3f));
        questSystem.OnStepCompleted += (q, i) => ShowNotif("Objective complete — keep moving!", Color.cyan);
        questSystem.OnQuestCompleted += q => ShowNotif($"Quest complete! +{q.rewardPoints} pts", Color.yellow);
    }

    // ─────────────────────────────────────────
    //  BUILD  (called once)
    // ─────────────────────────────────────────

    void Build()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
        questRows.Clear();

        // ── LEFT PANEL ──────────────────────────────────────────────────
        leftPanel = MakeBox(transform, "LeftPanel",
            anchor: new Vector2(0, 1), pivot: new Vector2(0, 1),
            pos: new Vector2(LEFT_X, TOP_Y),
            size: new Vector2(PANEL_W, 400));

        // Panel header bar
        var headerBg = new GameObject("HeaderBg");
        headerBg.transform.SetParent(leftPanel.transform, false);
        var hImg = headerBg.AddComponent<Image>();
        hImg.color = new Color(0.05f, 0.05f, 0.1f, 1f);
        var hrt = headerBg.GetComponent<RectTransform>();
        hrt.anchorMin = new Vector2(0, 1);
        hrt.anchorMax = new Vector2(1, 1);
        hrt.pivot = new Vector2(0.5f, 1);
        hrt.anchoredPosition = Vector2.zero;
        hrt.sizeDelta = new Vector2(0, 32);

        MakeLabelAt(leftPanel.transform, "QUEST BOARD", PADDING - 2, 24, 14,
                    new Color(1f, 0.85f, 0.1f), bold: true, xOff: 4);

        // ── RIGHT PANEL ──────────────────────────────────────────────────
        rightPanel = MakeBox(transform, "RightPanel",
            anchor: new Vector2(1, 1), pivot: new Vector2(1, 1),
            pos: new Vector2(RIGHT_X, TOP_Y),
            size: new Vector2(PANEL_W, 60));
        activeTitleTxt = null;
        activeStepTxt = null;
        rewardTxt = null;
        pointsTxt = null;
        abandonBtnGo = null;

        // ── NOTIFICATION BANNER ──────────────────────────────────────────
        notifGo = MakeBox(transform, "Notif",
            anchor: new Vector2(0.5f, 1f), pivot: new Vector2(0.5f, 1f),
            pos: new Vector2(0, -10),
            size: new Vector2(700, 50));
        notifGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.82f);

        notifTxt = MakeLabel(notifGo.transform, "", 0, 18, Color.white, bold: true, centered: true);
        var nrt = notifTxt.rectTransform;
        nrt.anchorMin = Vector2.zero; nrt.anchorMax = Vector2.one;
        nrt.offsetMin = nrt.offsetMax = Vector2.zero;
        notifGo.SetActive(false);

        // ── COMPASS ARROW ────────────────────────────────────────────────
        compassGo = MakeBox(transform, "Compass",
            anchor: new Vector2(0.5f, 0f), pivot: new Vector2(0.5f, 0f),
            pos: new Vector2(0, 20),
            size: new Vector2(120, 80));
        compassGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.75f);

        var arrowGo = new GameObject("Arrow");
        arrowGo.transform.SetParent(compassGo.transform, false);
        var arrowTxt = arrowGo.AddComponent<Text>();
        arrowTxt.font = font;
        arrowTxt.text = "▲";
        arrowTxt.fontSize = 32;
        arrowTxt.color = new Color(1f, 0.9f, 0.1f);
        arrowTxt.alignment = TextAnchor.MiddleCenter;
        compassArrowRT = arrowGo.GetComponent<RectTransform>();
        compassArrowRT.anchorMin = new Vector2(0.5f, 0.5f);
        compassArrowRT.anchorMax = new Vector2(0.5f, 0.5f);
        compassArrowRT.pivot = new Vector2(0.5f, 0.5f);
        compassArrowRT.anchoredPosition = new Vector2(0, 10);
        compassArrowRT.sizeDelta = new Vector2(40, 40);

        compassDistTxt = MakeLabel(compassGo.transform, "", 48, 12, Color.white, centered: true);
        var crt = compassDistTxt.rectTransform;
        crt.anchorMin = new Vector2(0, 0); crt.anchorMax = new Vector2(1, 0);
        crt.pivot = new Vector2(0.5f, 0);
        crt.anchoredPosition = new Vector2(0, 6);
        crt.sizeDelta = new Vector2(0, 18);
        compassGo.SetActive(false);

        // ── MINIMAP PANEL ────────────────────────────────────────────────
        minimapPanel = MakeBox(transform, "MinimapPanel",
            anchor: new Vector2(1, 1), pivot: new Vector2(1, 1),
            pos: new Vector2(RIGHT_X, -238),
            size: new Vector2(PANEL_W, MINIMAP_PX + 28));
        minimapPanel.GetComponent<Image>().color = new Color(0, 0, 0, 0.85f);

        MakeLabel(minimapPanel.transform, "MINIMAP", 0, 14, Color.yellow, bold: true);

        var rimGo = new GameObject("MinimapRT");
        rimGo.transform.SetParent(minimapPanel.transform, false);
        minimapImage = rimGo.AddComponent<RawImage>();
        minimapImage.color = Color.white;

        var rimRT = rimGo.GetComponent<RectTransform>();
        rimRT.anchorMin = new Vector2(0, 0);
        rimRT.anchorMax = new Vector2(1, 1);
        rimRT.pivot = new Vector2(0.5f, 0.5f);
        rimRT.offsetMin = new Vector2(4, 4);
        rimRT.offsetMax = new Vector2(-4, -26);

        playerDot = MakeMinimapDot(minimapPanel.transform, "●", Color.white, 14);
        questDot = MakeMinimapDot(minimapPanel.transform, "★", Color.yellow, 16);

        BuildMinimapCamera();
    }

    void BuildMinimapCamera()
    {
        minimapRT = new RenderTexture(MINIMAP_SIZE, MINIMAP_SIZE, 16);
        minimapRT.name = "MinimapRT";
        if (minimapImage != null) minimapImage.texture = minimapRT;

        var camGo = new GameObject("MinimapCamera");
        minimapCam = camGo.AddComponent<Camera>();
        minimapCam.orthographic = true;
        minimapCam.orthographicSize = MINIMAP_RANGE;
        minimapCam.nearClipPlane = 1f;
        minimapCam.farClipPlane = 2000f;
        minimapCam.targetTexture = minimapRT;
        minimapCam.clearFlags = CameraClearFlags.SolidColor;
        minimapCam.backgroundColor = new Color(0.13f, 0.22f, 0.13f);
        minimapCam.cullingMask = ~(1 << LayerMask.NameToLayer("UI"));
        camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
    }

    GameObject MakeMinimapDot(Transform parent, string symbol, Color color, int size)
    {
        var go = new GameObject("Dot_" + symbol);
        go.transform.SetParent(parent, false);
        var txt = go.AddComponent<Text>();
        txt.font = font;
        txt.text = symbol;
        txt.fontSize = size;
        txt.color = color;
        txt.alignment = TextAnchor.MiddleCenter;
        txt.horizontalOverflow = HorizontalWrapMode.Overflow;
        txt.verticalOverflow = VerticalWrapMode.Overflow;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 0);
        rt.anchorMax = new Vector2(0, 0);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(20, 20);
        return go;
    }

    // ─────────────────────────────────────────
    //  REFRESH
    // ─────────────────────────────────────────

    public void Refresh()
    {
        if (questSystem == null)
            questSystem = FindFirstObjectByType<QuestSystem>();
        if (questSystem == null) return;
        if (leftPanel == null) return;

        RefreshLeft();
        RefreshRight();
    }

    // ─────────────────────────────────────────
    //  LEFT PANEL — category tabs + quest rows
    // ─────────────────────────────────────────

    void RefreshLeft()
    {
        foreach (var r in questRows) if (r) Destroy(r);
        questRows.Clear();

        var quests = questSystem.GetAllQuests();
        int innerW = PANEL_W - PADDING * 2;

        // Group quests by category, preserving encounter order
        var categoryOrder = new List<string>();
        var groups = new Dictionary<string, List<Quest>>();
        foreach (var q in quests)
        {
            if (!groups.ContainsKey(q.category))
            {
                groups[q.category] = new List<Quest>();
                categoryOrder.Add(q.category);
            }
            groups[q.category].Add(q);
        }

        // Default any new category to expanded
        foreach (var cat in categoryOrder)
            if (!expandedCategories.ContainsKey(cat))
                expandedCategories[cat] = true;

        int yOffset = 34;   // below "QUEST BOARD" header

        foreach (var cat in categoryOrder)
        {
            var catQuests = groups[cat];
            bool expanded = expandedCategories.TryGetValue(cat, out bool e) && e;

            Color catColor = GetCategoryColor(cat);
            string arrow = expanded ? "▼" : "▶";
            int completed = catQuests.Count(q => q.status == QuestStatus.Completed);

            // ── Category tab header ──────────────────────────────────────
            var tabRow = MakeBox(leftPanel.transform, $"Tab_{cat}",
                anchor: new Vector2(0, 1), pivot: new Vector2(0, 1),
                pos: new Vector2(0, -yOffset),
                size: new Vector2(PANEL_W, 28));

            // Background: darkened version of category colour
            tabRow.GetComponent<Image>().color = new Color(
                catColor.r * 0.18f,
                catColor.g * 0.18f,
                catColor.b * 0.22f,
                0.98f);

            // Left accent stripe
            var stripe = new GameObject("Stripe");
            stripe.transform.SetParent(tabRow.transform, false);
            stripe.AddComponent<Image>().color = catColor;
            var srt = stripe.GetComponent<RectTransform>();
            srt.anchorMin = new Vector2(0, 0);
            srt.anchorMax = new Vector2(0, 1);
            srt.pivot = new Vector2(0, 0.5f);
            srt.anchoredPosition = Vector2.zero;
            srt.sizeDelta = new Vector2(4, 0);

            // Tab label: "▼  DELIVERY  (2/3)"
            string tabLabel = $"{arrow}  {cat.ToUpper()}";
            string countLabel = completed > 0
                ? $"({completed}/{catQuests.Count} done)"
                : $"({catQuests.Count})";

            MakeLabelAt(tabRow.transform, tabLabel, 3, 22, 12,
                        catColor, bold: true, xOff: 10);
            MakeLabelAt(tabRow.transform, countLabel, 5, 18, 11,
                        new Color(catColor.r, catColor.g, catColor.b, 0.7f),
                        bold: false, xOff: 0, rightAnchored: true);

            // Make the tab clickable
            var btn = tabRow.AddComponent<Button>();
            var bc = btn.colors;
            bc.normalColor = Color.white;
            bc.highlightedColor = new Color(1.15f, 1.15f, 1.15f);
            bc.pressedColor = new Color(0.80f, 0.80f, 0.80f);
            bc.colorMultiplier = 1f;
            btn.colors = bc;
            var capturedCat = cat;
            btn.onClick.AddListener(() =>
            {
                expandedCategories[capturedCat] = !expandedCategories[capturedCat];
                Refresh();
            });

            questRows.Add(tabRow);
            yOffset += 28 + 2;

            if (!expanded) continue;

            // ── Quest rows under this category ────────────────────────────
            foreach (var q in catQuests)
            {
                bool canAccept = q.status == QuestStatus.Available
                                 && questSystem.GetActiveQuest() == null;

                int titleH = MeasureTextHeight(q.title, 13, innerW - 8);
                int descH = MeasureTextHeight(q.description, 11, innerW - 8);
                int stepsH = 0;

                // Show step count hint when available
                string stepsHint = q.steps.Count > 1
                    ? $"{q.steps.Count} objectives"
                    : "1 objective";
                int stepsHintH = 16;

                int btnH = canAccept ? 26 : 0;
                int rowH = PADDING + titleH + 2 + 16 + 2 + stepsHintH + 2
                           + descH + (btnH > 0 ? 4 + btnH : 0) + PADDING;

                var row = MakeBox(leftPanel.transform, $"Row_{q.id}",
                    anchor: new Vector2(0, 1), pivot: new Vector2(0, 1),
                    pos: new Vector2(6, -yOffset),
                    size: new Vector2(PANEL_W - 6, rowH));

                // Row background
                row.GetComponent<Image>().color = q.status switch
                {
                    QuestStatus.Active => new Color(0.08f, 0.22f, 0.42f, 0.97f),
                    QuestStatus.Completed => new Color(0.08f, 0.28f, 0.08f, 0.95f),
                    _ => new Color(0.13f, 0.13f, 0.16f, 0.97f)
                };

                // Category color left accent on each quest row
                var rowStripe = new GameObject("RowStripe");
                rowStripe.transform.SetParent(row.transform, false);
                rowStripe.AddComponent<Image>().color = new Color(
                    catColor.r, catColor.g, catColor.b, 0.5f);
                var rsrt = rowStripe.GetComponent<RectTransform>();
                rsrt.anchorMin = new Vector2(0, 0);
                rsrt.anchorMax = new Vector2(0, 1);
                rsrt.pivot = new Vector2(0, 0.5f);
                rsrt.anchoredPosition = Vector2.zero;
                rsrt.sizeDelta = new Vector2(3, 0);

                // Badge
                string badge = q.status switch
                {
                    QuestStatus.Active => "[ ACTIVE ]",
                    QuestStatus.Completed => "[ DONE ]",
                    _ => $"+ {q.rewardPoints} pts"
                };
                Color badgeCol = q.status switch
                {
                    QuestStatus.Active => new Color(0.4f, 0.85f, 1.0f),
                    QuestStatus.Completed => new Color(0.4f, 1.0f, 0.4f),
                    _ => new Color(1f, 0.85f, 0.2f)
                };

                int yLocal = PADDING;

                // Title
                MakeLabelAt(row.transform, q.title, yLocal, titleH, 13,
                            Color.white, bold: true, xOff: 8);
                yLocal += titleH + 2;

                // Badge (right-aligned, same line offset as title bottom)
                MakeLabelAt(row.transform, badge, yLocal - 2, 16, 11,
                            badgeCol, bold: false, xOff: 0, rightAnchored: true);
                yLocal += 16;

                // Step-count hint in subtle colour
                MakeLabelAt(row.transform, stepsHint, yLocal, stepsHintH, 10,
                            new Color(catColor.r * 0.8f + 0.2f,
                                      catColor.g * 0.8f + 0.2f,
                                      catColor.b * 0.8f + 0.2f, 0.75f),
                            xOff: 8);
                yLocal += stepsHintH + 2;

                // Description
                MakeLabelAt(row.transform, q.description, yLocal, descH, 11,
                            new Color(0.72f, 0.72f, 0.72f), xOff: 8);
                yLocal += descH;

                // Accept button
                if (canAccept)
                {
                    yLocal += 4;
                    var cq = q;
                    MakeButtonAt(row.transform, "ACCEPT QUEST", yLocal, 24,
                        () => questSystem.AcceptQuest(cq));
                    yLocal += 24;
                }

                yOffset += rowH + SPACING;
                questRows.Add(row);
            }

            // Small gap between categories
            yOffset += 5;
        }

        var rt = leftPanel.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(PANEL_W, yOffset + PADDING);
    }

    // ─────────────────────────────────────────
    //  RIGHT PANEL — active quest
    // ─────────────────────────────────────────

    void RefreshRight()
    {
        if (rightPanel == null) return;

        for (int i = rightPanel.transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(rightPanel.transform.GetChild(i).gameObject);
        activeTitleTxt = null; activeStepTxt = null;
        rewardTxt = null; pointsTxt = null; abandonBtnGo = null;

        var aq = questSystem?.GetActiveQuest();
        int y = PADDING;
        int innerW = PANEL_W - PADDING * 2;

        // Header bar
        MakeLabelAt(rightPanel.transform, "ACTIVE QUEST", y, 20, 14,
                    Color.yellow, bold: true);
        y += 20 + 6;

        // Divider line
        var divGo = new GameObject("Divider");
        divGo.transform.SetParent(rightPanel.transform, false);
        divGo.AddComponent<Image>().color = new Color(1f, 0.85f, 0.1f, 0.3f);
        var drt = divGo.GetComponent<RectTransform>();
        drt.anchorMin = new Vector2(0, 1); drt.anchorMax = new Vector2(1, 1);
        drt.pivot = new Vector2(0.5f, 1f);
        drt.offsetMin = new Vector2(PADDING, 0);
        drt.offsetMax = new Vector2(-PADDING, 0);
        drt.anchoredPosition = new Vector2(0, -y - PADDING);
        drt.sizeDelta = new Vector2(drt.sizeDelta.x, 1);
        y += 4;

        if (aq == null)
        {
            int th = MeasureTextHeight("No active quest", 13, innerW);
            MakeLabelAt(rightPanel.transform, "No active quest",
                        y, th, 13, new Color(1f, 0.85f, 0f), bold: true);
            y += th + 4;

            int sh = MeasureTextHeight("Accept a quest from the board.", 12, innerW);
            MakeLabelAt(rightPanel.transform, "Accept a quest from the board.",
                        y, sh, 12, new Color(0.55f, 0.55f, 0.60f));
            y += sh + 4;

            int ph = MeasureTextHeight($"Total Points: {questSystem.totalPoints}", 12, innerW);
            MakeLabelAt(rightPanel.transform, $"Total Points: {questSystem.totalPoints}",
                        y, ph, 12, Color.yellow, bold: true);
            y += ph + PADDING;
        }
        else
        {
            // Category label
            Color catColor = GetCategoryColor(aq.category);
            int ch = 16;
            MakeLabelAt(rightPanel.transform, aq.category.ToUpper(), y, ch, 10,
                        new Color(catColor.r, catColor.g, catColor.b, 0.85f), bold: true);
            y += ch + 2;

            // Quest title
            int th = MeasureTextHeight(aq.title, 14, innerW);
            activeTitleTxt = MakeLabelAt(rightPanel.transform, aq.title,
                                          y, th, 14, new Color(1f, 0.90f, 0.1f), bold: true);
            y += th + 8;

            // Current step
            if (aq.currentStep < aq.steps.Count)
            {
                var step = aq.steps[aq.currentStep];
                string icon = step.type switch
                {
                    QuestStepType.GoTo => "GO TO",
                    QuestStepType.PickUp => "PICK UP",
                    QuestStepType.Deliver => "DELIVER",
                    QuestStepType.Inspect => "INSPECT",
                    QuestStepType.Meet => "FIND",
                    QuestStepType.Escort => "ESCORT",
                    _ => "DO"
                };
                string stepTxt = $"[{aq.currentStep + 1} / {aq.steps.Count}]  {icon}\n{step.description}";
                int sh = MeasureTextHeight(stepTxt, 12, innerW);
                activeStepTxt = MakeLabelAt(rightPanel.transform, stepTxt,
                                             y, sh, 12, new Color(0.85f, 0.85f, 0.85f));
                y += sh + 8;
            }

            // Step progress dots
            {
                var dotsGo = new GameObject("StepDots");
                dotsGo.transform.SetParent(rightPanel.transform, false);
                var dotsRT = dotsGo.AddComponent<RectTransform>();
                dotsRT.anchorMin = new Vector2(0, 1);
                dotsRT.anchorMax = new Vector2(1, 1);
                dotsRT.pivot = new Vector2(0, 1);
                dotsRT.offsetMin = new Vector2(PADDING, 0);
                dotsRT.offsetMax = new Vector2(-PADDING, 0);
                dotsRT.anchoredPosition = new Vector2(0, -y - PADDING);
                dotsRT.sizeDelta = new Vector2(dotsRT.sizeDelta.x, 14);

                for (int s = 0; s < aq.steps.Count; s++)
                {
                    var dot = new GameObject($"Dot_{s}");
                    dot.transform.SetParent(dotsGo.transform, false);
                    var dtxt = dot.AddComponent<Text>();
                    dtxt.font = font;
                    dtxt.text = (s < aq.currentStep || aq.steps[s].done) ? "●" : "○";
                    dtxt.fontSize = 12;
                    dtxt.color = (s < aq.currentStep || aq.steps[s].done)
                        ? new Color(0.3f, 1f, 0.3f)
                        : new Color(0.5f, 0.5f, 0.5f);
                    dtxt.alignment = TextAnchor.MiddleCenter;
                    var drt2 = dot.GetComponent<RectTransform>();
                    drt2.anchorMin = new Vector2(0, 0.5f);
                    drt2.anchorMax = new Vector2(0, 0.5f);
                    drt2.pivot = new Vector2(0, 0.5f);
                    drt2.anchoredPosition = new Vector2(s * 18, 0);
                    drt2.sizeDelta = new Vector2(16, 16);
                }
                y += 14 + 6;
            }

            // Reward
            string rwTxt = $"Reward: {aq.rewardPoints} pts";
            int rh = MeasureTextHeight(rwTxt, 12, innerW);
            rewardTxt = MakeLabelAt(rightPanel.transform, rwTxt,
                                     y, rh, 12, new Color(0.5f, 1f, 0.5f));
            y += rh + 2;

            // Total points
            string ptTxt = $"Total Points: {questSystem.totalPoints}";
            int ph = MeasureTextHeight(ptTxt, 12, innerW);
            pointsTxt = MakeLabelAt(rightPanel.transform, ptTxt,
                                     y, ph, 12, Color.yellow, bold: true);
            y += ph + 8;

            // Abandon button
            MakeButtonAt(rightPanel.transform, "ABANDON QUEST", y, 24,
                () => questSystem?.AbandonQuest());
            y += 24 + PADDING;
        }

        var prt = rightPanel.GetComponent<RectTransform>();
        prt.sizeDelta = new Vector2(PANEL_W, y + PADDING);

        if (minimapPanel != null)
        {
            var mrt = minimapPanel.GetComponent<RectTransform>();
            mrt.anchoredPosition = new Vector2(RIGHT_X, -(y + PADDING + 8));
        }
    }

    // ─────────────────────────────────────────
    //  CATEGORY COLOURS
    // ─────────────────────────────────────────

    static Color GetCategoryColor(string category) => category switch
    {
        "Delivery" => new Color(1.00f, 0.55f, 0.10f),   // amber
        "Help" => new Color(0.20f, 0.85f, 0.85f),   // cyan
        "Investigation" => new Color(0.70f, 0.40f, 1.00f),   // purple
        "Escort" => new Color(0.20f, 0.90f, 0.45f),   // green
        _ => new Color(0.80f, 0.80f, 0.80f),   // neutral grey
    };

    // ─────────────────────────────────────────
    //  NOTIFICATION
    // ─────────────────────────────────────────

    void ShowNotif(string msg, Color col)
    {
        if (notifGo == null || notifTxt == null) return;
        notifTxt.text = msg;
        notifTxt.color = col;
        notifGo.SetActive(true);
        notifTimer = 3f;
    }

    void Update()
    {
        if (notifTimer > 0f)
        {
            notifTimer -= Time.deltaTime;
            if (notifTimer <= 0f && notifGo != null)
                notifGo.SetActive(false);
        }

        UpdateCompass();
        UpdateMinimap();
    }

    void UpdateCompass()
    {
        if (compassGo == null) return;

        var aq = questSystem?.GetActiveQuest();
        if (aq == null || aq.currentStep >= aq.steps.Count)
        {
            compassGo.SetActive(false);
            return;
        }

        if (playerCamera == null) playerCamera = Camera.main;
        if (playerCamera == null) return;

        Vector3 target = aq.steps[aq.currentStep].worldPosition;
        Vector3 playerPos = playerCamera.transform.position;

        float dist = Vector2.Distance(
            new Vector2(playerPos.x, playerPos.z),
            new Vector2(target.x, target.z));

        Vector3 toTarget = new Vector3(target.x - playerPos.x, 0, target.z - playerPos.z).normalized;
        Vector3 camFwd = playerCamera.transform.forward; camFwd.y = 0; camFwd.Normalize();
        Vector3 camRight = playerCamera.transform.right; camRight.y = 0; camRight.Normalize();

        float fwdDot = Vector3.Dot(toTarget, camFwd);
        float rightDot = Vector3.Dot(toTarget, camRight);
        float angle = Mathf.Atan2(rightDot, fwdDot) * Mathf.Rad2Deg;
        compassArrowRT.localRotation = Quaternion.Euler(0, 0, -angle);

        if (compassDistTxt != null)
            compassDistTxt.text = dist < 1000f
                ? $"{dist:F0} m"
                : $"{dist / 1000f:F1} km";

        compassGo.SetActive(true);
    }

    void UpdateMinimap()
    {
        if (minimapPanel == null || !minimapPanel.activeInHierarchy) return;

        if (playerTransform == null)
        {
            var fps = FindFirstObjectByType<FirstPersonController>();
            if (fps != null) playerTransform = fps.transform;
        }
        if (playerTransform == null || minimapCam == null) return;

        Vector3 p = playerTransform.position;
        minimapCam.transform.position = new Vector3(p.x, 1200f, p.z);
        minimapCam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        PlaceDotAtNormalized(playerDot, 0f, 0f);

        if (playerDot != null)
        {
            float yaw = playerTransform.eulerAngles.y;
            playerDot.GetComponent<RectTransform>().localRotation =
                Quaternion.Euler(0f, 0f, -yaw);
        }

        var aq = questSystem?.GetActiveQuest();
        if (aq != null && aq.currentStep < aq.steps.Count && questDot != null)
        {
            Vector3 target = aq.steps[aq.currentStep].worldPosition;
            float nx = Mathf.Clamp((target.x - p.x) / MINIMAP_RANGE, -1f, 1f);
            float nz = Mathf.Clamp((target.z - p.z) / MINIMAP_RANGE, -1f, 1f);
            PlaceDotAtNormalized(questDot, nx, nz);
            questDot.SetActive(true);
        }
        else if (questDot != null)
        {
            questDot.SetActive(false);
        }
    }

    void PlaceDotAtNormalized(GameObject dot, float nx, float nz)
    {
        if (dot == null) return;
        float imgL = 4f;
        float imgR = PANEL_W - 4f;
        float imgB = 4f;
        float imgT = MINIMAP_PX + 28 - 26 - 4f;

        float px = Mathf.Lerp(imgL, imgR, nx * 0.5f + 0.5f);
        float py = Mathf.Lerp(imgB, imgT, nz * 0.5f + 0.5f);

        var rt = dot.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(px, py);
    }

    // ─────────────────────────────────────────
    //  HELPERS — explicit RectTransform sizing
    // ─────────────────────────────────────────

    int MeasureTextHeight(string text, int fontSize, int widthPx)
    {
        if (string.IsNullOrEmpty(text)) return fontSize + 4;
        var gen = new TextGenerator();
        var settings = new TextGenerationSettings
        {
            font = font,
            fontSize = fontSize,
            fontStyle = FontStyle.Normal,
            color = Color.white,
            lineSpacing = 1f,
            richText = false,
            scaleFactor = 1f,
            horizontalOverflow = HorizontalWrapMode.Wrap,
            verticalOverflow = VerticalWrapMode.Overflow,
            generateOutOfBounds = true,
            resizeTextForBestFit = false,
            pivot = new Vector2(0f, 1f),
            textAnchor = TextAnchor.UpperLeft,
            generationExtents = new Vector2(widthPx - PADDING * 2 - 12, 9999f)
        };
        gen.Populate(text, settings);
        return Mathf.Max(fontSize + 4,
                         Mathf.CeilToInt(gen.GetPreferredHeight(text, settings)) + 4);
    }

    GameObject MakeBox(Transform parent, string name,
        Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = new Color(0, 0, 0, 0.87f);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return go;
    }

    Text MakeLabelAt(Transform parent, string text, int yFromTop, int height,
                     int fontSize, Color color, bool bold = false,
                     int xOff = 0, bool rightAnchored = false)
    {
        var go = new GameObject("Lbl");
        go.transform.SetParent(parent, false);

        var txt = go.AddComponent<Text>();
        txt.font = font;
        txt.text = text;
        txt.fontSize = fontSize;
        txt.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        txt.color = color;
        txt.alignment = rightAnchored ? TextAnchor.UpperRight : TextAnchor.UpperLeft;
        txt.horizontalOverflow = HorizontalWrapMode.Wrap;
        txt.verticalOverflow = VerticalWrapMode.Overflow;

        var rt = go.GetComponent<RectTransform>();
        if (rightAnchored)
        {
            rt.anchorMin = new Vector2(1, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(1, 1);
            rt.anchoredPosition = new Vector2(xOff - PADDING, -yFromTop - PADDING);
            rt.sizeDelta = new Vector2(100, height);
        }
        else
        {
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0, 1);
            rt.offsetMin = new Vector2(xOff + PADDING, 0);
            rt.offsetMax = new Vector2(-PADDING, 0);
            rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, -yFromTop - PADDING);
            rt.sizeDelta = new Vector2(rt.sizeDelta.x, height);
        }
        return txt;
    }

    void MakeButtonAt(Transform parent, string label, int yFromTop, int height,
                      UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Btn_" + label);
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = new Color(0.20f, 0.20f, 0.25f, 1f);

        var btn = go.AddComponent<Button>();
        var colors = btn.colors;
        colors.normalColor = Color.white;
        colors.highlightedColor = new Color(0.7f, 0.85f, 1.0f);
        colors.pressedColor = new Color(0.5f, 0.5f, 0.5f);
        btn.colors = colors;
        btn.onClick.AddListener(onClick);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(PADDING, 0);
        rt.offsetMax = new Vector2(-PADDING, 0);
        rt.anchoredPosition = new Vector2(0, -yFromTop - PADDING);
        rt.sizeDelta = new Vector2(rt.sizeDelta.x, height);

        var lgo = new GameObject("BtnLabel");
        lgo.transform.SetParent(go.transform, false);
        var txt = lgo.AddComponent<Text>();
        txt.font = font;
        txt.text = label;
        txt.fontSize = 12;
        txt.fontStyle = FontStyle.Bold;
        txt.color = Color.white;
        txt.alignment = TextAnchor.MiddleCenter;
        var lrt = lgo.GetComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
        lrt.offsetMin = lrt.offsetMax = Vector2.zero;
    }

    Text MakeLabel(Transform parent, string text, int yFromTop, int fontSize,
        Color color, bool bold = false, bool centered = false, int xOffset = 0)
    {
        var go = new GameObject("Lbl_" + text.Substring(0, Mathf.Min(8, text.Length)));
        go.transform.SetParent(parent, false);

        var txt = go.AddComponent<Text>();
        txt.font = font;
        txt.text = text;
        txt.fontSize = fontSize;
        txt.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        txt.color = color;
        txt.alignment = centered ? TextAnchor.MiddleCenter : TextAnchor.UpperLeft;
        txt.horizontalOverflow = HorizontalWrapMode.Wrap;
        txt.verticalOverflow = VerticalWrapMode.Overflow;

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(0, 1);
        rt.offsetMin = new Vector2(xOffset + PADDING, 0);
        rt.offsetMax = new Vector2(-PADDING, 0);
        rt.anchoredPosition = new Vector2(rt.anchoredPosition.x, -yFromTop - PADDING);
        rt.sizeDelta = new Vector2(rt.sizeDelta.x, fontSize + 6);
        return txt;
    }

    GameObject MakeButton(Transform parent, string label, int yFromTop,
        UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Btn_" + label);
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = new Color(0.20f, 0.20f, 0.25f, 1f);

        var btn = go.AddComponent<Button>();
        var colors = btn.colors;
        colors.highlightedColor = new Color(0.4f, 0.4f, 0.4f);
        colors.pressedColor = new Color(0.15f, 0.15f, 0.15f);
        btn.colors = colors;
        btn.onClick.AddListener(onClick);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(PADDING, 0);
        rt.offsetMax = new Vector2(-PADDING, 0);
        rt.anchoredPosition = new Vector2(0, -yFromTop - PADDING);
        rt.sizeDelta = new Vector2(rt.sizeDelta.x, 24);

        var lgo = new GameObject("BtnLabel");
        lgo.transform.SetParent(go.transform, false);
        var txt = lgo.AddComponent<Text>();
        txt.font = font;
        txt.text = label;
        txt.fontSize = 13;
        txt.fontStyle = FontStyle.Bold;
        txt.color = Color.white;
        txt.alignment = TextAnchor.MiddleCenter;

        var lrt = lgo.GetComponent<RectTransform>();
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
        lrt.offsetMin = lrt.offsetMax = Vector2.zero;
        return go;
    }

    Font GetFont() => font;
}