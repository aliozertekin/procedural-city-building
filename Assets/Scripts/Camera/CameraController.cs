using Unity.Mathematics;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

public class CameraController : MonoBehaviour
{
    // Public
    public static CameraController instance;

    public Transform followTransform;
    public Transform cameraTransform;

    public float movementSpeed;
    public float fastSpeed;
    public float movementTime;
    public float rotationAmount;
    public float rotationScale;
    public Vector3 zoomAmount;

    public Vector3 newZoom;
    public Vector3 newPosition;
    public Quaternion newRotation;

    // Private
    private Vector3 dragStartPosition;
    private Vector3 dragCurrentPosition;
    private Vector2 lastMousePosition;
    private CameraControls inputActions;
    private bool leftMouseHeld = false;
    private bool rightMouseHeld = false;

    private void Awake()
    {
        inputActions = new CameraControls();
        inputActions.Enable();
        inputActions.CameraMove.CancelFollow.performed += ctx => followTransform = null;

        inputActions.CameraMove.LeftClick.started += _ => leftMouseHeld = true;
        inputActions.CameraMove.LeftClick.canceled += _ => leftMouseHeld = false;

        inputActions.CameraMove.RightClick.started += ctx =>
        {
            rightMouseHeld = true;
            lastMousePosition = inputActions.CameraMove.MousePos.ReadValue<Vector2>();
        };
        inputActions.CameraMove.RightClick.canceled += _ => rightMouseHeld = false;
    }

    void Start()
    {
        instance = this;
        newZoom = cameraTransform.localPosition;
        newPosition = transform.position;
        newRotation = transform.rotation;
    }

    void Update()
    {
        if (followTransform != null)
            transform.position = followTransform.position;
        else
            HandleMouseInput();
    }

    private void LateUpdate()
    {
        if (followTransform != null)
            transform.position = followTransform.position;
        else
            HandleMovementInput();

    }

    private void OnDisable()
    {
        inputActions.Disable();
    }

    private void HandleMovementInput()
    {
        Vector2 input = inputActions.CameraMove.Move.ReadValue<Vector2>();
        float rotationInput = inputActions.CameraMove.Rotation.ReadValue<float>();
        float zoomInput = inputActions.CameraMove.Zoom.ReadValue<float>();
        bool speed = inputActions.CameraMove.Speed.IsPressed();

        float currentSpeed = speed ? fastSpeed : movementSpeed;

        // Movement
        Vector3 direction = new Vector3(input.x, 0, input.y);
        newPosition += (transform.right * direction.x + transform.forward * direction.z) * currentSpeed * Time.deltaTime;
        transform.position = Vector3.Lerp(transform.position, newPosition, Time.deltaTime * movementTime);

        // Rotation
        if (rotationInput != 0)
        {
            Quaternion targetRotation = Quaternion.Euler(Vector3.up * rotationAmount * rotationInput);
            newRotation = transform.rotation * targetRotation;
        }

        transform.rotation = Quaternion.Lerp(transform.rotation, newRotation, Time.deltaTime * movementTime);

        // Zoom
        if (zoomInput != 0)
        {
            newZoom += zoomAmount * zoomInput;
        }
        cameraTransform.localPosition = Vector3.Lerp(cameraTransform.localPosition, newZoom , Time.deltaTime * movementTime);
    }
    private void HandleMouseInput()
    {
        Vector2 mousePosition = inputActions.CameraMove.MousePos.ReadValue<Vector2>();

        // Left-click drag for panning
        if (leftMouseHeld)
        {
            if (dragStartPosition == Vector3.zero)
            {
                Plane plane = new Plane(Vector3.up, Vector3.zero);
                Ray ray = Camera.main.ScreenPointToRay(mousePosition);
                if (plane.Raycast(ray, out float entry))
                    dragStartPosition = ray.GetPoint(entry);
            }

            Plane movePlane = new Plane(Vector3.up, Vector3.zero);
            Ray moveRay = Camera.main.ScreenPointToRay(mousePosition);
            if (movePlane.Raycast(moveRay, out float entry2))
            {
                dragCurrentPosition = moveRay.GetPoint(entry2);
                Vector3 offset = dragStartPosition - dragCurrentPosition;
                newPosition = transform.position + offset;
            }
        }
        else
        {
            dragStartPosition = Vector3.zero;
        }

        // Right-click drag for rotation
        if (rightMouseHeld)
        {
            Vector2 delta = mousePosition - lastMousePosition;
            float rotationDelta = delta.x * rotationAmount * rotationScale;

            Quaternion targetRotation = Quaternion.Euler(Vector3.up * rotationDelta);
            newRotation = transform.rotation * targetRotation;

            lastMousePosition = mousePosition;
        }
    }
}
