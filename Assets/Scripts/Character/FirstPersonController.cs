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

    private CharacterController controller;
    private float yVelocity;
    private float xRotation;

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
    }

    void OnDisable()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void Update()
    {
        HandleMouseLook();
        HandleMovement();
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

        // ---------- ANIMATION ----------
        if (animator != null)
        {
            animator.SetFloat("Speed", speedPercent, 0.2f, Time.deltaTime);
            animator.SetBool("IsRunning", isRunning && speedPercent > 0.1f);
            animator.SetBool("IsGrounded", grounded);
        }
        // -------------------------------

        if (grounded)
        {
            if (yVelocity < 0f)
                yVelocity = -2f;

            if (Input.GetKeyDown(KeyCode.Space))
            {
                yVelocity = jumpForce;

                if (animator != null)
                    animator.SetTrigger("Jump");
            }
        }

        yVelocity += gravity * Time.deltaTime;

        Vector3 velocity = move;
        velocity.y = yVelocity;

        controller.Move(velocity * Time.deltaTime);
    }
}
