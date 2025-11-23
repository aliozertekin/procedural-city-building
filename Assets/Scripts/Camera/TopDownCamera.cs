using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[ExecuteAlways]
[RequireComponent(typeof(Camera))]
public class TopDownCamera : MonoBehaviour
{
    [Header("Target Follow")]
    public Transform target;
    public Vector3 targetOffset = new Vector3(0, 20, -20);
    public bool followTarget = false;

    [Header("Movement")]
    public float panSpeed = 150f;
    public float edgePanBorder = 20f;
    public bool useEdgePan = true;
    public bool clampToTerrain = true;

    [Header("Zoom (Perspective)")]
    public float zoomSpeed = 100f;
    public float minHeight = 10f;
    public float maxHeight = 80f;

    [Header("Rotation")]
    public bool allowRotation = true;
    public float rotationSpeed = 90f;
    [Range(30f, 85f)]
    public float pitch = 60f;

    [Header("Smoothing")]
    public bool smoothFollow = true;
    public float smoothTime = 0.15f;
    private Vector3 velocity = Vector3.zero;

    private Camera cam;
    private Terrain terrain;

    void Awake()
    {
        cam = GetComponent<Camera>();
        cam.orthographic = false;
        cam.farClipPlane = 5000f; // <-- increase far clip
        terrain = FindFirstObjectByType<Terrain>();
        CenterOnTerrain();
    }


    void Update()
    {
        HandleRotation();
        HandleZoom();
    }

    void LateUpdate()
    {
        Vector3 targetPos = transform.position;

        // Follow target
        if (followTarget && target != null)
        {
            Vector3 rotatedOffset = Quaternion.Euler(0, transform.eulerAngles.y, 0) * targetOffset;
            targetPos = target.position + rotatedOffset;
        }

        // Manual movement
        Vector2 panInput = GetPanInput();
        if (panInput.sqrMagnitude > 0.0001f)
        {
            Vector3 right = transform.right; right.y = 0; right.Normalize();
            Vector3 forward = transform.forward; forward.y = 0; forward.Normalize();
            Vector3 move = (right * panInput.x + forward * panInput.y) * panSpeed * Time.deltaTime;
            targetPos += move;
        }

        if (smoothFollow)
            transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref velocity, smoothTime);
        else
            transform.position = targetPos;

        if (clampToTerrain && terrain != null)
            ClampToTerrain();

        Vector3 euler = transform.eulerAngles;
        transform.rotation = Quaternion.Euler(pitch, euler.y, 0f);
    }
    Vector2 GetPanInput()
    {
        Vector2 input = Vector2.zero;

#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.wKey.isPressed) input.y += 1;
            if (keyboard.sKey.isPressed) input.y -= 1;
            if (keyboard.aKey.isPressed) input.x -= 1;
            if (keyboard.dKey.isPressed) input.x += 1;
        }
#else
        input.x += Input.GetAxisRaw("Horizontal");
        input.y += Input.GetAxisRaw("Vertical");
#endif

#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (useEdgePan && mouse != null)
        {
            Vector2 mPos = mouse.position.ReadValue();
            if (mPos.x >= Screen.width - edgePanBorder) input.x += 1;
            else if (mPos.x <= edgePanBorder) input.x -= 1;

            if (mPos.y >= Screen.height - edgePanBorder) input.y += 1;
            else if (mPos.y <= edgePanBorder) input.y -= 1;
        }
#else
        if (useEdgePan)
        {
            Vector2 m = Input.mousePosition;
            if (m.x >= Screen.width - edgePanBorder) input.x += 1;
            else if (m.x <= edgePanBorder) input.x -= 1;
            if (m.y >= Screen.height - edgePanBorder) input.y += 1;
            else if (m.y <= edgePanBorder) input.y -= 1;
        }
#endif

        if (input.magnitude > 1f) input.Normalize();
        return input;
    }

    void HandleZoom()
    {
#if ENABLE_INPUT_SYSTEM
        var mouse = Mouse.current;
        if (mouse == null) return;
        float scroll = mouse.scroll.ReadValue().y;
#else
        float scroll = Input.GetAxis("Mouse ScrollWheel") * 100f;
#endif
        if (Mathf.Abs(scroll) < 1e-4f) return;

        Vector3 pos = transform.position;
        pos.y -= scroll * zoomSpeed * Time.deltaTime;
        pos.y = Mathf.Clamp(pos.y, minHeight, maxHeight);
        transform.position = pos;
    }

    void HandleRotation()
    {
        if (!allowRotation) return;

        float rot = 0f;

#if ENABLE_INPUT_SYSTEM
        var keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (keyboard.qKey.isPressed) rot -= 1;
            if (keyboard.eKey.isPressed) rot += 1;
        }
#else
        if (Input.GetKey(KeyCode.Q)) rot -= 1;
        if (Input.GetKey(KeyCode.E)) rot += 1;
#endif

        if (Mathf.Abs(rot) > 1e-4f)
            transform.Rotate(Vector3.up, rot * rotationSpeed * Time.deltaTime, Space.World);
    }

    void CenterOnTerrain()
    {
        if (terrain == null) return;

        Vector3 center = terrain.transform.position + terrain.terrainData.size / 2f;
        transform.position = center + new Vector3(0, Mathf.Lerp(minHeight, maxHeight, 0.5f), -terrain.terrainData.size.z / 3f);
        transform.rotation = Quaternion.Euler(pitch, 0, 0);
    }

    void ClampToTerrain()
    {
        if (terrain == null) return;

        TerrainData t = terrain.terrainData;
        Vector3 pos = transform.position;
        Vector3 size = t.size;
        Vector3 origin = terrain.transform.position;

        pos.x = Mathf.Clamp(pos.x, origin.x, origin.x + size.x);
        pos.z = Mathf.Clamp(pos.z, origin.z, origin.z + size.z);
        transform.position = pos;
    }
}
