using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// ─────────────────────────────────────────────
//  DATA
// ─────────────────────────────────────────────

public enum QuestStepType { GoTo, PickUp, Deliver, Inspect, Meet, Escort }
public enum QuestStatus { Available, Active, Completed, Failed }

[Serializable]
public class QuestStep
{
    public QuestStepType type;
    public string description;
    public Vector3 worldPosition;
    public float radius = 6f;
    [HideInInspector] public bool done;
    [NonSerialized] public Transform npcTarget;
}

[Serializable]
public class Quest
{
    public string id;
    public string title;
    public string description;
    public int rewardPoints = 100;
    public string category;
    public List<QuestStep> steps = new List<QuestStep>();
    [HideInInspector] public QuestStatus status = QuestStatus.Available;
    [HideInInspector] public int currentStep = 0;
    [NonSerialized] public int[] npcStepIndices;
}

// ─────────────────────────────────────────────
//  QUEST TEMPLATE
// ─────────────────────────────────────────────

struct QuestTemplate
{
    public string category;
    public string titleFmt;
    public string descFmt;
    public int baseReward;
    public QuestStepType[] stepTypes;
    public string[] stepDescFmts;
    public float minDist;
    public float maxDist;
    public string sub0Source;
    public string sub1Source;
    public int[] npcStepIndices;
}

// ─────────────────────────────────────────────
//  MANAGER  (singleton)
// ─────────────────────────────────────────────

public class QuestSystem : MonoBehaviour
{
    public static QuestSystem Instance { get; private set; }

    [Header("References")]
    public Transform playerTransform;
    public CityGenerator cityGenerator;
    [Tooltip("Drag your NPCSpawner here so quest steps can track live NPCs")]
    public NPCSpawner npcSpawner;
    [Tooltip("Drag your CarControl here so arrows and minimap work while driving")]
    public CarControl carControl;

    [Header("Settings")]
    public float checkInterval = 0.3f;
    [Tooltip("How many quests appear on the board at once")]
    public int questBoardSize = 8;
    [Tooltip("Max world-unit radius from road nodes to pick quest locations.")]
    public float questSpawnRadius = 60f;
    [Tooltip("Seconds after all quests complete before auto-regenerating a new board.")]
    public float autoRestartDelay = 6f;

    [Header("Path Arrows (in-world while driving)")]
    [Tooltip("Show direction arrows on the road guiding player to the active quest step.")]
    public bool showPathArrows = true;
    [Tooltip("Reference to your CityPathfinder if you want road-following arrows. Leave null for straight-line arrows.")]
    public CityPathfinder pathfinder;
    [Tooltip("World-space gap between consecutive arrow markers.")]
    public float arrowSpacing = 14f;
    [Tooltip("How far ahead of the player to start placing arrows.")]
    public float arrowStartOffset = 6f;
    [Tooltip("Half-width of each arrowhead quad in world units.")]
    public float arrowHalfWidth = 1.6f;
    [Tooltip("Tip length of each arrowhead quad in world units.")]
    public float arrowLength = 3.0f;
    [Tooltip("Height above road surface to float the arrows.")]
    public float arrowHeight = 0.35f;
    [Tooltip("Primary arrow colour.")]
    public Color arrowColor = new Color(1f, 0.85f, 0.1f, 0.92f);
    [Tooltip("Arrow outline / shadow colour.")]
    public Color arrowOutlineColor = new Color(0f, 0f, 0f, 0.55f);
    [Tooltip("Pulse speed of the arrow opacity.")]
    public float arrowPulseSpeed = 1.4f;
    [Tooltip("Max arrows rendered at once (limits object count).")]
    public int maxArrows = 18;

    // ── Time-based scoring ───────────────────────────────
    [Header("Time Scoring")]
    [Tooltip("Under this many seconds earns the maximum time bonus.")]
    public float bonusTimeThreshold = 60f;
    [Tooltip("Over this many seconds earns the maximum time penalty.")]
    public float penaltyTimeThreshold = 300f;
    [Tooltip("Score multiplier at the fast extreme (e.g. 1.5 = +50%).")]
    public float maxBonusMultiplier = 1.5f;
    [Tooltip("Score multiplier at the slow extreme (e.g. 0.5 = -50%).")]
    public float minPenaltyMultiplier = 0.5f;

    // ── Events the UI listens to ─────────────────────────
    public event Action<Quest> OnQuestAccepted;
    public event Action<Quest, int> OnStepCompleted;
    /// <summary>Fires when a quest completes. Second arg is the final (time-adjusted) points awarded.</summary>
    public event Action<Quest, int> OnQuestCompleted;
    public event Action OnQuestsChanged;
    /// <summary>Fires when every quest on the current board has been completed.</summary>
    public event Action OnAllQuestsCompleted;

    public int totalPoints { get; private set; }

    // ── Timer ────────────────────────────────────────────
    private float questStartTime;

    /// <summary>Elapsed seconds for the current active quest (0 when none).</summary>
    public float QuestElapsedTime => activeQuest != null ? Time.time - questStartTime : 0f;

    /// <summary>Elapsed time of the most recently completed quest.</summary>
    public float LastQuestElapsed { get; private set; }

    /// <summary>The time-based score multiplier applied to the most recently completed quest.</summary>
    public float LastQuestMultiplier { get; private set; }

