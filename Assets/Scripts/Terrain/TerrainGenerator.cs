using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteInEditMode]
public class TerrainGenerator : MonoBehaviour
{
    public TerrainSettings settings;
    private Terrain terrain;

    void OnValidate()
    {
        terrain = GetComponent<Terrain>();
    }

    [ContextMenu("Generate Terrain")]
    public void Generate()
    {
        if (settings == null)
        {
            Debug.LogWarning("Missing TerrainSettings reference!");
            return;
        }

        if (terrain == null)
        {
            terrain = GetComponent<Terrain>();
            if (terrain == null)
            {
                Debug.LogError("No Terrain component found on this GameObject!");
                return;
            }
        }

        TerrainData data = terrain.terrainData;
        int res = data.heightmapResolution;
        data.size = new Vector3(settings.terrainWidth, settings.terrainHeight, settings.terrainLength);

        float[,] heights = new float[res, res];

        // Seed-based offsets for deterministic variation
        System.Random prng = new System.Random(settings.seed);
        foreach (var layer in settings.noiseLayers)
        {
            if (layer == null) continue;
            layer.offsetX = (float)prng.NextDouble() * 10000f;
            layer.offsetY = (float)prng.NextDouble() * 10000f;
        }

        // Precalculate normalization
        float normalization = 0f;
        foreach (var l in settings.noiseLayers)
        {
            if (l != null && l.enabled)
                normalization += l.amplitude;
        }
        if (normalization <= 0f) normalization = 1f;

        // Generate terrain
        for (int y = 0; y < res; y++)
        {
            for (int x = 0; x < res; x++)
            {
                float nx = (float)x / res;
                float ny = (float)y / res;
                float total = 0f;

                foreach (var layer in settings.noiseLayers)
                {
                    if (layer == null || !layer.enabled) continue;
                    total += GenerateNoise(nx, ny, layer);
                }

                float h = total / normalization;
                h = settings.heightCurve.Evaluate(h);
                heights[y, x] = Mathf.Clamp01(h);
            }
        }

        data.SetHeights(0, 0, heights);
        Debug.Log($"Terrain generated with seed {settings.seed} at resolution {res}");

        //Update texture tiling properly after setting heights
        if (data.terrainLayers != null && data.terrainLayers.Length > 0)
        {
            for (int i = 0; i < data.terrainLayers.Length; i++)
            {
                TerrainLayer layer = data.terrainLayers[i];
                if (layer == null) continue;

                // Clone the layer (so Unity registers the change)
                TerrainLayer newLayer = Object.Instantiate(layer);
                newLayer.tileSize = new Vector2(settings.textureTileSize, settings.textureTileSize);

                data.terrainLayers[i] = newLayer;
            }

            // Force Unity to refresh the terrain material
            terrain.Flush();
        }


    }

    private float GenerateNoise(float x, float y, NoiseLayer layer)
    {
        float noise = 0f;
        float frequency = layer.frequency;
        float amplitude = layer.amplitude;

        for (int i = 0; i < layer.octaves; i++)
        {
            // Apply seed-based offset
            float sampleX = x * frequency + layer.offsetX;
            float sampleY = y * frequency + layer.offsetY;

            float n = Mathf.PerlinNoise(sampleX, sampleY);
            noise += n * amplitude;
            frequency *= layer.lacunarity;
            amplitude *= layer.persistence;
        }

        return noise;
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(TerrainGenerator))]
public class TerrainGeneratorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        TerrainGenerator gen = (TerrainGenerator)target;
        if (GUILayout.Button("Generate Terrain"))
        {
            gen.Generate();
        }
    }
}
#endif
