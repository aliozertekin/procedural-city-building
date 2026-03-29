using System.Collections.Generic;
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

    // Left panel (quest list)
    private GameObject leftPanel;
    private readonly List<GameObject> questRows = new List<GameObject>();

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
    // Reference to camera for direction calculation
    public Camera playerCamera;

    // ── Minimap ──
    public Transform playerTransform;   // assign in Inspector or auto-found
    private GameObject minimapPanel;
    private RawImage minimapImage;
    private GameObject playerDot;
    private GameObject questDot;
    private RenderTexture minimapRT;
    private Camera minimapCam;

    // How many world units the minimap covers (half-extent)
    const float MINIMAP_RANGE = 120f;
    const int MINIMAP_SIZE = 512;    // render texture resolution
    const int MINIMAP_PX = 260;    // panel pixel size

    // Layout constants
    const int LEFT_X = 10;
    const int RIGHT_X = -10;   // from right edge
    const int PANEL_W = 300;
    const int TOP_Y = -10;   // from top edge
    const int ROW_H = 22;
    const int PADDING = 10;
    const int SPACING = 4;

    // ─────────────────────────────────────────
    void Awake()
    {
        font = uiFont ? uiFont : Resources.GetBuiltinResource<Font>("Arial.ttf");

        canvas = GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 20;

        // ConstantPixelSize: panels are always exactly the pixel sizes we set.
        // ScaleWithScreenSize was silently shrinking everything on non-1080p screens.
        var scaler = GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
        scaler.scaleFactor = 1f;

        if (questSystem == null)
            questSystem = FindFirstObjectByType<QuestSystem>();

        Build();
        Subscribe();
        // Leave active so Start() runs and layout initialises correctly.
        // CameraModeSwitcher.Start() will call SetActive(false) right after.
    }

    void Start()
    {
        Refresh();
    }

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
        questSystem.OnQuestAccepted -= q => ShowNotif($"Started: {q.title}", new Color(0.3f, 1f, 0.3f));
        questSystem.OnQuestCompleted -= q => ShowNotif($"Complete! +{q.rewardPoints} pts", Color.yellow);
    }

    void Subscribe()
    {
        if (questSystem == null) return;
        questSystem.OnQuestsChanged += Refresh;
        questSystem.OnQuestAccepted += q => ShowNotif($"Quest started: {q.title}", new Color(0.3f, 1f, 0.3f));
        questSystem.OnStepCompleted += (q, i) => ShowNotif("Step complete! Keep going...", Color.cyan);
        questSystem.OnQuestCompleted += q => ShowNotif($"Quest complete! +{q.rewardPoints} pts", Color.yellow);
    }

    // ─────────────────────────────────────────
    //  BUILD  (called once)
    // ─────────────────────────────────────────

    void Build()
    {
        // destroy old children
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
        questRows.Clear();

        // ── LEFT PANEL ──
        leftPanel = MakeBox(transform, "LeftPanel",
            anchor: new Vector2(0, 1), pivot: new Vector2(0, 1),
            pos: new Vector2(LEFT_X, TOP_Y),
            size: new Vector2(PANEL_W, 400));

        MakeLabel(leftPanel.transform, "QUESTS", 0, 16, Color.yellow, bold: true);
        // quest rows filled in Refresh()

        // ── RIGHT PANEL ──
        rightPanel = MakeBox(transform, "RightPanel",
            anchor: new Vector2(1, 1), pivot: new Vector2(1, 1),
            pos: new Vector2(RIGHT_X, TOP_Y),
            size: new Vector2(PANEL_W, 220));

        MakeLabel(rightPanel.transform, "ACTIVE QUEST", 0, 16, Color.yellow, bold: true);

        activeTitleTxt = MakeLabel(rightPanel.transform, "—", 28, 14, new Color(1f, 0.85f, 0f), bold: true);
        activeStepTxt = MakeLabel(rightPanel.transform, "No quest", 52, 13, Color.white);
        rewardTxt = MakeLabel(rightPanel.transform, "", 80, 12, new Color(0.6f, 1f, 0.6f));
        pointsTxt = MakeLabel(rightPanel.transform, "Points: 0", 100, 13, Color.yellow, bold: true);

        abandonBtnGo = MakeButton(rightPanel.transform, "ABANDON", 126, () =>
        {
            questSystem?.AbandonQuest();
        });
        abandonBtnGo.SetActive(false);

        // ── NOTIFICATION BANNER ──
        notifGo = MakeBox(transform, "Notif",
            anchor: new Vector2(0.5f, 1f), pivot: new Vector2(0.5f, 1f),
            pos: new Vector2(0, -10),
            size: new Vector2(700, 50));
        notifGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.8f);

        notifTxt = MakeLabel(notifGo.transform, "", 0, 18, Color.white, bold: true, centered: true);
        var nrt = notifTxt.rectTransform;
        nrt.anchorMin = Vector2.zero; nrt.anchorMax = Vector2.one;
        nrt.offsetMin = nrt.offsetMax = Vector2.zero;

        notifGo.SetActive(false);

        // ── COMPASS ARROW ──
        // A small panel at the bottom centre with a rotating arrow pointing
        // toward the current quest objective, plus distance text.
        compassGo = MakeBox(transform, "Compass",
            anchor: new Vector2(0.5f, 0f), pivot: new Vector2(0.5f, 0f),
            pos: new Vector2(0, 20),
            size: new Vector2(120, 80));
        compassGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.75f);

        // Arrow: a simple ▲ character rotated each frame
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

        // Distance label below the arrow
        compassDistTxt = MakeLabel(compassGo.transform, "", 48, 12, Color.white, centered: true);
        var crt = compassDistTxt.rectTransform;
        crt.anchorMin = new Vector2(0, 0); crt.anchorMax = new Vector2(1, 0);
        crt.pivot = new Vector2(0.5f, 0);
        crt.anchoredPosition = new Vector2(0, 6);
        crt.sizeDelta = new Vector2(0, 18);

        compassGo.SetActive(false);

        // ── MINIMAP PANEL ──
        // Sits directly below the right panel (right panel is 220px tall, TOP_Y=-10)
        // so minimap top = TOP_Y - rightPanelHeight - gap
        minimapPanel = MakeBox(transform, "MinimapPanel",
            anchor: new Vector2(1, 1), pivot: new Vector2(1, 1),
            pos: new Vector2(RIGHT_X, -238),
            size: new Vector2(PANEL_W, MINIMAP_PX + 28));   // +28 for header
        minimapPanel.GetComponent<Image>().color = new Color(0, 0, 0, 0.85f);

        // Header label
        MakeLabel(minimapPanel.transform, "MINIMAP", 0, 14, Color.yellow, bold: true);

        // RawImage — fills panel below the header
        var rimGo = new GameObject("MinimapRT");
        rimGo.transform.SetParent(minimapPanel.transform, false);
        minimapImage = rimGo.AddComponent<RawImage>();
        minimapImage.color = Color.white;

        var rimRT = rimGo.GetComponent<RectTransform>();
        rimRT.anchorMin = new Vector2(0, 0);
        rimRT.anchorMax = new Vector2(1, 1);
        rimRT.pivot = new Vector2(0.5f, 0.5f);
        rimRT.offsetMin = new Vector2(4, 4);
        rimRT.offsetMax = new Vector2(-4, -26);   // leave 26px for header

        // Player dot — white circle using a label character
        playerDot = MakeMinimapDot(minimapPanel.transform, "P", Color.white, 14);
        questDot = MakeMinimapDot(minimapPanel.transform, "★", Color.yellow, 16);

        // Create the RenderTexture + orthographic top-down camera
        BuildMinimapCamera();
    }

    void BuildMinimapCamera()
    {
        minimapRT = new RenderTexture(MINIMAP_SIZE, MINIMAP_SIZE, 16);
        minimapRT.name = "MinimapRT";
        if (minimapImage != null) minimapImage.texture = minimapRT;

        var camGo = new GameObject("MinimapCamera");
        // Don't parent to UI — it lives in the world
        minimapCam = camGo.AddComponent<Camera>();
        minimapCam.orthographic = true;
        minimapCam.orthographicSize = MINIMAP_RANGE;
        minimapCam.nearClipPlane = 1f;
        minimapCam.farClipPlane = 5000f;
        minimapCam.targetTexture = minimapRT;
        minimapCam.clearFlags = CameraClearFlags.SolidColor;
        minimapCam.backgroundColor = new Color(0.13f, 0.22f, 0.13f);  // dark green

        // Only render Default + TransparentFX layers; exclude UI
        minimapCam.cullingMask = ~(1 << LayerMask.NameToLayer("UI"));

        // Face straight down
        camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        // Boost LOD and texture quality specifically when this camera renders.
        // The minimap camera is always active so we can't rely on TopDownCamera's
        // OnEnable/OnDisable — we need per-render hooks instead.
        camGo.AddComponent<MinimapQualityBoost>();
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
    //  REFRESH  (called whenever data changes)
    // ─────────────────────────────────────────

    public void Refresh()
    {
        if (questSystem == null)
            questSystem = FindFirstObjectByType<QuestSystem>();
        if (questSystem == null) return;
        if (leftPanel == null) return;   // not built yet

        RefreshLeft();
        RefreshRight();
    }

    void RefreshLeft()
    {
        // destroy old quest rows (keep first child = header label)
        foreach (var r in questRows) if (r) Destroy(r);
        questRows.Clear();

        var quests = questSystem.GetAllQuests();
        int yOffset = 28;   // start below the "QUESTS" header

        foreach (var q in quests)
        {
            // row height depends on content
            int rowH = 64;
            bool canAccept = q.status == QuestStatus.Available && questSystem.GetActiveQuest() == null;
            if (canAccept) rowH += 26;

            var row = MakeBox(leftPanel.transform, $"Row_{q.id}",
                anchor: new Vector2(0, 1), pivot: new Vector2(0, 1),
                pos: new Vector2(PADDING, -yOffset),
                size: new Vector2(PANEL_W - PADDING * 2, rowH));

            // row background color by status
            row.GetComponent<Image>().color = q.status switch
            {
                QuestStatus.Active => new Color(0.1f, 0.25f, 0.45f, 0.95f),
                QuestStatus.Completed => new Color(0.1f, 0.3f, 0.1f, 0.95f),
                _ => new Color(0.18f, 0.18f, 0.18f, 0.95f)
            };

            // title + badge on same line
            string badge = q.status switch
            {
                QuestStatus.Active => "[ACTIVE]",
                QuestStatus.Completed => "[DONE]",
                _ => $"+{q.rewardPoints}pts"
            };
            Color badgeCol = q.status switch
            {
                QuestStatus.Active => new Color(0.4f, 0.8f, 1f),
                QuestStatus.Completed => new Color(0.4f, 1f, 0.4f),
                _ => Color.yellow
            };

            MakeLabel(row.transform, q.title, 4, 13, Color.white, bold: true, xOffset: 6);
            MakeLabel(row.transform, badge, 4, 11, badgeCol, xOffset: PANEL_W - PADDING * 2 - 80);
            MakeLabel(row.transform, q.description, 22, 11, new Color(0.75f, 0.75f, 0.75f), xOffset: 6);

            if (canAccept)
            {
                var cq = q;
                MakeButton(row.transform, "ACCEPT", 42, () =>
                {
                    questSystem.AcceptQuest(cq);
                });
            }

            yOffset += rowH + SPACING;
            questRows.Add(row);
        }

        // Resize left panel to fit all rows
        var rt = leftPanel.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(PANEL_W, yOffset + PADDING);
    }

    void RefreshRight()
    {
        if (activeTitleTxt == null) return;

        var aq = questSystem.GetActiveQuest();

        if (aq == null)
        {
            activeTitleTxt.text = "No active quest";
            activeStepTxt.text = "Accept a quest →";
            rewardTxt.text = "";
            abandonBtnGo?.SetActive(false);
        }
        else
        {
            activeTitleTxt.text = aq.title;
            rewardTxt.text = $"Reward: {aq.rewardPoints} pts";
            abandonBtnGo?.SetActive(true);

            if (aq.currentStep < aq.steps.Count)
            {
                var step = aq.steps[aq.currentStep];
                string icon = step.type switch
                {
                    QuestStepType.GoTo => ">> GO TO",
                    QuestStepType.PickUp => ">> PICK UP",
                    QuestStepType.Deliver => ">> DELIVER",
                    _ => ">>"
                };
                activeStepTxt.text =
                    $"Step {aq.currentStep + 1} / {aq.steps.Count}\n{icon}: {step.description}";
            }
        }

        if (pointsTxt != null)
            pointsTxt.text = $"Points: {questSystem.totalPoints}";
    }

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

        // Auto-find player camera if not assigned
        if (playerCamera == null)
            playerCamera = Camera.main;
        if (playerCamera == null) return;

        Vector3 target = aq.steps[aq.currentStep].worldPosition;
        Vector3 playerPos = playerCamera.transform.position;

        // Flat distance (ignore Y)
        float dist = Vector2.Distance(
            new Vector2(playerPos.x, playerPos.z),
            new Vector2(target.x, target.z));

        // Direction to target in world XZ, then project onto camera's forward/right
        Vector3 toTarget = new Vector3(target.x - playerPos.x, 0, target.z - playerPos.z).normalized;
        Vector3 camFwd = playerCamera.transform.forward; camFwd.y = 0; camFwd.Normalize();
        Vector3 camRight = playerCamera.transform.right; camRight.y = 0; camRight.Normalize();

        float fwdDot = Vector3.Dot(toTarget, camFwd);
        float rightDot = Vector3.Dot(toTarget, camRight);

        // Angle: 0° = straight ahead, rotates clockwise
        float angle = Mathf.Atan2(rightDot, fwdDot) * Mathf.Rad2Deg;
        compassArrowRT.localRotation = Quaternion.Euler(0, 0, -angle);

        // Distance label
        if (compassDistTxt != null)
            compassDistTxt.text = dist < 1000f
                ? $"{dist:F0}m"
                : $"{dist / 1000f:F1}km";

        compassGo.SetActive(true);
    }

    void UpdateMinimap()
    {
        if (minimapPanel == null || !minimapPanel.activeInHierarchy) return;

        // Auto-find player transform if not assigned
        if (playerTransform == null)
        {
            var fps = FindFirstObjectByType<FirstPersonController>();
            if (fps != null) playerTransform = fps.transform;
        }
        if (playerTransform == null || minimapCam == null) return;

        // Move minimap camera above the player, looking straight down
        Vector3 p = playerTransform.position;
        minimapCam.transform.position = new Vector3(p.x, p.y + 300f, p.z);

        // ── Position the player dot at the centre of the minimap image ──
        // Player is always centred since the camera follows them
        PositionDotOnMinimap(playerDot, Vector3.zero);   // zero offset = centre

        // ── Rotate player dot to show facing direction ──
        if (playerDot != null)
        {
            float yaw = playerTransform.eulerAngles.y;
            playerDot.GetComponent<RectTransform>().localRotation =
                Quaternion.Euler(0, 0, -yaw);
        }

        // ── Quest dot ──
        var aq = questSystem?.GetActiveQuest();
        if (aq != null && aq.currentStep < aq.steps.Count && questDot != null)
        {
            Vector3 target = aq.steps[aq.currentStep].worldPosition;
            Vector3 offset = target - p;   // world offset from player
            PositionDotOnMinimap(questDot, offset);
            questDot.SetActive(true);
        }
        else if (questDot != null)
        {
            questDot.SetActive(false);
        }
    }

    // Converts a world-space offset from the player into a pixel position
    // on the minimap RawImage RectTransform.
    void PositionDotOnMinimap(GameObject dot, Vector3 worldOffset)
    {
        if (dot == null || minimapImage == null) return;

        // Get the pixel rect of the minimap image inside the panel
        var imgRT = minimapImage.GetComponent<RectTransform>();

        // Normalised position: clamp to [-1, 1] in each axis
        float nx = Mathf.Clamp(worldOffset.x / MINIMAP_RANGE, -1f, 1f);
        float nz = Mathf.Clamp(worldOffset.z / MINIMAP_RANGE, -1f, 1f);

        // Convert to local position within the panel
        // imgRT rect: offsetMin=(4,4) offsetMax=(-4,-26)
        // We need to map [-1,1] → [imgRT.rect.min, imgRT.rect.max]
        Rect r = imgRT.rect;
        float px = r.x + (nx * 0.5f + 0.5f) * r.width;
        float py = r.y + (nz * 0.5f + 0.5f) * r.height;

        var rt = dot.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = new Vector2(0, 0);
        rt.anchoredPosition = new Vector2(px, py);
    }

    // ─────────────────────────────────────────
    //  HELPERS  — explicit RectTransform sizing
    // ─────────────────────────────────────────

    /// Creates a panel with a background Image and explicit pixel size.
    GameObject MakeBox(Transform parent, string name,
        Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = new Color(0, 0, 0, 0.85f);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
        return go;
    }

    /// Creates a Text label with an explicit Y offset from top of parent.
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

    /// Creates a clickable button with explicit Y offset from top of parent.
    GameObject MakeButton(Transform parent, string label, int yFromTop,
        UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("Btn_" + label);
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = new Color(0.25f, 0.25f, 0.25f);

        var btn = go.AddComponent<Button>();
        var colors = btn.colors;
        colors.highlightedColor = new Color(0.4f, 0.4f, 0.4f);
        colors.pressedColor = new Color(0.15f, 0.15f, 0.15f);
        btn.colors = colors;
        btn.onClick.AddListener(onClick);

        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = new Vector2(0, 1);
        rt.anchorMax = new Vector2(1, 1);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(PADDING, 0);
        rt.offsetMax = new Vector2(-PADDING, 0);
        rt.anchoredPosition = new Vector2(0, -yFromTop - PADDING);
        rt.sizeDelta = new Vector2(rt.sizeDelta.x, 24);

        // Label inside button
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
        lrt.anchorMin = Vector2.zero;
        lrt.anchorMax = Vector2.one;
        lrt.offsetMin = lrt.offsetMax = Vector2.zero;

        return go;
    }

    Font GetFont() => font;
}

