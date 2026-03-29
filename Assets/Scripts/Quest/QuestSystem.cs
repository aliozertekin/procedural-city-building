using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
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
    public string description;
    public Vector3 worldPosition;
    public float radius = 4f;
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
    public float checkInterval = 0.3f;

    public event Action<Quest> OnQuestAccepted;
    public event Action<Quest, int> OnStepCompleted;
    public event Action<Quest> OnQuestCompleted;
    public event Action OnQuestsChanged;

    public int totalPoints { get; private set; }

    private List<Quest> allQuests = new List<Quest>();
    private Quest activeQuest;

    private GameObject markerParent;
    private GameObject currentMarker;

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

    void OnCityReady() => GenerateQuests();

    IEnumerator DelayedGenerate()
    {
        yield return null;
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
            HashSet<string> seen = new HashSet<string>();
            foreach (var s in segs)
            {
                string ka = $"{s.start.x:F0},{s.start.z:F0}";
                string kb = $"{s.end.x:F0},{s.end.z:F0}";
                if (seen.Add(ka)) nodes.Add(s.start);
                if (seen.Add(kb)) nodes.Add(s.end);
            }

            // Keep only nodes inside the city footprint to prevent quests
            // spawning at map edges where geometry is incomplete or missing.
            if (nodes.Count > 0 && cityGenerator != null)
            {
                Vector3 centroid = Vector3.zero;
                foreach (var n in nodes) centroid += n;
                centroid /= nodes.Count;

                float halfX = cityGenerator.citySize.x * 0.45f;
                float halfZ = cityGenerator.citySize.y * 0.45f;

                nodes = nodes.Where(n =>
                    Mathf.Abs(n.x - centroid.x) <= halfX &&
                    Mathf.Abs(n.z - centroid.z) <= halfZ
                ).ToList();
            }
        }

        if (nodes.Count < 6)
        {
            for (int i = 0; i < 20; i++)
                nodes.Add(new Vector3(
                    UnityEngine.Random.Range(-200f, 200f), 0,
                    UnityEngine.Random.Range(-200f, 200f)));
        }

        var rng = new System.Random(42);

        for (int q = 0; q < 6; q++)
        {
            string item = deliveryItems[rng.Next(deliveryItems.Length)];

            // 2-step quests (PickUp + Deliver).
            // The original 3-step design had a GoTo step whose location could
            // coincide with the player spawn, causing the marker to flash on the
            // character and immediately auto-complete. Removing it fixes that.
            Vector3 pickupPos = nodes[rng.Next(nodes.Count)];
            Vector3 deliverPos = nodes[rng.Next(nodes.Count)];

            int safety = 0;
            while (Vector3.Distance(pickupPos, deliverPos) < 50f && safety++ < 30)
                deliverPos = nodes[rng.Next(nodes.Count)];

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
                        type          = QuestStepType.PickUp,
                        description   = $"Pick up the {item}",
                        worldPosition = pickupPos,
                        radius        = 6f
                    },
                    new QuestStep
                    {
                        type          = QuestStepType.Deliver,
                        description   = $"Deliver the {item} to the destination",
                        worldPosition = deliverPos,
                        radius        = 6f
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
        if (activeQuest != null) return;

        activeQuest = q;
        q.status = QuestStatus.Active;
        q.currentStep = 0;

        // Immediately advance past any steps the player is already within radius of.
        // Without this, a step at the player's location would flash a marker on them
        // before auto-completing on the next ProximityLoop tick (up to 0.3 s later).
        AdvanceCompletedSteps();

        if (activeQuest != null)          // quest might have completed immediately
        {
            SpawnMarker(q.steps[q.currentStep].worldPosition);
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

            if (dist <= step.radius)
                CompleteCurrentStep();
            else
                break;
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
            q.status = QuestStatus.Completed;
            totalPoints += q.rewardPoints;
            activeQuest = null;
            DestroyMarker();
            OnQuestCompleted?.Invoke(q);
            OnQuestsChanged?.Invoke();
        }
    }

    // ─────────────────────────────────────────
    //  MARKER — large always-visible beacon
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

        // Base disc
        AddPrimitive(PrimitiveType.Cylinder, currentMarker.transform,
                     new Vector3(0, 0.4f, 0), new Vector3(8f, 0.4f, 8f), goldMat);
        // Pole (Unity cylinder y-scale = half-height, so 20 → 40 real units)
        AddPrimitive(PrimitiveType.Cylinder, currentMarker.transform,
                     new Vector3(0, 21f, 0), new Vector3(1.2f, 20f, 1.2f), goldMat);
        // Outer orb
        AddPrimitive(PrimitiveType.Sphere, currentMarker.transform,
                     new Vector3(0, 45f, 0), Vector3.one * 8f, goldMat);
        // Inner bright core
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
        mat.EnableKeyword("_EMISSIVE_COLOR_MAP");
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