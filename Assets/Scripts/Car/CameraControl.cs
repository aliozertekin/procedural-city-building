using UnityEngine;

public class CameraControl : MonoBehaviour
{
    public Transform target;          // switched at runtime between player and car
    public float smoothness = 5f;
    public float rotationSpeed = 2f;
    public float distance = 5f;
    public float height = 2f;

    private Vector3 offset;

    void Start()
    {
        offset = new Vector3(0f, height, -distance);
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void FixedUpdate()
    {
        if (target == null) return;

        // Smoothly move toward the target
        Vector3 desiredPos = target.position + offset;
        transform.position = Vector3.Lerp(transform.position, desiredPos, smoothness * Time.deltaTime);
        transform.LookAt(target.position);

        // Orbit left / right with mouse
        float mouseX = Input.GetAxis("Mouse X") * rotationSpeed;
        offset = Quaternion.Euler(0f, mouseX, 0f) * offset;
    }

    // Called by CarControl when entering / exiting the car
    public void SetTarget(Transform newTarget)
    {
        target = newTarget;

        // Snap offset direction to behind the new target immediately
        // so the camera doesn't swing from the old position
        if (newTarget != null)
            offset = Quaternion.Euler(0f, newTarget.eulerAngles.y, 0f)
                     * new Vector3(0f, height, -distance);
    }
}