// ─────────────────────────────────────────────────────────────────────────────
//  MINIMAP QUALITY BOOST
//  Attached to the minimap camera. Uses OnPreRender / OnPostRender to
//  temporarily override global quality settings so the minimap renders
//  at full LOD and full texture quality, then restores the originals.
//  This works independently of which player-camera is currently active.
// ─────────────────────────────────────────────────────────────────────────────
public class MinimapQualityBoost : MonoBehaviour
{
    [Tooltip("LOD bias while the minimap renders. " +
             "Higher = objects keep their high-detail LOD at greater distances.")]
    public float lodBias = 4f;

    private float savedLodBias;
    private float savedShadowDist;
    private int savedTextureLimit;
    private AnisotropicFiltering savedAniso;

    void OnPreRender()
    {
        savedLodBias = QualitySettings.lodBias;
        savedShadowDist = QualitySettings.shadowDistance;
        savedTextureLimit = QualitySettings.globalTextureMipmapLimit;
        savedAniso = QualitySettings.anisotropicFiltering;

        QualitySettings.lodBias = lodBias;
        QualitySettings.shadowDistance = 0f;      // no shadows needed on minimap
        QualitySettings.globalTextureMipmapLimit = 0;       // full-res textures
        QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
    }

    void OnPostRender()
    {
        QualitySettings.lodBias = savedLodBias;
        QualitySettings.shadowDistance = savedShadowDist;
        QualitySettings.globalTextureMipmapLimit = savedTextureLimit;
        QualitySettings.anisotropicFiltering = savedAniso;
    }
}