    /// <summary>The bonus/penalty points (positive or negative) applied to the last completed quest.</summary>
    public int LastQuestTimeBonus { get; private set; }

    // ── Internal state ────────────────────────────────────
    private List<Quest> allQuests = new List<Quest>();
    private Quest activeQuest;
    private bool allQuestsDoneRestarting = false;

    private GameObject markerParent;
    private GameObject currentMarker;

    // ── Path-arrow runtime state ──────────────────────────
    private readonly List<GameObject> arrowObjects = new List<GameObject>();
    private Material arrowMatBase;
    private float arrowPulseTimer;

    // ── Navigation transform (player on foot OR car while driving) ──
    /// <summary>
    /// Returns the car transform while driving, otherwise the player transform.
    /// Lazily re-finds CarControl if the reference was lost or never assigned,
    /// so arrows keep updating even if the inspector field was left empty.
    /// </summary>
    private Transform NavigationTransform
    {
        get
        {
            if (carControl == null) carControl = FindFirstObjectByType<CarControl>();
            if (carControl != null && carControl.IsDriving) return carControl.transform;
            return playerTransform;
        }
    }

    // ─── content tables ──────────────────────────────────
    static readonly string[] ItemNames =
    {
        "Medical Supplies",     "Legal Documents",    "A Hot Meal",
        "Birthday Cake",        "Engine Components",  "A Sealed Parcel",
        "Fresh Groceries",      "City Hall Notice",   "Building Permit",
        "A Cash Deposit",       "Old Photographs",    "Concert Tickets",
        "A Spare Set of Keys",  "Lab Samples",        "Repair Tools",
        "Sacred Relics",        "Confidential Files", "Prescription Meds",
        "Emergency Equipment",  "A Locked Briefcase", "Auction Papers",
        "Survey Results",       "A Wedding Ring",     "Replacement Parts"
    };

    static readonly string[] PersonNames =
    {
        "Mayor Chen",       "Dr. Vasquez",     "Officer Reeves",
        "Nurse Hailey",     "Chef Moretti",    "Engineer Polk",
        "Artist Okafor",    "Banker Leigh",    "Teacher Sato",
        "Mechanic Ruiz",    "Lawyer Pratt",    "Vendor Aydin",
        "Detective Walsh",  "Librarian Cross", "Alderman Fitch",
        "Courier Yuen",     "Foreman Ibarra",  "Medic Torres",
        "Archivist Bloom",  "Groundskeeper Nix"
    };

    static readonly string[] LocationNames =
    {
        "the Town Hall",         "the Hospital",       "the Police Station",
        "the Fire Station",      "the Post Office",    "the Market Square",
        "the Train Station",     "the Park Gate",      "the Old Warehouse",
        "the Community Centre",  "the Library",        "the Garage",
        "the Construction Site", "the Docks",          "the Clock Tower",
        "the Underground Station"
    };

    static readonly string[] CrimeDescriptions =
    {
        "a suspicious vehicle",        "fresh graffiti on a wall",
        "a broken street light",       "an abandoned bag",
        "unusual late-night activity", "a blocked storm drain",
        "a cracked gas main",          "a missing manhole cover",
        "signs of forced entry",       "a discarded firearm"
    };

    static readonly string[] MeetReasons =
    {
        "to hand off the package",
        "for a quick exchange",
        "before they leave town",
        "to collect a signature",
        "to pass on urgent news"
    };

