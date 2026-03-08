using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────
//  DATA
// ─────────────────────────────────────────────

public enum QuestStepType { GoTo, PickUp, Deliver }
public enum QuestStatus { Available, Active, Completed, Failed }

[Serializable]
public class QuestStep
{
    public QuestStepType type;
    public string description;   // shown in UI
    public Vector3 worldPosition; // marker position
    public float radius = 4f;   // how close player must get
    [HideInInspector] public bool done;
}

[Serializable]
public class Quest
{
    public string id;
    public string title;
    public string description;
    public int rewardPoints = 100;
    public List<QuestStep> steps = new List<QuestStep>();
    [HideInInspector] public QuestStatus status = QuestStatus.Available;
    [HideInInspector] public int currentStep = 0;
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

    [Header("Settings")]
    public float checkInterval = 0.3f;   // seconds between proximity checks

    // Events the UI listens to
    public event Action<Quest> OnQuestAccepted;
    public event Action<Quest, int> OnStepCompleted;   // quest, step index
    public event Action<Quest> OnQuestCompleted;
    public event Action OnQuestsChanged;

    public int totalPoints { get; private set; }

    private List<Quest> allQuests = new List<Quest>();
    private Quest activeQuest;

    // ── world markers ──
    private GameObject markerParent;
    private GameObject currentMarker;

    // ── built-in quest templates ──
    static readonly string[] deliveryItems =
        { "Package", "Documents", "Medicine", "Food Crate", "Spare Parts" };

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

        // Subscribe to OnCityGenerated so quests always use real road positions.
        // If the city is already generated (e.g. generated before QuestSystem.Start),
        // we fall through and call GenerateQuests() directly after a frame delay.
        if (cityGenerator != null)
            cityGenerator.OnCityGenerated += OnCityReady;

