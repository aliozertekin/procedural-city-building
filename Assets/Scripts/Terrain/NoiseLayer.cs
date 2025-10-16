using UnityEngine;

[CreateAssetMenu(menuName = "ProceduralCity/NoiseLayer")]
public class NoiseLayer : ScriptableObject
{
    public bool enabled = true;
    public bool useMask = false;
    public float frequency = 0.01f;
    public float amplitude = 1f;
    public int octaves = 4;
    public float lacunarity = 2f;
    public float persistence = 0.5f;

    [HideInInspector] public float offsetX;
    [HideInInspector] public float offsetY;
}