    // ─── templates ────────────────────────────────────────
    static readonly QuestTemplate[] Templates =
    {
        new QuestTemplate { category="Delivery", titleFmt="Package for {1}",
            descFmt="{1} has been expecting {0} and they're getting impatient. Pick it up from the collection point and get it delivered before they give up waiting.",
            baseReward=80, stepTypes=new[]{QuestStepType.PickUp,QuestStepType.Meet},
            stepDescFmts=new[]{"Collect {0} from the marked pickup point","Deliver {0} to {1} — they're somewhere in the area"},
            minDist=45f,maxDist=9999f,sub0Source="item",sub1Source="person",npcStepIndices=new[]{1}},

        new QuestTemplate { category="Delivery", titleFmt="URGENT: {0} for {1}",
            descFmt="{1} needs {0} immediately and there are no alternatives. Pick it up and move fast. This is not a drill.",
            baseReward=160, stepTypes=new[]{QuestStepType.PickUp,QuestStepType.Meet},
            stepDescFmts=new[]{"Grab {0} from the pickup point — don't waste a second","Get {0} to {1} right now. They're waiting and the clock is ticking"},
            minDist=45f,maxDist=9999f,sub0Source="item",sub1Source="person",npcStepIndices=new[]{1}},

        new QuestTemplate { category="Delivery", titleFmt="City-Wide Courier Run",
            descFmt="Three stops, one trip. Collect {0}, make a scheduled drop at {2}, then get the remainder to {1}.",
            baseReward=220, stepTypes=new[]{QuestStepType.PickUp,QuestStepType.Deliver,QuestStepType.Meet},
            stepDescFmts=new[]{"Pick up {0} from the first location","Make a partial drop-off at {2}. Leave what's expected","Finish the run — deliver the remaining {0} to {1}"},
            minDist=55f,maxDist=9999f,sub0Source="item",sub1Source="person",npcStepIndices=new[]{2}},

        new QuestTemplate { category="Help", titleFmt="{0} Sent Word",
            descFmt="{0} has been asking around for a reliable pair of hands. Track them down and hear what they need.",
            baseReward=50, stepTypes=new[]{QuestStepType.Meet},
            stepDescFmts=new[]{"Find {0} and hear what they have to say"},
            minDist=20f,maxDist=9999f,sub0Source="person",sub1Source="reason",npcStepIndices=new[]{0}},

        new QuestTemplate { category="Help", titleFmt="A Favour for {1}",
            descFmt="{1} is tied up and can't leave their post. They need someone to grab {0} and bring it back.",
            baseReward=65, stepTypes=new[]{QuestStepType.PickUp,QuestStepType.Meet},
            stepDescFmts=new[]{"Pick up {0} from the marked location","Return {0} to {1} — they're counting on you"},
            minDist=30f,maxDist=9999f,sub0Source="item",sub1Source="person",npcStepIndices=new[]{1}},

        new QuestTemplate { category="Help", titleFmt="Lost: {0}",
            descFmt="{1} has lost {0} somewhere in the city and they're desperate to get it back.",
            baseReward=75, stepTypes=new[]{QuestStepType.GoTo,QuestStepType.Meet},
            stepDescFmts=new[]{"Check the area where {0} was last seen","Return {0} to {1} — they'll be relieved to see it"},
            minDist=35f,maxDist=9999f,sub0Source="item",sub1Source="person",npcStepIndices=new[]{1}},

        new QuestTemplate { category="Investigation", titleFmt="Look Into: {0}",
            descFmt="Several residents have reported {0} in the area. {1} wants eyes on it before it becomes a bigger problem.",
            baseReward=80, stepTypes=new[]{QuestStepType.Inspect,QuestStepType.Meet},
            stepDescFmts=new[]{"Investigate the scene — residents reported {0} right around here","Report your findings to {1}. They're waiting for your assessment"},
            minDist=35f,maxDist=9999f,sub0Source="crime",sub1Source="person",npcStepIndices=new[]{1}},

        new QuestTemplate { category="Investigation", titleFmt="Sweep the Area: {0}",
            descFmt="Two separate sightings of {0} have been reported on opposite ends of the block. Check both and relay everything to {1}.",
            baseReward=110, stepTypes=new[]{QuestStepType.Inspect,QuestStepType.Inspect,QuestStepType.Meet},
            stepDescFmts=new[]{"Check the first area — scan for any trace of {0}","Move on to the second location and repeat the check","Report everything to {1} — don't leave anything out"},
            minDist=40f,maxDist=9999f,sub0Source="crime",sub1Source="person",npcStepIndices=new[]{2}},

        new QuestTemplate { category="Investigation", titleFmt="Someone Needs to Check This Out",
            descFmt="{1} received an anonymous tip about {0} near two spots in the city. They can't go themselves.",
            baseReward=95, stepTypes=new[]{QuestStepType.GoTo,QuestStepType.Inspect,QuestStepType.Meet},
            stepDescFmts=new[]{"Head to the area flagged in the report","Inspect the exact spot — look closely for {0}","Relay what you found to {1}. They're expecting your call"},
            minDist=40f,maxDist=9999f,sub0Source="crime",sub1Source="person",npcStepIndices=new[]{2}},

        new QuestTemplate { category="Escort", titleFmt="Safe Passage: {0}",
            descFmt="{0} needs to reach {1} and they're not comfortable going alone. Find them and walk them there personally.",
            baseReward=130, stepTypes=new[]{QuestStepType.Meet,QuestStepType.Escort},
            stepDescFmts=new[]{"Find {0} — they're somewhere nearby and they're anxious","Walk {0} safely all the way to {1}. Stay close and keep moving"},
            minDist=60f,maxDist=9999f,sub0Source="person",sub1Source="location",npcStepIndices=new[]{0,1}},

        new QuestTemplate { category="Escort", titleFmt="Don't Leave Them Waiting",
            descFmt="{0} missed the last transit and needs an escort to {1}.",
            baseReward=115, stepTypes=new[]{QuestStepType.Meet,QuestStepType.Escort},
            stepDescFmts=new[]{"Locate {0} — they should be waiting somewhere around the marked area","Get {0} safely to {1}. They know the way — just make sure they arrive"},
            minDist=55f,maxDist=9999f,sub0Source="person",sub1Source="location",npcStepIndices=new[]{0,1}},
    };

    // ─────────────────────────────────────────
    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        markerParent = new GameObject("QuestMarkers");

        if (cityGenerator == null) cityGenerator = FindFirstObjectByType<CityGenerator>();
        if (npcSpawner == null) npcSpawner = FindFirstObjectByType<NPCSpawner>();
        if (pathfinder == null) pathfinder = FindFirstObjectByType<CityPathfinder>();
        if (carControl == null) carControl = FindFirstObjectByType<CarControl>();

        if (cityGenerator != null) cityGenerator.OnCityGenerated += OnCityReady;
        if (npcSpawner != null) npcSpawner.OnNPCsSpawned += OnNPCsReady;

