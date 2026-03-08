using UnityEngine;

[System.Serializable]
public class NPCPrefabEntry
{
    [Tooltip("The NPC prefab to spawn")]
    public GameObject prefab;

    [Tooltip("Relative spawn weight. Higher = appears more often.")]
    [Range(1, 100)]
    public int weight = 10;
}