        StartCoroutine(DelayedGenerate());
        StartCoroutine(ProximityLoop());
    }

    void OnDestroy()
    {
        if (cityGenerator != null)
            cityGenerator.OnCityGenerated -= OnCityReady;
    }

    void OnCityReady()
    {
        // City just finished generating — rebuild quests from fresh road data
        GenerateQuests();
    }

    System.Collections.IEnumerator DelayedGenerate()
    {
        yield return null; // wait one frame for city to generate if generateOnStart=true
        // Only generate if we don't already have quests from OnCityReady
        if (allQuests.Count == 0)
            GenerateQuests();
    }

    // ─────────────────────────────────────────
    //  QUEST GENERATION
    // ─────────────────────────────────────────

    void GenerateQuests()
    {
        allQuests.Clear();

        var segs = cityGenerator?.GetRoadSegments();
        List<Vector3> nodes = new List<Vector3>();

        if (segs != null && segs.Count > 0)
        {
            // collect unique road nodes
            HashSet<string> seen = new HashSet<string>();
            foreach (var s in segs)
            {
                string ka = $"{s.start.x:F0},{s.start.z:F0}";
                string kb = $"{s.end.x:F0},{s.end.z:F0}";
                if (seen.Add(ka)) nodes.Add(s.start);
                if (seen.Add(kb)) nodes.Add(s.end);
            }
        }

        // fallback: scatter points around world origin if no road data
        if (nodes.Count < 6)
        {
            for (int i = 0; i < 20; i++)
                nodes.Add(new Vector3(
                    UnityEngine.Random.Range(-200f, 200f), 0,
                    UnityEngine.Random.Range(-200f, 200f)));
        }

        var rng = new System.Random(42);
        string[] items = deliveryItems;

        for (int q = 0; q < 6; q++)
        {
            string item = items[rng.Next(items.Length)];

            // pick 3 distinct positions
            Vector3 pickupPos = nodes[rng.Next(nodes.Count)];
            Vector3 deliverPos = nodes[rng.Next(nodes.Count)];
            while (Vector3.Distance(pickupPos, deliverPos) < 30f)
                deliverPos = nodes[rng.Next(nodes.Count)];

            Vector3 startPos = nodes[rng.Next(nodes.Count)];
            while (Vector3.Distance(startPos, pickupPos) < 20f)
                startPos = nodes[rng.Next(nodes.Count)];

            var quest = new Quest
            {
                id = $"quest_{q}",
                title = $"Deliver {item}",
                description = $"Pick up the {item} and deliver it to the destination.",
                rewardPoints = 50 + q * 25,
                steps = new List<QuestStep>
                {
                    new QuestStep
                    {
                        type        = QuestStepType.GoTo,
                        description = $"Go to the pickup location",
                        worldPosition = startPos,
                        radius      = 5f
                    },
                    new QuestStep
                    {
                        type        = QuestStepType.PickUp,
                        description = $"Pick up the {item}",
                        worldPosition = pickupPos,
                        radius      = 5f
                    },
                    new QuestStep
                    {
                        type        = QuestStepType.Deliver,
                        description = $"Deliver the {item} to the destination",
                        worldPosition = deliverPos,
                        radius      = 5f
                    }
                }
            };

            allQuests.Add(quest);
        }

        OnQuestsChanged?.Invoke();
    }

    // ─────────────────────────────────────────
    //  PUBLIC API
    // ─────────────────────────────────────────

    public List<Quest> GetAllQuests() => allQuests;
    public Quest GetActiveQuest() => activeQuest;

    public void AcceptQuest(Quest q)
    {
        if (q.status != QuestStatus.Available) return;
        if (activeQuest != null) return; // one at a time

        activeQuest = q;
        q.status = QuestStatus.Active;
        q.currentStep = 0;

        SpawnMarker(q.steps[0].worldPosition);
        OnQuestAccepted?.Invoke(q);
        OnQuestsChanged?.Invoke();
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
    //  PROXIMITY CHECK
    // ─────────────────────────────────────────

    IEnumerator ProximityLoop()
    {
        while (true)
        {
            yield return new WaitForSeconds(checkInterval);
            if (activeQuest == null || playerTransform == null) continue;

            var step = activeQuest.steps[activeQuest.currentStep];
            if (step.done) continue;

            float dist = Vector3.Distance(
                new Vector3(playerTransform.position.x, 0, playerTransform.position.z),
                new Vector3(step.worldPosition.x, 0, step.worldPosition.z));

            if (dist <= step.radius)
                CompleteCurrentStep();
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
            SpawnMarker(q.steps[q.currentStep].worldPosition);
            OnQuestsChanged?.Invoke();
        }
        else
        {
            // all steps done
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

        // Snap Y to terrain surface using a downward raycast from high up,
        // so the marker always stands on the ground regardless of stored Y value
        float groundY = pos.y;
        if (Physics.Raycast(new Vector3(pos.x, 500f, pos.z), Vector3.down, out RaycastHit hit, 1000f))
            groundY = hit.point.y;

        currentMarker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        currentMarker.name = "QuestMarker";
        currentMarker.transform.SetParent(markerParent.transform);
        currentMarker.transform.localScale = new Vector3(1f, 8f, 1f);
        currentMarker.transform.position = new Vector3(pos.x, groundY + 4f, pos.z);

        Destroy(currentMarker.GetComponent<Collider>());

        var mr = currentMarker.GetComponent<MeshRenderer>();
        if (mr)
        {
            // Try every known shader name across BIRP / URP / HDRP.
            // Fall back to the guaranteed-present hidden/InternalErrorShader
            // which at least renders pink so the marker is visible.
            Shader sh = Shader.Find("Universal Render Pipeline/Lit")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Color")
                     ?? Shader.Find("Standard")
                     ?? Shader.Find("HDRP/Lit");

            Material mat;
            if (sh != null)
            {
                mat = new Material(sh);
            }
            else
            {
                // Absolute fallback: grab whatever shader the terrain uses
                var anyRenderer = UnityEngine.Object.FindFirstObjectByType<MeshRenderer>();
                mat = anyRenderer != null
                    ? new Material(anyRenderer.sharedMaterial.shader)
                    : new Material(Shader.Find("Hidden/InternalErrorShader"));
            }

            Color gold = new Color(1f, 0.85f, 0f);
            mat.color = gold;

            // Emission works in both BIRP and URP
            if (mat.HasProperty("_EmissionColor"))
            {
                mat.EnableKeyword("_EMISSION");
                mat.SetColor("_EmissionColor", gold * 2.5f);
            }
            // URP uses _BaseColor instead of _Color
            if (mat.HasProperty("_BaseColor"))
                mat.SetColor("_BaseColor", gold);

            mr.sharedMaterial = mat;
        }

        // pulsing handled by QuestMarkerPulse if you want, or just static
        currentMarker.AddComponent<QuestMarkerPulse>();
    }

    void DestroyMarker()
    {
        if (currentMarker != null)
            Destroy(currentMarker);
        currentMarker = null;
    }
}

// ─────────────────────────────────────────────
//  SIMPLE PULSE ANIMATION ON THE MARKER
// ─────────────────────────────────────────────

public class QuestMarkerPulse : MonoBehaviour
{
    float baseY;
    void Start() => baseY = transform.position.y;
    void Update()
    {
        float y = baseY + Mathf.Sin(Time.time * 2f) * 0.5f;
        transform.position = new Vector3(transform.position.x, y, transform.position.z);
        float s = 1f + Mathf.Sin(Time.time * 3f) * 0.08f;
        transform.localScale = new Vector3(s, 8f * s, s);
    }
}