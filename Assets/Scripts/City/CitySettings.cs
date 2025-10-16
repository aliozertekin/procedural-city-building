using UnityEngine;

[CreateAssetMenu(menuName = "ProceduralCity/CitySettings")]
public class CitySettings : ScriptableObject
{
    public enum GrowthMode { Grid, Radial, Organic }

    [Header("General Settings")]
    public GrowthMode growthMode = GrowthMode.Grid;
    public int seed = 12345;
    public int roadCount = 200;
    public float roadLength = 30f;
    public float minSlope = 10f;

    [Header("Population Settings")]
    [Range(0f, 1f)] public float densityFalloff = 0.3f;
    public float densityScale = 0.5f;

    [Header("Prefabs")]
    public GameObject roadPrefab;
    public GameObject buildingPrefab;
}
