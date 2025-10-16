using UnityEngine;

[CreateAssetMenu(menuName = "ProceduralCity/TerrainSettings")]
public class TerrainSettings : ScriptableObject
{
    [Header("Texture Settings")]
    public float textureTileSize = 200f;

    [Header("General Settings")]
    public int terrainWidth = 512;
    public int terrainHeight = 128;
    public int terrainLength = 512;

    [Header("Noise Settings")]
    public int seed = 1234;
    public NoiseLayer[] noiseLayers;

    [Header("Post-processing")]
    public AnimationCurve heightCurve = AnimationCurve.Linear(0, 0, 1, 1);
}
