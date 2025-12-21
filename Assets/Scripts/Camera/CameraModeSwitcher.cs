using UnityEngine;

public class CameraModeSwitcher : MonoBehaviour
{
    public Camera topDownCamera;
    public Camera fpsCamera;

    public FirstPersonController fpsController;
    public TopDownCamera topDownController;

    void Start()
    {
        SetTopDown();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            if (fpsCamera.enabled)
                SetTopDown();
            else
                SetFPS();
        }
    }

    void SetTopDown()
    {
        fpsCamera.enabled = false;
        topDownCamera.enabled = true;

        fpsController.enabled = false;
        topDownController.enabled = true;
    }

    void SetFPS()
    {
        topDownCamera.enabled = false;
        fpsCamera.enabled = true;

        topDownController.enabled = false;
        fpsController.enabled = true;
    }
}
