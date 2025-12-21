using UnityEngine;

[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : MonoBehaviour
{
    [Header("Movement")]
    public float moveSpeed = 6f;
    public float jumpForce = 6f;
    public float gravity = -25f;

    [Header("Mouse Look")]
    public float mouseSensitivity = 2.5f;
    public Transform cameraPivot;

    private CharacterController controller;
    private float yVelocity;
    private float xRotation;

    void Awake()
    {
        controller = GetComponent<CharacterController>();

        controller.height = 1.8f;
        controller.center = new Vector3(0f, 0.9f, 0f);
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

        Vector3 move = (transform.right * h + transform.forward * v) * moveSpeed;

        if (controller.isGrounded)
        {
            if (yVelocity < 0f)
                yVelocity = -2f;

            if (Input.GetKeyDown(KeyCode.Space))
                yVelocity = jumpForce;
        }

        yVelocity += gravity * Time.deltaTime;

        Vector3 velocity = move;
        velocity.y = yVelocity;

        controller.Move(velocity * Time.deltaTime);
    }
}
