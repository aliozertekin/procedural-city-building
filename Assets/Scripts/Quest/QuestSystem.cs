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
    public Vector3 worldPosition;   // updated each frame if npcTarget is set
    public float radius = 6f;
    [HideInInspector] public bool done;
    // When set, worldPosition is overwritten each frame with this NPC's position
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
    // Step indices that must track a live NPC — stored so BindNPCsToAllQuests
    // can re-apply NPC transforms after NPCs finish spawning.
    [NonSerialized] public int[] npcStepIndices;
}

// ─────────────────────────────────────────────
//  QUEST TEMPLATE  (data-driven generation)
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
    // "item" | "person" | "crime"
    public string sub0Source;
    // "person" | "location" | "reason"
    public string sub1Source;
    // Which step indices should track a live NPC transform instead of a fixed point.
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

    [Header("Settings")]
    public float checkInterval = 0.3f;
    [Tooltip("How many quests appear on the board at once")]
    public int questBoardSize = 8;
    [Tooltip("Max world-unit radius from road nodes to pick quest locations.\n" +
             "Lower = quests stay tighter to the built area.")]
    public float questSpawnRadius = 60f;

    // Events the UI listens to
    public event Action<Quest> OnQuestAccepted;
    public event Action<Quest, int> OnStepCompleted;
    public event Action<Quest> OnQuestCompleted;
    public event Action OnQuestsChanged;

    public int totalPoints { get; private set; }

    private List<Quest> allQuests = new List<Quest>();
    private Quest activeQuest;

    private GameObject markerParent;
    private GameObject currentMarker;

    // ─── content tables ──────────────────────
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
        "a suspicious vehicle",  "fresh graffiti on a wall",
        "a broken street light", "an abandoned bag",
        "unusual late-night activity", "a blocked storm drain",
        "a cracked gas main",    "a missing manhole cover",
        "signs of forced entry", "a discarded firearm"
    };

    static readonly string[] MeetReasons =
    {
        "to hand off the package",
        "for a quick exchange",
        "before they leave town",
        "to collect a signature",
        "to pass on urgent news"
    };

    // ─── templates ───────────────────────────
    // sub0Source: "item" | "person" | "crime"
    // sub1Source: "person" | "location" | "reason"
    static readonly QuestTemplate[] Templates =
    {
        // ── DELIVERY ─────────────────────────────────────────────────────
        new QuestTemplate
        {
            category     = "Delivery",
            titleFmt     = "Package for {1}",
            descFmt      = "{1} has been expecting {0} and they're getting impatient. "
                         + "Pick it up from the collection point and get it delivered before they give up waiting.",
            baseReward   = 80,
            stepTypes    = new[]{ QuestStepType.PickUp, QuestStepType.Meet },
            stepDescFmts = new[]{
                "Collect {0} from the marked pickup point",
                "Deliver {0} to {1} — they're somewhere in the area"
            },
            minDist = 45f, maxDist = 9999f,
            sub0Source = "item", sub1Source = "person",
            npcStepIndices = new int[]{ 1 },
        },

        // ── URGENT DELIVERY ──────────────────────────────────────────────
        new QuestTemplate
        {
            category     = "Delivery",
            titleFmt     = "URGENT: {0} for {1}",
            descFmt      = "Crisis situation — {1} needs {0} immediately and there are no alternatives. "
                         + "Pick it up and move fast. This is not a drill.",
            baseReward   = 160,
            stepTypes    = new[]{ QuestStepType.PickUp, QuestStepType.Meet },
            stepDescFmts = new[]{
                "Grab {0} from the pickup point — don't waste a second",
                "Get {0} to {1} right now. They're waiting and the clock is ticking"
            },
            minDist = 45f, maxDist = 9999f,
            sub0Source = "item", sub1Source = "person",
            npcStepIndices = new int[]{ 1 },
        },

        // ── CITY-WIDE COURIER RUN ────────────────────────────────────────
        new QuestTemplate
        {
            category     = "Delivery",
            titleFmt     = "City-Wide Courier Run",
            descFmt      = "Three stops, one trip. Collect {0}, make a scheduled drop at {2}, "
                         + "then get the remainder to {1}. Stay sharp — the route is longer than it looks.",
            baseReward   = 220,
            stepTypes    = new[]{ QuestStepType.PickUp, QuestStepType.Deliver, QuestStepType.Meet },
            stepDescFmts = new[]{
                "Pick up {0} from the first location — everything should be there",
                "Make a partial drop-off at {2}. Leave what's expected and take the rest",
                "Finish the run — deliver the remaining {0} to {1}"
            },
            minDist = 55f, maxDist = 9999f,
            sub0Source = "item", sub1Source = "person",
            npcStepIndices = new int[]{ 2 },
        },

        // ── HELP: SOMEONE NEEDS YOU ───────────────────────────────────────
        new QuestTemplate
        {
            category     = "Help",
            titleFmt     = "{0} Sent Word",
            descFmt      = "{0} has been asking around for a reliable pair of hands. "
                         + "Track them down and hear what they need — it sounds like it pays.",
            baseReward   = 50,
            stepTypes    = new[]{ QuestStepType.Meet },
            stepDescFmts = new[]{
                "Find {0} and hear what they have to say"
            },
            minDist = 20f, maxDist = 9999f,
            sub0Source = "person", sub1Source = "reason",
            npcStepIndices = new int[]{ 0 },
        },

        // ── HELP: QUICK FAVOUR ───────────────────────────────────────────
        new QuestTemplate
        {
            category     = "Help",
            titleFmt     = "A Favour for {1}",
            descFmt      = "{1} is tied up and can't leave their post. They need someone to grab {0} and bring it back. "
                         + "Simple job, decent reward.",
            baseReward   = 65,
            stepTypes    = new[]{ QuestStepType.PickUp, QuestStepType.Meet },
            stepDescFmts = new[]{
                "Pick up {0} from the marked location",
                "Return {0} to {1} — they're counting on you"
            },
            minDist = 30f, maxDist = 9999f,
            sub0Source = "item", sub1Source = "person",
            npcStepIndices = new int[]{ 1 },
        },

        // ── HELP: LOST AND FOUND ─────────────────────────────────────────
        new QuestTemplate
        {
            category     = "Help",
            titleFmt     = "Lost: {0}",
            descFmt      = "{1} has lost {0} somewhere in the city and they're desperate to get it back. "
                         + "Help them locate it and return it safely.",
            baseReward   = 75,
            stepTypes    = new[]{ QuestStepType.GoTo, QuestStepType.Meet },
            stepDescFmts = new[]{
                "Check the area where {0} was last seen",
                "Return {0} to {1} — they'll be relieved to see it"
            },
            minDist = 35f, maxDist = 9999f,
            sub0Source = "item", sub1Source = "person",
            npcStepIndices = new int[]{ 1 },
        },

        // ── INVESTIGATION: SCENE CHECK ───────────────────────────────────
        new QuestTemplate
        {
            category     = "Investigation",
            titleFmt     = "Look Into: {0}",
            descFmt      = "Several residents have reported {0} in the area and people are getting uneasy. "
                         + "{1} wants eyes on it before it becomes a bigger problem. Inspect the scene and report back.",
            baseReward   = 80,
            stepTypes    = new[]{ QuestStepType.Inspect, QuestStepType.Meet },
            stepDescFmts = new[]{
                "Investigate the scene — residents reported {0} right around here",
                "Report your findings to {1}. They're waiting for your assessment"
            },
            minDist = 35f, maxDist = 9999f,
            sub0Source = "crime", sub1Source = "person",
            npcStepIndices = new int[]{ 1 },
        },

        // ── INVESTIGATION: PATROL ────────────────────────────────────────
        new QuestTemplate
        {
            category     = "Investigation",
            titleFmt     = "Sweep the Area: {0}",
            descFmt      = "Two separate sightings of {0} have been reported on opposite ends of the block. "
                         + "Check both locations and relay everything you find to {1}.",
            baseReward   = 110,
            stepTypes    = new[]{ QuestStepType.Inspect, QuestStepType.Inspect, QuestStepType.Meet },
            stepDescFmts = new[]{
                "Check the first area — scan for any trace of {0}",
                "Move on to the second location and repeat the check",
                "Report everything to {1} — don't leave anything out"
            },
            minDist = 40f, maxDist = 9999f,
            sub0Source = "crime", sub1Source = "person",
            npcStepIndices = new int[]{ 2 },
        },

        // ── INVESTIGATION: STAKEOUT ──────────────────────────────────────
        new QuestTemplate
        {
            category     = "Investigation",
            titleFmt     = "Someone Needs to Check This Out",
            descFmt      = "{1} received an anonymous tip about {0} near two spots in the city. "
                         + "They can't go themselves — you'll need to verify the report firsthand.",
            baseReward   = 95,
            stepTypes    = new[]{ QuestStepType.GoTo, QuestStepType.Inspect, QuestStepType.Meet },
            stepDescFmts = new[]{
                "Head to the area flagged in the report",
                "Inspect the exact spot — look closely for {0}",
                "Relay what you found to {1}. They're expecting your call"
            },
            minDist = 40f, maxDist = 9999f,
            sub0Source = "crime", sub1Source = "person",
            npcStepIndices = new int[]{ 2 },
        },

        // ── ESCORT: SAFE PASSAGE ─────────────────────────────────────────
        new QuestTemplate
        {
            category     = "Escort",
            titleFmt     = "Safe Passage: {0}",
            descFmt      = "{0} needs to reach {1} and they're not comfortable going alone. "
                         + "The streets have been tense lately. Find them and walk them there personally.",
            baseReward   = 130,
            stepTypes    = new[]{ QuestStepType.Meet, QuestStepType.Escort },
            stepDescFmts = new[]{
                "Find {0} — they're somewhere nearby and they're anxious",
                "Walk {0} safely all the way to {1}. Stay close and keep moving"
            },
            minDist = 60f, maxDist = 9999f,
            sub0Source = "person", sub1Source = "location",
            npcStepIndices = new int[]{ 0, 1 },
        },

        // ── ESCORT: LATE-NIGHT WALK ──────────────────────────────────────
        new QuestTemplate
        {
            category     = "Escort",
            titleFmt     = "Don't Leave Them Waiting",
            descFmt      = "{0} missed the last transit and needs an escort to {1}. "
                         + "Find them before they try to walk it alone — that's a bad idea right now.",
            baseReward   = 115,
            stepTypes    = new[]{ QuestStepType.Meet, QuestStepType.Escort },
            stepDescFmts = new[]{
                "Locate {0} — they should be waiting somewhere around the marked area",
                "Get {0} safely to {1}. They know the way — just make sure they arrive"
            },
            minDist = 55f, maxDist = 9999f,
            sub0Source = "person", sub1Source = "location",
            npcStepIndices = new int[]{ 0, 1 },
        },
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

        if (cityGenerator == null)
            cityGenerator = FindFirstObjectByType<CityGenerator>();
        if (npcSpawner == null)
            npcSpawner = FindFirstObjectByType<NPCSpawner>();

        if (cityGenerator != null)
            cityGenerator.OnCityGenerated += OnCityReady;

        // Subscribe so we re-bind NPC transforms after NPCs finish spawning.
        // This fixes the race condition where GenerateQuests runs before NPCs exist.
        if (npcSpawner != null)
            npcSpawner.OnNPCsSpawned += OnNPCsReady;

        StartCoroutine(DelayedGenerate());
        StartCoroutine(ProximityLoop());
        StartCoroutine(TrackActiveNPC());   // moves marker to follow NPC every frame
    }

    void OnDestroy()
    {
        if (cityGenerator != null)
            cityGenerator.OnCityGenerated -= OnCityReady;
        if (npcSpawner != null)
            npcSpawner.OnNPCsSpawned -= OnNPCsReady;
    }

    void OnCityReady() => GenerateQuests();

    // Called by NPCSpawner.OnNPCsSpawned after all NPCs are placed in the scene.
    // Applies live NPC transforms to any quest steps that need them.
    void OnNPCsReady()
    {
        if (allQuests.Count == 0)
            GenerateQuests();   // city was ready but quests not yet built
        else
            BindNPCsToAllQuests();
        OnQuestsChanged?.Invoke();
    }

    // Walks every quest and assigns a live NPC transform to each npcStepIndex step.
    // Safe to call multiple times — skips steps that already have a valid target.
    void BindNPCsToAllQuests()
    {
        if (npcSpawner == null) return;
        var npcs = npcSpawner.GetSpawnedNPCs();
        if (npcs == null || npcs.Count == 0) return;

        var rng = new System.Random(77);
        foreach (var quest in allQuests)
        {
            if (quest.npcStepIndices == null || quest.npcStepIndices.Length == 0) continue;

            // Each quest gets a different starting NPC so markers spread across the city
            int pick = rng.Next(npcs.Count);
            foreach (int si in quest.npcStepIndices)
            {
                if (si >= quest.steps.Count) continue;
                var step = quest.steps[si];
                if (step.npcTarget != null) continue;   // already bound

                // Find the next non-null NPC
                for (int attempt = 0; attempt < npcs.Count; attempt++)
                {
                    var npc = npcs[pick % npcs.Count];
                    pick++;
                    if (npc == null) continue;
                    step.npcTarget = npc.transform;
                    step.worldPosition = npc.transform.position;
                    break;
                }
            }
        }
    }

    // Runs every frame and keeps the active quest marker on top of the target NPC.
    IEnumerator TrackActiveNPC()
    {
        while (true)
        {
            yield return null;
            if (activeQuest == null || currentMarker == null) continue;
            var step = activeQuest.steps[activeQuest.currentStep];
            if (step.npcTarget == null) continue;

            // Keep worldPosition fresh for the ProximityLoop distance check
            step.worldPosition = step.npcTarget.position;

            // Move the marker to hover above the NPC
            var markerPos = step.npcTarget.position;
            currentMarker.transform.position = markerPos;
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

        // ── 1. City bounds ────────────────────────────────────────────────
        Vector3 cityCenter = cityGenerator != null
            ? cityGenerator.GetTerrainCenter()
            : Vector3.zero;

        float cityHalfX = cityGenerator != null ? cityGenerator.citySize.x * 0.5f : 100f;
        float cityHalfZ = cityGenerator != null ? cityGenerator.citySize.y * 0.5f : 100f;

        float safeHalfX = cityHalfX * 0.80f;
        float safeHalfZ = cityHalfZ * 0.80f;

        float capX = Mathf.Min(safeHalfX, questSpawnRadius);
        float capZ = Mathf.Min(safeHalfZ, questSpawnRadius);

        // ── Distance scaling: bigger city → steps farther apart ───────────
        // A 160-unit half-extent is the baseline (scale = 1.0).
        // Small cities scale down so quests don't overshoot; large ones scale up
        // so chain quests actually feel like a journey across town.
        float cityMinExtent = Mathf.Min(safeHalfX, safeHalfZ);
        float distScale = Mathf.Clamp(cityMinExtent / 80f, 0.5f, 4f);

        // ── 2. Sidewalk waypoints as spawn candidates ─────────────────────
        var nodes = new List<Vector3>();

        if (cityGenerator?.sidewalkWaypoints != null &&
            cityGenerator.sidewalkWaypoints.Count > 6)
        {
            foreach (var wp in cityGenerator.sidewalkWaypoints)
            {
                if (Mathf.Abs(wp.x - cityCenter.x) <= capX &&
                    Mathf.Abs(wp.z - cityCenter.z) <= capZ)
                    nodes.Add(wp);
            }
        }

        // ── 3. Fallback: road nodes offset sideways ───────────────────────
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

        // ── 4. NPC waypoints for social steps ────────────────────────────
        var npcNodes = new List<Vector3>();
        if (cityGenerator?.pedestrianWaypoints != null &&
            cityGenerator.pedestrianWaypoints.Count > 0)
        {
            foreach (var wp in cityGenerator.pedestrianWaypoints)
            {
                if (Mathf.Abs(wp.x - cityCenter.x) <= capX &&
                    Mathf.Abs(wp.z - cityCenter.z) <= capZ)
                    npcNodes.Add(wp);
            }
        }
        if (npcNodes.Count == 0) npcNodes = nodes;

        // ── 5. Shuffle nodes and pick templates ──────────────────────────
        var rng = new System.Random(UnityEngine.Random.Range(0, 99999));
        nodes = nodes.OrderBy(_ => rng.NextDouble()).ToList();

        var usedCategories = new HashSet<string>();
        var templateList = new List<QuestTemplate>(Templates);
        var pickedTemplates = new List<QuestTemplate>();
        string lastCategory = "";

        templateList = templateList.OrderBy(_ => rng.NextDouble()).ToList();

        // First pass: one quest per category (best variety)
        foreach (var t in templateList)
        {
            if (pickedTemplates.Count >= questBoardSize) break;
            if (t.category == lastCategory) continue;
            if (usedCategories.Contains(t.category)) continue;
            pickedTemplates.Add(t);
            usedCategories.Add(t.category);
            lastCategory = t.category;
        }

        // Second pass: fill remaining slots allowing repeats, no adjacent duplicates
        if (pickedTemplates.Count < questBoardSize)
        {
            lastCategory = pickedTemplates.Count > 0
                ? pickedTemplates[pickedTemplates.Count - 1].category : "";

            foreach (var t in templateList)
            {
                if (pickedTemplates.Count >= questBoardSize) break;
                if (t.category == lastCategory) continue;
                pickedTemplates.Add(t);
                lastCategory = t.category;
            }
        }

        // ── 6. Build each quest ───────────────────────────────────────────
        int nodeIdx = 0;
        for (int q = 0; q < pickedTemplates.Count; q++)
        {
            var tmpl = pickedTemplates[q];

            string item = ItemNames[rng.Next(ItemNames.Length)];
            string person = PersonNames[rng.Next(PersonNames.Length)];
            string loc = LocationNames[rng.Next(LocationNames.Length)];
            string crime = CrimeDescriptions[rng.Next(CrimeDescriptions.Length)];
            string meetWhy = MeetReasons[rng.Next(MeetReasons.Length)];

            // Resolve {0} and {1} from template source declarations
            string sub0 = tmpl.sub0Source switch
            {
                "person" => person,
                "crime" => crime,
                _ => item
            };
            string sub1 = tmpl.sub1Source switch
            {
                "location" => loc,
                "reason" => meetWhy,
                _ => person
            };
            string sub2 = loc;   // third stop for multi-stop quests

            // ── Place step positions ──────────────────────────────────────
            var stepPositions = new List<Vector3>();
            Vector3 prevPos = Vector3.zero;
            bool firstStep = true;

            int npcIdx = nodeIdx % Mathf.Max(1, npcNodes.Count);

            foreach (var stepType in tmpl.stepTypes)
            {
                if (nodes.Count == 0) break;

                bool isSocialStep = stepType == QuestStepType.Meet ||
                                    stepType == QuestStepType.Escort;
                var pool = isSocialStep ? npcNodes : nodes;
                ref int idx = ref (isSocialStep ? ref npcIdx : ref nodeIdx);

                // Scale distances by city size so chain quests span the city properly
                if (firstStep)
                {
                    prevPos = PickNode(pool, ref idx, Vector3.zero, 0f, 9999f);
                    firstStep = false;
                }
                else
                {
                    float scaledMin = tmpl.minDist * distScale;
                    float scaledMax = 9999f;
                    prevPos = PickNode(pool, ref idx, prevPos, scaledMin, scaledMax);
                }
                stepPositions.Add(prevPos);
            }

            if (stepPositions.Count < tmpl.stepTypes.Length) continue;

            // ── Build step list ───────────────────────────────────────────
            var steps = new List<QuestStep>();
            for (int s = 0; s < tmpl.stepTypes.Length; s++)
            {
                string desc = tmpl.stepDescFmts[s]
                    .Replace("{0}", sub0)
                    .Replace("{1}", sub1)
                    .Replace("{2}", sub2);

                steps.Add(new QuestStep
                {
                    type = tmpl.stepTypes[s],
                    description = desc,
                    worldPosition = stepPositions[s],
                    radius = 6f
                });
            }

            string title = tmpl.titleFmt
                .Replace("{0}", sub0)
                .Replace("{1}", sub1)
                .Replace("{2}", sub2);

            string questDesc = tmpl.descFmt
                .Replace("{0}", sub0)
                .Replace("{1}", sub1)
                .Replace("{2}", sub2);

            // Longer quests pay more; every additional step adds a bonus
            int reward = tmpl.baseReward
                + q * 10
                + (steps.Count > 2 ? 40 : 0)
                + (steps.Count > 3 ? 30 : 0);

            // ── Bind live NPCs to social steps ────────────────────────────
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

            // Store which indices need NPC binding so BindNPCsToAllQuests can
            // re-apply later if NPCs weren't ready during GenerateQuests.
            var quest = new Quest
            {
                id = $"quest_{q}",
                title = title,
                description = questDesc,
                rewardPoints = reward,
                category = tmpl.category,
                steps = steps,
                npcStepIndices = tmpl.npcStepIndices,
            };
            allQuests.Add(quest);
        }

        OnQuestsChanged?.Invoke();
    }

    static void TryAddNode(Vector3 v, HashSet<string> seen, List<Vector3> list)
    {
        string key = $"{v.x:F0},{v.z:F0}";
        if (seen.Add(key)) list.Add(v);
    }

    static void AddRoadDir(Dictionary<string, List<Vector3>> dict, Vector3 node, Vector3 dir)
    {
        string key = $"{node.x:F0},{node.z:F0}";
        if (!dict.TryGetValue(key, out var list))
        { list = new List<Vector3>(); dict[key] = list; }
        list.Add(dir);
    }

    static float MinDistToRoads(Vector2 p, List<(Vector2 a, Vector2 b)> segs)
    {
        float best = float.MaxValue;
        foreach (var (a, b) in segs)
        {
            Vector2 ab = b - a;
            float lenSq = ab.sqrMagnitude;
            float t = lenSq < 0.001f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / lenSq);
            float d = Vector2.Distance(p, a + t * ab);
            if (d < best) best = d;
        }
        return best;
    }

    // Pick a node from the pool within [minDist, maxDist] of `from`.
    // Falls back to any node if none meet criteria after 20 tries.
    Vector3 PickNode(List<Vector3> pool, ref int idx,
                     Vector3 from, float minDist, float maxDist)
    {
        for (int attempt = 0; attempt < Mathf.Min(20, pool.Count); attempt++)
        {
            Vector3 candidate = pool[idx % pool.Count];
            idx++;
            float d = Vector2.Distance(new Vector2(candidate.x, candidate.z),
                                       new Vector2(from.x, from.z));
            if (minDist == 0f || (d >= minDist && d <= maxDist))
                return candidate;
        }
        Vector3 fb = pool[idx % pool.Count];
        idx++;
        return fb;
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
        while (activeQuest != null &&
               activeQuest.currentStep < activeQuest.steps.Count)
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
        DestroyMarker();
        OnQuestsChanged?.Invoke();
    }

    public void RegenerateQuests()
    {
        AbandonQuest();
        GenerateQuests();
    }

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

            if (step.npcTarget != null)
                step.worldPosition = step.npcTarget.position;

            float dist = Vector3.Distance(
                new Vector3(playerTransform.position.x, 0, playerTransform.position.z),
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
            q.status = QuestStatus.Completed;
            totalPoints += q.rewardPoints;
            activeQuest = null;
            DestroyMarker();
            OnQuestCompleted?.Invoke(q);
            OnQuestsChanged?.Invoke();
        }
    }

    // ─────────────────────────────────────────
    //  MARKER
    // ─────────────────────────────────────────

    void SpawnMarker(Vector3 pos)
    {
        DestroyMarker();

        float groundY = pos.y;
        if (Physics.Raycast(new Vector3(pos.x, 500f, pos.z), Vector3.down,
                            out RaycastHit hit, 1000f))
            groundY = hit.point.y;

        currentMarker = new GameObject("QuestMarker");
        currentMarker.transform.SetParent(markerParent.transform);
        currentMarker.transform.position = new Vector3(pos.x, groundY, pos.z);

        Material goldMat = MakeMarkerMaterial(new Color(1f, 0.80f, 0f), 4f);
        Material whiteMat = MakeMarkerMaterial(Color.white, 2f);

        AddPrimitive(PrimitiveType.Cylinder, currentMarker.transform,
                     new Vector3(0, 0.4f, 0), new Vector3(8f, 0.4f, 8f), goldMat);
        AddPrimitive(PrimitiveType.Cylinder, currentMarker.transform,
                     new Vector3(0, 21f, 0), new Vector3(1.2f, 20f, 1.2f), goldMat);
        AddPrimitive(PrimitiveType.Sphere, currentMarker.transform,
                     new Vector3(0, 45f, 0), Vector3.one * 8f, goldMat);
        AddPrimitive(PrimitiveType.Sphere, currentMarker.transform,
                     new Vector3(0, 45f, 0), Vector3.one * 4f, whiteMat);

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