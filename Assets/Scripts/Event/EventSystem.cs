using UnityEngine;
using UnityEngine.EventSystems;

public class EnsureEventSystem : MonoBehaviour
{
    void Awake()
    {
        if (FindFirstObjectByType<EventSystem>() == null)
        {
            GameObject es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<StandaloneInputModule>();
        }
    }
}
