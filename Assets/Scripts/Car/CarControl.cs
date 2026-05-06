using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class CarControl : MonoBehaviour
{
    [Header("Engine")]
    public float enginePower = 2000.0f;
    public float turnSpeed = 25.0f;
    public float turnSmoothness = 5.0f;

    [Header("Wheels")]
    public Transform[] wheels;
    public Transform[] wheelMeshes;

    [Header("References")]
    public Transform centerOfMass;
    public GameObject steeringWheel;

    [Header("Enter / Exit")]
    public GameObject playerObject;
    public CameraControl cameraControl;
    public float enterRadius = 3.5f;
    public float exitSideOffset = 2.5f;

    [Header("Respawn")]
    [Tooltip("Y position below which the car is considered off the world and will respawn.")]
    public float fallThreshold = -30f;
    [Tooltip("How high above the terrain surface to place the car when respawning.")]
    public float respawnHeightOffset = 2f;
    [Tooltip("How far from the player to try spawning the car (radius checked in 8 directions).")]
    public float respawnPlayerOffset = 6f;
    [Tooltip("Seconds to wait before respawning after a fall (0 = instant).")]
    public float respawnDelay = 1.2f;

    // ── Private ──────────────────────────────────────────
    private Rigidbody rb;
    private FirstPersonController fpsController;
    private float currentTurnAngle = 0f;
    private bool isDriving = false;
    private bool isRespawning = false;

    // ─────────────────────────────────────────────────────
    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.centerOfMass = centerOfMass.localPosition;

        if (playerObject != null)
            fpsController = playerObject.GetComponent<FirstPersonController>();
    }

    void Update()
    {
        if (isRespawning) return;

        // ── Fall detection ────────────────────────────────
        if (transform.position.y < fallThreshold)
        {
            StartCoroutine(RespawnCar());
            return;
        }

        if (!isDriving)
        {
            float dist = Vector3.Distance(transform.position, playerObject.transform.position);
            if (dist <= enterRadius && Input.GetKeyDown(KeyCode.E))
                EnterCar();
        }
        else
        {
            if (Input.GetKeyDown(KeyCode.E))
                ExitCar();

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void FixedUpdate()
    {
        if (!isDriving || isRespawning) return;

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");

        float targetTurnAngle = h * turnSpeed;
        currentTurnAngle = Mathf.Lerp(currentTurnAngle, targetTurnAngle, Time.deltaTime * turnSmoothness);

        if (steeringWheel != null)
            steeringWheel.transform.localEulerAngles = new Vector3(-64f, 0f, currentTurnAngle * 3f);

        for (int i = 0; i < wheels.Length; i++)
        {
            WheelCollider wc = wheels[i].GetComponent<WheelCollider>();
            wc.steerAngle = (i < 2) ? currentTurnAngle : 0f;
            wc.motorTorque = v * enginePower;

            wc.GetWorldPose(out Vector3 pos, out Quaternion rot);
            wheelMeshes[i].position = pos;
            wheelMeshes[i].rotation = rot;
        }
    }

    // ── Car respawn ───────────────────────────────────────
    // Respawns the car near the player's current position.
    // Walks 8 directions around the player at respawnPlayerOffset
    // radius, raycasting downward each time to find solid terrain.
    IEnumerator RespawnCar()
    {
        isRespawning = true;

        // If player was driving, eject them safely before moving the car
        if (isDriving)
        {
            isDriving = false;
            SpawnPlayerSafely();
        }

        if (respawnDelay > 0f)
            yield return new WaitForSeconds(respawnDelay);

        Vector3 spawnPos = FindTerrainSpawnNearPlayer();

        // Teleport cleanly with no residual velocity
        rb.isKinematic = true;
        transform.position = spawnPos;
        transform.rotation = Quaternion.Euler(0f, playerObject.transform.eulerAngles.y, 0f);
        rb.isKinematic = false;
        rb.linearVelocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;

        isRespawning = false;
    }

    // Tries 8 evenly-spaced directions around the player at
    // respawnPlayerOffset distance, raycasting down to find terrain.
    // Returns the best valid position, or falls back above the player.
    Vector3 FindTerrainSpawnNearPlayer()
    {
        Vector3 playerPos = playerObject.transform.position;

        for (int i = 0; i < 8; i++)
        {
            float angle = i * 45f;
            Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            Vector3 candidate = playerPos + dir * respawnPlayerOffset;

            // Cast down from well above to find any ground surface
            if (Physics.Raycast(candidate + Vector3.up * 60f, Vector3.down, out RaycastHit hit, 300f))
            {
                Vector3 result = hit.point + Vector3.up * respawnHeightOffset;
                if (result.y > fallThreshold + 5f)
                    return result;
            }
        }

        // Fallback: directly above the player
        if (Physics.Raycast(playerPos + Vector3.up * 60f, Vector3.down, out RaycastHit fbHit, 300f))
            return fbHit.point + Vector3.up * respawnHeightOffset;

        return playerPos + Vector3.up * respawnHeightOffset;
    }

    // ── Enter ─────────────────────────────────────────────
    void EnterCar()
    {
        isDriving = true;
        playerObject.SetActive(false);

        if (cameraControl != null)
            cameraControl.target = transform;

        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    // ── Exit ──────────────────────────────────────────────
    void ExitCar()
    {
        isDriving = false;
        SpawnPlayerSafely();

        if (cameraControl != null)
            cameraControl.target = playerObject.transform;
    }

    // ── Safe player exit placement ────────────────────────
    void SpawnPlayerSafely()
    {
        Vector3[] offsets = new Vector3[]
        {
            -transform.right  * exitSideOffset,
             transform.right  * exitSideOffset,
            -transform.forward * exitSideOffset,
             transform.forward * exitSideOffset,
        };

        foreach (var offset in offsets)
        {
            Vector3 candidate = transform.position + offset;

            if (Physics.Raycast(candidate + Vector3.up * 10f, Vector3.down, out RaycastHit hit, 30f))
            {
                candidate.y = hit.point.y + 0.1f;
                if (!OverlapsCarBounds(candidate))
                {
                    PlacePlayer(candidate);
                    return;
                }
            }
        }

        // Fallback: above the car
        Vector3 fallback = transform.position + Vector3.up * 3f;
        if (Physics.Raycast(fallback + Vector3.up * 20f, Vector3.down, out RaycastHit fbHit, 50f))
            fallback.y = fbHit.point.y + 0.1f;

        PlacePlayer(fallback);
    }

    bool OverlapsCarBounds(Vector3 position)
    {
        foreach (var col in GetComponentsInChildren<Collider>())
            if (col.bounds.Contains(position)) return true;
        return false;
    }

    void PlacePlayer(Vector3 position)
    {
        playerObject.transform.position = position;
        playerObject.transform.rotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        playerObject.SetActive(true);

        if (fpsController != null)
            fpsController.ResetVerticalVelocity();
    }

    // ── Proximity prompt ──────────────────────────────────
    void OnGUI()
    {
        if (isRespawning) return;

        if (isDriving)
        {
            DrawPrompt("Press [E] to exit vehicle", 0.5f, 0.08f, 1f);
            return;
        }

        if (playerObject == null) return;
        float dist = Vector3.Distance(transform.position, playerObject.transform.position);
        float alpha = Mathf.Clamp01(1f - (dist - enterRadius * 0.5f) / (enterRadius * 0.5f));
        if (alpha > 0.01f)
            DrawPrompt("Press [E] to enter vehicle", 0.5f, 0.08f, alpha);
    }

    void DrawPrompt(string text, float xNorm, float yNorm, float alpha)
    {
        GUI.skin.label.fontSize = 22;
        GUI.skin.label.alignment = TextAnchor.MiddleCenter;
        float w = 400f, h = 40f;
        float x = Screen.width * xNorm - w * 0.5f;
        float y = Screen.height * yNorm;
        GUI.color = new Color(0f, 0f, 0f, alpha * 0.6f);
        GUI.Label(new Rect(x + 2f, y + 2f, w, h), text);
        GUI.color = new Color(1f, 1f, 1f, alpha);
        GUI.Label(new Rect(x, y, w, h), text);
        GUI.color = Color.white;
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0f, 1f, 0.4f, 0.3f);
        Gizmos.DrawWireSphere(transform.position, enterRadius);
        // Show respawn search radius around player
        if (playerObject != null)
        {
            Gizmos.color = new Color(1f, 0.6f, 0f, 0.25f);
            Gizmos.DrawWireSphere(playerObject.transform.position, respawnPlayerOffset);
        }
    }
}