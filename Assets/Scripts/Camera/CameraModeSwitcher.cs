using UnityEngine;

public class CameraModeSwitcher : MonoBehaviour
{
    [Header("Cameras")]
    public Camera topDownCamera;
    public Camera fpsCamera;

    [Header("Controllers")]
    public FirstPersonController fpsController;
    public TopDownCamera topDownController;

    [Header("UI — assign the root GameObjects in the Inspector")]
    [Tooltip("The GameObject that has CityPathfinderUI on it (top-down UI)")]
    public GameObject pathfinderUIObject;

    [Tooltip("The GameObject that has QuestUI on it (FPS UI)")]
    public GameObject questUIObject;

    [Header("Quest System")]
    public QuestSystem questSystem;

    void Awake()
    {
        // Auto-find if not assigned in Inspector
        if (pathfinderUIObject == null)
        {
            var found = FindFirstObjectByType<CityPathfinderUI>();
            if (found != null) pathfinderUIObject = found.gameObject;
        }

        if (questUIObject == null)
        {
            var found = FindFirstObjectByType<QuestUI>();
            if (found != null) questUIObject = found.gameObject;
        }

        if (questSystem == null)
            questSystem = FindFirstObjectByType<QuestSystem>();
    }

    void Start()
    {
        // Wire player transform into quest system
        if (questSystem != null && fpsController != null)
            questSystem.playerTransform = fpsController.transform;

        SetTopDown();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            if (fpsCamera.enabled) SetTopDown();
            else SetFPS();
        }
    }

    void SetTopDown()
    {
        fpsCamera.enabled = false;
        topDownCamera.enabled = true;

        fpsController.enabled = false;
        topDownController.enabled = true;

        // Show pathfinder UI, hide quest UI
        if (pathfinderUIObject != null) pathfinderUIObject.SetActive(true);
        if (questUIObject != null) questUIObject.SetActive(false);

        // Free the cursor for top-down UI interaction
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void SetFPS()
    {
        topDownCamera.enabled = false;
        fpsCamera.enabled = true;

        topDownController.enabled = false;
        fpsController.enabled = true;

        // Hide pathfinder UI, show quest UI
        if (pathfinderUIObject != null) pathfinderUIObject.SetActive(false);
        if (questUIObject != null) questUIObject.SetActive(true);

        // Also free cursor so the quest UI buttons are clickable.
        // The FPS controller should only re-lock the cursor when the
        // player clicks IN the game world (not on UI). If your
        // FirstPersonController locks the cursor in its own Update/Start,
        // comment those lines out and let this switcher own cursor state.
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }
}