        BuildArrowMaterial();
        StartCoroutine(DelayedGenerate());
        StartCoroutine(ProximityLoop());
        StartCoroutine(TrackActiveNPC());
    }

    void OnDestroy()
    {
        if (cityGenerator != null) cityGenerator.OnCityGenerated -= OnCityReady;
        if (npcSpawner != null) npcSpawner.OnNPCsSpawned -= OnNPCsReady;
        ClearPathArrows();
    }

    // ─────────────────────────────────────────
    //  PATH ARROW SYSTEM
    // ─────────────────────────────────────────

    void BuildArrowMaterial()
    {
        Shader sh = Shader.Find("Sprites/Default")
                 ?? Shader.Find("Unlit/Color")
                 ?? Shader.Find("Standard");
        arrowMatBase = new Material(sh);
        arrowMatBase.color = arrowColor;
        if (arrowMatBase.HasProperty("_Color")) arrowMatBase.SetColor("_Color", arrowColor);
        arrowMatBase.renderQueue = 3000;
        if (arrowMatBase.HasProperty("_Mode")) arrowMatBase.SetFloat("_Mode", 2f);
        if (arrowMatBase.HasProperty("_Surface")) arrowMatBase.SetFloat("_Surface", 1f);
        arrowMatBase.EnableKeyword("_ALPHAPREMULTIPLY_ON");
    }

    void Update()
    {
        if (!showPathArrows) { ClearPathArrows(); return; }
        if (activeQuest == null) { ClearPathArrows(); return; }

        arrowPulseTimer += Time.deltaTime * arrowPulseSpeed;
        float pulse = 0.65f + 0.35f * Mathf.Sin(arrowPulseTimer);

        UpdatePathArrows(pulse);
    }

    void UpdatePathArrows(float pulse)
    {
        // Use car transform while driving, player transform on foot.
        Transform navTransform = NavigationTransform;
        if (navTransform == null) return;

        if (activeQuest == null || activeQuest.currentStep >= activeQuest.steps.Count)
        {
            ClearPathArrows(); return;
        }

        var step = activeQuest.steps[activeQuest.currentStep];
        Vector3 target = step.npcTarget != null ? step.npcTarget.position : step.worldPosition;
        Vector3 origin = navTransform.position;

        List<Vector3> spine;
        if (pathfinder != null)
        {
            pathfinder.SetStart(origin);
            pathfinder.SetEnd(target);
            spine = pathfinder.GetCurrentPath();
        }
        else
        {
            spine = null;
        }

        if (spine == null || spine.Count < 2)
            spine = BuildStraightSpine(origin, target, arrowSpacing * 0.5f);

        PlaceArrowsAlongSpine(spine, pulse);
    }

    List<Vector3> BuildStraightSpine(Vector3 from, Vector3 to, float sampleStep)
    {
        var pts = new List<Vector3>();
        float dist = Vector3.Distance(from, to);
        int n = Mathf.Max(2, Mathf.CeilToInt(dist / sampleStep));
        for (int i = 0; i <= n; i++)
            pts.Add(Vector3.Lerp(from, to, i / (float)n));
        return pts;
    }

    void PlaceArrowsAlongSpine(List<Vector3> spine, float pulse)
    {
        while (arrowObjects.Count < maxArrows) arrowObjects.Add(CreateArrowObject());
        foreach (var a in arrowObjects) a.SetActive(false);

        float distAccum = 0f;
        float nextArrow = arrowStartOffset;
        int arrowIdx = 0;

        for (int i = 1; i < spine.Count && arrowIdx < maxArrows; i++)
        {
            Vector3 segA = spine[i - 1];
            Vector3 segB = spine[i];
            float segLen = Vector3.Distance(segA, segB);
            Vector3 dir = (segB - segA).normalized;

            while (distAccum + segLen >= nextArrow && arrowIdx < maxArrows)
            {
                float lt = nextArrow - distAccum;
                Vector3 pos = segA + dir * lt;

                float groundY = pos.y;
                if (Physics.Raycast(new Vector3(pos.x, pos.y + 50f, pos.z),
                                    Vector3.down, out RaycastHit hit, 200f))
                    groundY = hit.point.y;
                pos.y = groundY + arrowHeight;

                PositionArrow(arrowObjects[arrowIdx], pos, dir, pulse);
                arrowObjects[arrowIdx].SetActive(true);
                arrowIdx++;
                nextArrow += arrowSpacing;
            }
            distAccum += segLen;
        }
    }

    GameObject CreateArrowObject()
    {
        var go = new GameObject("QuestArrow");
        go.transform.SetParent(markerParent.transform, true);

        var mf = go.AddComponent<MeshFilter>();
        var mr = go.AddComponent<MeshRenderer>();

        float hw = arrowHalfWidth;
        float len = arrowLength;
        float shw = hw * 1.28f;
        float slen = len * 1.22f;

        var verts = new Vector3[]
        {
            new Vector3(  0f,  -0.02f,  slen         ),
            new Vector3(-shw,  -0.02f, -slen * 0.35f ),
            new Vector3( shw,  -0.02f, -slen * 0.35f ),
            new Vector3(  0f,   0f,     len          ),
            new Vector3(-hw,    0f,    -len  * 0.35f ),
            new Vector3( hw,    0f,    -len  * 0.35f ),
        };

        var mesh = new Mesh { name = "ArrowMesh" };
        mesh.vertices = verts;
        mesh.subMeshCount = 2;
        mesh.SetTriangles(new[] { 0, 2, 1 }, 0);
        mesh.SetTriangles(new[] { 3, 4, 5 }, 1);
        mesh.RecalculateNormals();
        mf.sharedMesh = mesh;

        Shader sh = Shader.Find("Sprites/Default")
                 ?? Shader.Find("Unlit/Color")
                 ?? Shader.Find("Standard");

        var matOutline = new Material(sh) { color = arrowOutlineColor };
        var matFill = new Material(sh) { color = arrowColor };

        mr.sharedMaterials = new[] { matOutline, matFill };
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;

        return go;
    }

    void PositionArrow(GameObject go, Vector3 worldPos, Vector3 forwardDir, float pulse)
    {
        go.transform.position = worldPos;

        Vector3 flatDir = new Vector3(forwardDir.x, 0f, forwardDir.z);
        if (flatDir.sqrMagnitude > 0.001f)
            go.transform.rotation = Quaternion.LookRotation(flatDir, Vector3.up);

        var mr = go.GetComponent<MeshRenderer>();
        if (mr == null) return;

        var mats = mr.materials;
        if (mats.Length > 1)
            mats[1].color = new Color(arrowColor.r, arrowColor.g, arrowColor.b, arrowColor.a * pulse);
        mr.materials = mats;
    }

    void ClearPathArrows()
    {
        foreach (var a in arrowObjects)
            if (a != null) a.SetActive(false);
    }

    // ─────────────────────────────────────────
    //  CITY / NPC EVENTS
    // ─────────────────────────────────────────

    void OnCityReady() => GenerateQuests();

    void OnNPCsReady()
    {
        if (allQuests.Count == 0) GenerateQuests();
        else BindNPCsToAllQuests();
        OnQuestsChanged?.Invoke();
    }

    void BindNPCsToAllQuests()
    {
        if (npcSpawner == null) return;
        var npcs = npcSpawner.GetSpawnedNPCs();
        if (npcs == null || npcs.Count == 0) return;

        var rng = new System.Random(77);
        foreach (var quest in allQuests)
        {
            if (quest.npcStepIndices == null || quest.npcStepIndices.Length == 0) continue;
            int pick = rng.Next(npcs.Count);
            foreach (int si in quest.npcStepIndices)
            {
                if (si >= quest.steps.Count) continue;
                var step = quest.steps[si];
                if (step.npcTarget != null) continue;
                for (int attempt = 0; attempt < npcs.Count; attempt++)
                {
                    var npc = npcs[pick % npcs.Count]; pick++;
                    if (npc == null) continue;
                    step.npcTarget = npc.transform;
                    step.worldPosition = npc.transform.position;
                    break;
                }
            }
        }
    }

    IEnumerator TrackActiveNPC()
    {
        while (true)
        {
            yield return null;
            if (activeQuest == null || currentMarker == null) continue;
            var step = activeQuest.steps[activeQuest.currentStep];
            if (step.npcTarget == null) continue;
            step.worldPosition = step.npcTarget.position;
            currentMarker.transform.position = step.npcTarget.position;
        }
    }

    IEnumerator DelayedGenerate()
    {
        yield return null;
        if (allQuests.Count == 0) GenerateQuests();
    }

    // ─────────────────────────────────────────
    //  QUEST GENERATION
    // ─────────────────────────────────────────

    void GenerateQuests()
    {
        allQuests.Clear();
        allQuestsDoneRestarting = false;

        Vector3 cityCenter = cityGenerator != null ? cityGenerator.GetTerrainCenter() : Vector3.zero;
        float cityHalfX = cityGenerator != null ? cityGenerator.citySize.x * 0.5f : 100f;
        float cityHalfZ = cityGenerator != null ? cityGenerator.citySize.y * 0.5f : 100f;
        float safeHalfX = cityHalfX * 0.80f;
        float safeHalfZ = cityHalfZ * 0.80f;
        float capX = Mathf.Min(safeHalfX, questSpawnRadius);
        float capZ = Mathf.Min(safeHalfZ, questSpawnRadius);
        float cityMinExtent = Mathf.Min(safeHalfX, safeHalfZ);
        float distScale = Mathf.Clamp(cityMinExtent / 80f, 0.5f, 4f);

        var nodes = new List<Vector3>();

        if (cityGenerator?.sidewalkWaypoints != null && cityGenerator.sidewalkWaypoints.Count > 6)
        {
            foreach (var wp in cityGenerator.sidewalkWaypoints)
                if (Mathf.Abs(wp.x - cityCenter.x) <= capX && Mathf.Abs(wp.z - cityCenter.z) <= capZ)
                    nodes.Add(wp);
        }

        if (nodes.Count < 6)
        {
            nodes.Clear();
            var segs = cityGenerator?.GetRoadSegments();
            float roadHalf = cityGenerator != null ? cityGenerator.GetRoadWidth() * 0.5f : 4f;
            float swWidth = cityGenerator != null ? cityGenerator.sidewalkWidth : 2f;
            float stepOff = roadHalf + swWidth + 2f;

            if (segs != null)
            {
                foreach (var s in segs)
                {
                    Vector3 mid = (s.start + s.end) * 0.5f;
                    Vector3 dir = s.end - s.start; dir.y = 0f;
                    if (dir.sqrMagnitude < 0.001f) continue;
                    dir.Normalize();
                    Vector3 perp = new Vector3(-dir.z, 0f, dir.x);
                    foreach (float side in new[] { 1f, -1f })
                    {
                        Vector3 candidate = mid + perp * stepOff * side;
                        if (Physics.Raycast(new Vector3(candidate.x, 500f, candidate.z),
                                            Vector3.down, out RaycastHit hr, 1000f))
                            candidate.y = hr.point.y;
                        if (Mathf.Abs(candidate.x - cityCenter.x) <= capX &&
                            Mathf.Abs(candidate.z - cityCenter.z) <= capZ)
                            nodes.Add(candidate);
                    }
                }
            }

            if (nodes.Count < 6)
            {
                int gridN = 5;
                for (int xi = 0; xi < gridN; xi++)
                    for (int zi = 0; zi < gridN; zi++)
                    {
                        float tx = ((xi + 0.5f) / gridN - 0.5f) * 2f * capX;
                        float tz = ((zi + 0.5f) / gridN - 0.5f) * 2f * capZ;
                        Vector3 pt = new Vector3(cityCenter.x + tx, 0, cityCenter.z + tz);
                        if (Physics.Raycast(new Vector3(pt.x, 500f, pt.z),
                                            Vector3.down, out RaycastHit hg, 1000f))
                            pt.y = hg.point.y;
                        nodes.Add(pt);
                    }
            }
        }

        var npcNodes = new List<Vector3>();
        if (cityGenerator?.pedestrianWaypoints != null && cityGenerator.pedestrianWaypoints.Count > 0)
            foreach (var wp in cityGenerator.pedestrianWaypoints)
                if (Mathf.Abs(wp.x - cityCenter.x) <= capX && Mathf.Abs(wp.z - cityCenter.z) <= capZ)
                    npcNodes.Add(wp);
        if (npcNodes.Count == 0) npcNodes = nodes;

        var rng = new System.Random(UnityEngine.Random.Range(0, 99999));
        nodes = nodes.OrderBy(_ => rng.NextDouble()).ToList();

        var usedCategories = new HashSet<string>();
        var templateList = new List<QuestTemplate>(Templates).OrderBy(_ => rng.NextDouble()).ToList();
        var pickedTemplates = new List<QuestTemplate>();
        string lastCategory = "";

        foreach (var t in templateList)
        {
            if (pickedTemplates.Count >= questBoardSize) break;
            if (t.category == lastCategory || usedCategories.Contains(t.category)) continue;
            pickedTemplates.Add(t); usedCategories.Add(t.category); lastCategory = t.category;
        }
        if (pickedTemplates.Count < questBoardSize)
        {
            lastCategory = pickedTemplates.Count > 0 ? pickedTemplates[pickedTemplates.Count - 1].category : "";
            foreach (var t in templateList)
            {
                if (pickedTemplates.Count >= questBoardSize) break;
                if (t.category == lastCategory) continue;
                pickedTemplates.Add(t); lastCategory = t.category;
            }
        }

        int nodeIdx = 0;
        for (int q = 0; q < pickedTemplates.Count; q++)
        {
            var tmpl = pickedTemplates[q];
            string item = ItemNames[rng.Next(ItemNames.Length)];
            string person = PersonNames[rng.Next(PersonNames.Length)];
            string loc = LocationNames[rng.Next(LocationNames.Length)];
            string crime = CrimeDescriptions[rng.Next(CrimeDescriptions.Length)];
            string meetWhy = MeetReasons[rng.Next(MeetReasons.Length)];

            string sub0 = tmpl.sub0Source == "person" ? person
                        : tmpl.sub0Source == "crime" ? crime
                        : item;
            string sub1 = tmpl.sub1Source == "location" ? loc
                        : tmpl.sub1Source == "reason" ? meetWhy
                        : person;
            string sub2 = loc;

            var stepPositions = new List<Vector3>();
            Vector3 prevPos = Vector3.zero;
            bool firstStep = true;
            int npcIdx = nodeIdx % Mathf.Max(1, npcNodes.Count);

            foreach (var stepType in tmpl.stepTypes)
            {
                if (nodes.Count == 0) break;
                bool isSocial = stepType == QuestStepType.Meet || stepType == QuestStepType.Escort;
                var pool = isSocial ? npcNodes : nodes;
                ref int idx = ref (isSocial ? ref npcIdx : ref nodeIdx);

                if (firstStep) { prevPos = PickNode(pool, ref idx, Vector3.zero, 0f, 9999f); firstStep = false; }
                else { prevPos = PickNode(pool, ref idx, prevPos, tmpl.minDist * distScale, 9999f); }
                stepPositions.Add(prevPos);
            }

            if (stepPositions.Count < tmpl.stepTypes.Length) continue;

            var steps = new List<QuestStep>();
            for (int s = 0; s < tmpl.stepTypes.Length; s++)
            {
                string desc = tmpl.stepDescFmts[s]
                    .Replace("{0}", sub0).Replace("{1}", sub1).Replace("{2}", sub2);
                steps.Add(new QuestStep
                {
                    type = tmpl.stepTypes[s],
                    description = desc,
                    worldPosition = stepPositions[s],
                    radius = 6f
                });
            }

            string title = tmpl.titleFmt.Replace("{0}", sub0).Replace("{1}", sub1).Replace("{2}", sub2);
            string questDesc = tmpl.descFmt.Replace("{0}", sub0).Replace("{1}", sub1).Replace("{2}", sub2);
            int reward = tmpl.baseReward + q * 10
                       + (steps.Count > 2 ? 40 : 0)
                       + (steps.Count > 3 ? 30 : 0);

            if (tmpl.npcStepIndices != null && npcSpawner != null)
            {
                var npcs = npcSpawner.GetSpawnedNPCs();
                if (npcs != null && npcs.Count > 0)
                {
                    int npcPick = rng.Next(npcs.Count);
                    foreach (int si in tmpl.npcStepIndices)
                    {
                        if (si < steps.Count && npcs[npcPick] != null)
                        {
                            steps[si].npcTarget = npcs[npcPick].transform;
                            steps[si].worldPosition = npcs[npcPick].transform.position;
                            npcPick = (npcPick + 1) % npcs.Count;
                        }
                    }
                }
            }

            allQuests.Add(new Quest
            {
                id = $"quest_{q}",
                title = title,
                description = questDesc,
                rewardPoints = reward,
                category = tmpl.category,
                steps = steps,
                npcStepIndices = tmpl.npcStepIndices,
            });
        }

        OnQuestsChanged?.Invoke();
    }

    Vector3 PickNode(List<Vector3> pool, ref int idx, Vector3 from, float minDist, float maxDist)
    {
        for (int attempt = 0; attempt < Mathf.Min(20, pool.Count); attempt++)
        {
            Vector3 candidate = pool[idx % pool.Count]; idx++;
            float d = Vector2.Distance(new Vector2(candidate.x, candidate.z),
                                       new Vector2(from.x, from.z));
            if (minDist == 0f || (d >= minDist && d <= maxDist)) return candidate;
        }
        Vector3 fb = pool[idx % pool.Count]; idx++;
        return fb;
    }

    // ─────────────────────────────────────────
    //  TIME SCORING
    // ─────────────────────────────────────────

    /// <summary>
    /// Returns a multiplier in [minPenaltyMultiplier, maxBonusMultiplier] based on elapsed time.
    /// Fast completion → bonus; slow completion → penalty.
    /// </summary>
    float ComputeTimeMultiplier(float elapsed)
    {
        if (elapsed <= bonusTimeThreshold)
            return maxBonusMultiplier;

        if (elapsed >= penaltyTimeThreshold)
            return minPenaltyMultiplier;

        // Linearly interpolate between bonus and penalty thresholds.
        float t = (elapsed - bonusTimeThreshold) / (penaltyTimeThreshold - bonusTimeThreshold);
        return Mathf.Lerp(maxBonusMultiplier, minPenaltyMultiplier, t);
    }

    /// <summary>Formats seconds as  "M:SS".</summary>
    public static string FormatTime(float seconds)
    {
        int totalSec = Mathf.FloorToInt(seconds);
        int m = totalSec / 60;
        int s = totalSec % 60;
        return $"{m}:{s:D2}";
    }

    // ─────────────────────────────────────────
    //  PUBLIC API
    // ─────────────────────────────────────────

    public List<Quest> GetAllQuests() => allQuests;
    public Quest GetActiveQuest() => activeQuest;

    public void AcceptQuest(Quest q)
    {
        if (q.status != QuestStatus.Available) return;
        if (activeQuest != null) return;

        activeQuest = q;
        q.status = QuestStatus.Active;
        q.currentStep = 0;

        // Start the quest timer.
        questStartTime = Time.time;

        AdvanceCompletedSteps();

        if (activeQuest != null)
        {
            var firstStep = q.steps[q.currentStep];
            Vector3 markerPos = firstStep.npcTarget != null
                ? firstStep.npcTarget.position
                : firstStep.worldPosition;
            SpawnMarker(markerPos);
            OnQuestAccepted?.Invoke(q);
            OnQuestsChanged?.Invoke();
        }
    }

    void AdvanceCompletedSteps()
    {
        if (activeQuest == null || playerTransform == null) return;
        while (activeQuest != null && activeQuest.currentStep < activeQuest.steps.Count)
        {
            var step = activeQuest.steps[activeQuest.currentStep];
            float dist = Vector3.Distance(
                new Vector3(playerTransform.position.x, 0, playerTransform.position.z),
                new Vector3(step.worldPosition.x, 0, step.worldPosition.z));
            if (dist <= step.radius) CompleteCurrentStep();
            else break;
        }
    }

    public void AbandonQuest()
    {
        if (activeQuest == null) return;
        activeQuest.status = QuestStatus.Available;
        activeQuest.currentStep = 0;
        foreach (var s in activeQuest.steps) s.done = false;
        activeQuest = null;
        questStartTime = 0f;
        DestroyMarker();
        ClearPathArrows();
        OnQuestsChanged?.Invoke();
    }

    public void RegenerateQuests() { AbandonQuest(); GenerateQuests(); }

    // ─────────────────────────────────────────
    //  PROXIMITY LOOP
    // ─────────────────────────────────────────

    IEnumerator ProximityLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(checkInterval);
            if (activeQuest == null || playerTransform == null) continue;

            var step = activeQuest.steps[activeQuest.currentStep];
            if (step.done) continue;
            if (step.npcTarget != null) step.worldPosition = step.npcTarget.position;

            // Check proximity against whichever transform is navigating (car or player).
            Transform nav = NavigationTransform;
            if (nav == null) continue;

            float dist = Vector3.Distance(
                new Vector3(nav.position.x, 0, nav.position.z),
                new Vector3(step.worldPosition.x, 0, step.worldPosition.z));

            if (dist <= step.radius) CompleteCurrentStep();
        }
    }

    void CompleteCurrentStep()
    {
        var q = activeQuest;
        int idx = q.currentStep;
        q.steps[idx].done = true;
        OnStepCompleted?.Invoke(q, idx);

        if (idx + 1 < q.steps.Count)
        {
            q.currentStep++;
            var nextStep = q.steps[q.currentStep];
            Vector3 nextPos = nextStep.npcTarget != null
                ? nextStep.npcTarget.position
                : nextStep.worldPosition;
            SpawnMarker(nextPos);
            OnQuestsChanged?.Invoke();
        }
        else
        {
            // ── Quest complete — apply time-based scoring ──────────────
            float elapsed = Time.time - questStartTime;
            float multiplier = ComputeTimeMultiplier(elapsed);
            int finalPoints = Mathf.RoundToInt(q.rewardPoints * multiplier);
            int timeBonus = finalPoints - q.rewardPoints;

            LastQuestElapsed = elapsed;
            LastQuestMultiplier = multiplier;
            LastQuestTimeBonus = timeBonus;

            q.status = QuestStatus.Completed;
            totalPoints += finalPoints;
            activeQuest = null;
            questStartTime = 0f;
            DestroyMarker();
            ClearPathArrows();
            OnQuestCompleted?.Invoke(q, finalPoints);
            OnQuestsChanged?.Invoke();

            // ── Check if entire board is complete ──────────────────────
            if (!allQuestsDoneRestarting && allQuests.Count > 0 &&
                allQuests.All(x => x.status == QuestStatus.Completed))
            {
                allQuestsDoneRestarting = true;
                OnAllQuestsCompleted?.Invoke();
                StartCoroutine(AutoRestartAfterDelay());
            }
        }
    }

    IEnumerator AutoRestartAfterDelay()
    {
        yield return new WaitForSeconds(autoRestartDelay);
        RegenerateQuests();
    }

    // ─────────────────────────────────────────
    //  MARKER
    // ─────────────────────────────────────────

    void SpawnMarker(Vector3 pos)
    {
        DestroyMarker();

        float groundY = pos.y;
        if (Physics.Raycast(new Vector3(pos.x, 500f, pos.z), Vector3.down, out RaycastHit hit, 1000f))
            groundY = hit.point.y;

        currentMarker = new GameObject("QuestMarker");
        currentMarker.transform.SetParent(markerParent.transform);
        currentMarker.transform.position = new Vector3(pos.x, groundY, pos.z);

        Material goldMat = MakeMarkerMaterial(new Color(1f, 0.80f, 0f), 4f);
        Material whiteMat = MakeMarkerMaterial(Color.white, 2f);

        AddPrimitive(PrimitiveType.Cylinder, currentMarker.transform, new Vector3(0, 0.4f, 0), new Vector3(8f, 0.4f, 8f), goldMat);
        AddPrimitive(PrimitiveType.Cylinder, currentMarker.transform, new Vector3(0, 21f, 0), new Vector3(1.2f, 20f, 1.2f), goldMat);
        AddPrimitive(PrimitiveType.Sphere, currentMarker.transform, new Vector3(0, 45f, 0), Vector3.one * 8f, goldMat);
        AddPrimitive(PrimitiveType.Sphere, currentMarker.transform, new Vector3(0, 45f, 0), Vector3.one * 4f, whiteMat);

        currentMarker.AddComponent<QuestMarkerPulse>();
    }

    static void AddPrimitive(PrimitiveType type, Transform parent,
                              Vector3 localPos, Vector3 localScale, Material mat)
    {
        var go = GameObject.CreatePrimitive(type);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = localPos;
        go.transform.localScale = localScale;
        var mr = go.GetComponent<MeshRenderer>();
        if (mr) mr.sharedMaterial = mat;
        var col = go.GetComponent<Collider>();
        if (col) UnityEngine.Object.Destroy(col);
    }

    static Material MakeMarkerMaterial(Color col, float emissionStrength)
    {
        Shader sh = Shader.Find("Universal Render Pipeline/Lit")
                 ?? Shader.Find("Universal Render Pipeline/Unlit")
                 ?? Shader.Find("Standard")
                 ?? Shader.Find("HDRP/Lit")
                 ?? Shader.Find("Unlit/Color");

        if (sh == null)
        {
            var any = UnityEngine.Object.FindFirstObjectByType<MeshRenderer>();
            sh = any != null ? any.sharedMaterial.shader
                             : Shader.Find("Hidden/InternalErrorShader");
        }

        var mat = new Material(sh);
        if (mat.HasProperty("_Color")) mat.SetColor("_Color", col);
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", col);
        if (mat.HasProperty("_EmissionColor"))
        {
            mat.EnableKeyword("_EMISSION");
            mat.SetColor("_EmissionColor", col * emissionStrength);
        }
        return mat;
    }

    void DestroyMarker()
    {
        if (currentMarker != null) Destroy(currentMarker);
        currentMarker = null;
    }
}

// ─────────────────────────────────────────────
//  PULSE ANIMATION
// ─────────────────────────────────────────────

public class QuestMarkerPulse : MonoBehaviour
{
    float baseY;
    void Start() => baseY = transform.position.y;
    void Update()
    {
        float y = baseY + Mathf.Sin(Time.time * 1.8f) * 1.2f;
        transform.position = new Vector3(transform.position.x, y, transform.position.z);
        float s = 1f + Mathf.Sin(Time.time * 2.5f) * 0.06f;
        transform.localScale = Vector3.one * s;
    }
}