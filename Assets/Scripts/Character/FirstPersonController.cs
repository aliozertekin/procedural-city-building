using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : MonoBehaviour
{
    [Header("Movement")]
    public float walkSpeed = 6f;
    public float runMultiplier = 2f;
    public float jumpForce = 18f;
    public float gravity = -25f;

    [Header("Mouse Look")]
    public float mouseSensitivity = 2.5f;
    public Transform cameraPivot;

    [Header("Animation")]
    public Animator animator;

    [Header("Respawn")]
    [Tooltip("Y position below which the player is teleported back to safe ground.")]
    public float fallThreshold = -30f;
    [Tooltip("Extra height above terrain surface when respawning.")]
    public float respawnHeightOffset = 0.5f;

    private CharacterController controller;
    private float yVelocity;
    private float xRotation;

    // Last known safe position (updated while grounded)
    private Vector3 lastSafePosition;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        controller.height = 3f;
        controller.center = new Vector3(0f, 0.3f, 0f);
        controller.minMoveDistance = 0f;
    }

    void OnEnable()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
        yVelocity = 0f;
    }

    void OnDisable()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void Start()
    {
        lastSafePosition = transform.position;
    }

    void Update()
    {
        // ── Fall / out-of-world detection ─────────────────
        if (transform.position.y < fallThreshold)
        {
            RespawnPlayer();
            return;
        }

        HandleMouseLook();
        HandleMovement();

        // Record safe position while grounded
        if (controller.isGrounded)
            lastSafePosition = transform.position;
    }

    // ── Respawn ───────────────────────────────────────────
    void RespawnPlayer()
    {
        // Try to snap the safe position to the terrain surface below it
        Vector3 spawnPos = lastSafePosition;

        if (Physics.Raycast(spawnPos + Vector3.up * 50f, Vector3.down, out RaycastHit hit, 200f))
            spawnPos.y = hit.point.y + respawnHeightOffset;
        else
            spawnPos.y = lastSafePosition.y + respawnHeightOffset;

        // Teleport: disable controller first to prevent physics fights
        controller.enabled = false;
        transform.position = spawnPos;
        controller.enabled = true;

        yVelocity = 0f;
    }

    // ── Called by CarControl when the player exits the car ──
    public void ResetVerticalVelocity()
    {
        yVelocity = 0f;
    }

    // ── Update safe position from external code (e.g. CarControl on enter) ──
    public void SetSafePosition(Vector3 pos)
    {
        lastSafePosition = pos;
    }

    void HandleMouseLook()
    {
        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity * 100f * Time.deltaTime;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity * 100f * Time.deltaTime;

        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -85f, 85f);

        cameraPivot.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        transform.Rotate(Vector3.up * mouseX);
    }

    void HandleMovement()
    {
        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        bool isRunning = Input.GetKey(KeyCode.LeftShift);
        float speedPercent = new Vector2(h, v).magnitude;
        float currentSpeed = walkSpeed * (isRunning ? runMultiplier : 1f);

        Vector3 move = (transform.right * h + transform.forward * v) * currentSpeed;

        bool grounded = controller.isGrounded;

        if (animator != null)
        {
            animator.SetFloat("Speed", speedPercent, 0.2f, Time.deltaTime);
            animator.SetBool("IsRunning", isRunning && speedPercent > 0.1f);
            animator.SetBool("IsGrounded", grounded);
        }

        if (grounded)
        {
            if (yVelocity < 0f) yVelocity = -2f;
            if (Input.GetKeyDown(KeyCode.Space))
            {
                yVelocity = jumpForce;
                if (animator != null) animator.SetTrigger("Jump");
            }
        }

        yVelocity += gravity * Time.deltaTime;

        Vector3 velocity = move;
        velocity.y = yVelocity;
        controller.Move(velocity * Time.deltaTime);
    }
}