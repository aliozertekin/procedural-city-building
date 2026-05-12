using UnityEngine;

/// <summary>
/// Attach to the car root (or any child). Creates a smoke/exhaust particle
/// system at the rear exhaust pipe that scales with speed.
///
/// Setup:
///   1. Add this component to your car GameObject.
///   2. Set exhaustOffset to place the emitter at your exhaust pipe
///      (e.g. new Vector3(-0.4f, 0.15f, -2.5f) for a rear-left pipe).
///   3. Assign your Rigidbody, or leave it empty to auto-find on the
///      same GameObject.
///   4. Optionally assign an exhaustMaterial (soft-additive or alpha-blended
///      sprite, ideally a soft circular puff texture). If left empty a
///      procedural soft-circle texture is generated automatically.
/// </summary>
public class CarExhaustParticles : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Rigidbody of the car. Auto-found on this GameObject if empty.")]
    public Rigidbody carRigidbody;

    [Tooltip("Optional material for the exhaust particles (soft puff/smoke sprite).")]
    public Material exhaustMaterial;

    [Header("Emitter Placement")]
    [Tooltip("Local-space offset from this transform's origin to the exhaust tip.")]
    public Vector3 exhaustOffset = new Vector3(-0.4f, 0.15f, -2.5f);

    [Header("Exhaust Settings")]
    [Tooltip("Base emission rate at idle (engine always running).")]
    public float idleEmissionRate = 8f;

    [Tooltip("Maximum additional particles per second at full speed.")]
    public float maxExtraEmission = 40f;

    [Tooltip("Speed (m/s) at which emission reaches maximum intensity.")]
    public float maxSpeed = 25f;

    [Tooltip("How long each smoke puff lives (seconds).")]
    public float particleLifetime = 2.2f;

    [Tooltip("How large each puff grows at the end of its life.")]
    public float maxParticleSize = 2.8f;

    [Tooltip("Smoke colour at birth (dark grey exhaust).")]
    public Color smokeColorStart = new Color(0.25f, 0.25f, 0.25f, 0.55f);

    [Tooltip("Smoke colour at death (fades to near-transparent light grey).")]
    public Color smokeColorEnd = new Color(0.7f, 0.7f, 0.7f, 0f);

    // ── runtime ──
    private ParticleSystem ps;
    private ParticleSystem.EmissionModule emission;

    // =========================================================
    void Awake()
    {
        if (carRigidbody == null)
            carRigidbody = GetComponent<Rigidbody>();

        BuildParticleSystem();
    }

    // =========================================================
    void BuildParticleSystem()
    {
        // ── Child GameObject at exhaust pipe tip ──────────────
        var go = new GameObject("ExhaustEmitter");
        go.transform.SetParent(transform, false);
        go.transform.localPosition = exhaustOffset;
        // Face the emitter backward along the car's local -Z axis
        go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        ps = go.AddComponent<ParticleSystem>();

        // ── Main module ──────────────────────────────────────
        var main = ps.main;
        main.loop = true;
        main.startLifetime = new ParticleSystem.MinMaxCurve(particleLifetime * 0.7f, particleLifetime);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.4f, 1.1f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.12f, 0.28f); // tiny at birth
        main.startColor = new ParticleSystem.MinMaxGradient(smokeColorStart, smokeColorEnd);
        main.gravityModifier = -0.06f;   // buoyant — hot exhaust rises
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 200;
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, 360f * Mathf.Deg2Rad);

        // ── Emission — idle base + speed bonus ───────────────
        emission = ps.emission;
        emission.enabled = true;
        emission.rateOverTime = idleEmissionRate;

        // ── Shape — tight circle at the pipe mouth ───────────
        // Particles emerge from a small disc, straight back, with
        // very little spread so they look like they come from a pipe.
        var shape = ps.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = 0.04f;   // roughly the size of an exhaust pipe mouth
        shape.radiusThickness = 1f; // emit from the full disc, not just the edge
        shape.arc = 360f;

        // ── Size over lifetime — small → large dissipating puff ──
        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        var sizeCurve = new AnimationCurve();
        sizeCurve.AddKey(0.00f, 0.05f);   // tiny as it exits the pipe
        sizeCurve.AddKey(0.15f, 0.40f);   // quickly expands
        sizeCurve.AddKey(0.60f, 0.85f);
        sizeCurve.AddKey(1.00f, maxParticleSize);
        sol.size = new ParticleSystem.MinMaxCurve(1f, sizeCurve);

        // ── Colour/Alpha over lifetime — dark smoke → transparent ──
        var col = ps.colorOverLifetime;
        col.enabled = true;
        var grad = new Gradient();
        grad.SetKeys(
            new[]
            {
                new GradientColorKey(smokeColorStart,                                  0.00f),
                new GradientColorKey(Color.Lerp(smokeColorStart, smokeColorEnd, 0.4f), 0.40f),
                new GradientColorKey(smokeColorEnd,                                    1.00f)
            },
            new[]
            {
                new GradientAlphaKey(smokeColorStart.a,        0.00f),
                new GradientAlphaKey(smokeColorStart.a * 0.6f, 0.30f),
                new GradientAlphaKey(0f,                       1.00f)
            });
        col.color = new ParticleSystem.MinMaxGradient(grad);

        // ── Rotation over lifetime — slow lazy spin ───────────
        var rot = ps.rotationOverLifetime;
        rot.enabled = true;
        rot.z = new ParticleSystem.MinMaxCurve(-25f * Mathf.Deg2Rad, 25f * Mathf.Deg2Rad);

        // ── Velocity over lifetime — drift backward + slight lateral waver ──
        var vel = ps.velocityOverLifetime;
        vel.enabled = true;
        // After leaving the pipe the puff drifts with the wind (backward) and
        // wobbles slightly side-to-side, simulating turbulence.
        vel.x = new ParticleSystem.MinMaxCurve(-0.15f, 0.15f);
        vel.y = new ParticleSystem.MinMaxCurve(0.1f, 0.35f);  // natural rise
        vel.z = new ParticleSystem.MinMaxCurve(-0.3f, 0.0f);   // backward drift in world
        vel.space = ParticleSystemSimulationSpace.World;

        // ── Noise — organic billowing ─────────────────────────
        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = 0.25f;
        noise.frequency = 0.4f;
        noise.scrollSpeed = 0.1f;
        noise.damping = true;
        noise.quality = ParticleSystemNoiseQuality.Medium;

        // ── Renderer ─────────────────────────────────────────
        var rend = go.GetComponent<ParticleSystemRenderer>();
        rend.renderMode = ParticleSystemRenderMode.Billboard;
        rend.minParticleSize = 0f;
        rend.maxParticleSize = 1.0f;
        rend.sortingFudge = 10f;

        if (exhaustMaterial != null)
        {
            rend.material = exhaustMaterial;
        }
        else
        {
            // Generate a procedural soft radial gradient texture so particles
            // look like rounded smoke puffs, not hard squares.
            rend.material = BuildSoftCircleMaterial();
        }

        ps.Play();
    }

    // =========================================================
    /// <summary>
    /// Generates a 64×64 soft radial gradient texture and returns
    /// an alpha-blended particle material using it.
    /// This removes the "billboard square" look entirely.
    /// </summary>
    private Material BuildSoftCircleMaterial()
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.ARGB32, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;

        float centre = size * 0.5f;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f),
                                              new Vector2(centre, centre));
                // Normalise so 0 = centre, 1 = edge
                float t = Mathf.Clamp01(dist / centre);
                // Smooth falloff: bright core fading softly to transparent edge
                float alpha = Mathf.Pow(1f - t, 2.2f);
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        tex.Apply();

        // Try URP/HDRP first, then Built-in fallbacks
        Shader sh = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                 ?? Shader.Find("Particles/Standard Unlit")
                 ?? Shader.Find("Particles/Alpha Blended")
                 ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended")
                 ?? Shader.Find("Sprites/Default")
                 ?? Shader.Find("Unlit/Transparent");

        var mat = sh != null ? new Material(sh) : new Material(Shader.Find("Unlit/Color"));

        // Assign texture
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", tex);   // URP
        if (mat.HasProperty("_MainTex")) mat.SetTexture("_MainTex", tex);   // Built-in
        if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.white);

        // Enable transparency
        if (mat.HasProperty("_Mode")) { mat.SetFloat("_Mode", 2f); }  // Fade (Built-in)
        if (mat.HasProperty("_Surface")) { mat.SetFloat("_Surface", 1f); } // Transparent (URP)
        if (mat.HasProperty("_Blend")) { mat.SetFloat("_Blend", 0f); }

        mat.EnableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.renderQueue = 3000;

        return mat;
    }

    // =========================================================
    void Update()
    {
        if (ps == null) return;

        float speed = carRigidbody != null
            ? carRigidbody.linearVelocity.magnitude
            : 0f;

        float t = Mathf.InverseLerp(0f, maxSpeed, speed);
        float rate = idleEmissionRate + Mathf.Lerp(0f, maxExtraEmission, t);
        emission.rateOverTime = rate;

        // Exhaust exits the pipe faster when the engine is working harder
        float exitSpeed = Mathf.Lerp(0.4f, 2.5f, t);
        var m = ps.main;
        m.startSpeed = new ParticleSystem.MinMaxCurve(exitSpeed * 0.35f, exitSpeed);
    }
}