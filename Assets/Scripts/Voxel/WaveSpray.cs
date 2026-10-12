using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Waves crashing into sea cliffs near the viewer: every few seconds a set of waves hits and throws white spray up
/// the cliff faces, as high as the cliff biome's Wave Spray. Made by VoxelWorld; spots come from the terrain
/// generator, so cliffs the player builds or digs away don't count.
/// </summary>
[RequireComponent(typeof(ParticleSystem))]
public class WaveSpray : MonoBehaviour
{
    public VoxelWorld world;

    const float Radius = 40f;
    const int MaxSpots = 48;
    const int SamplesPerFrame = 12;

    struct Spot
    {
        public Vector3 position;
        /// <summary>Flat direction from the water toward the cliff.</summary>
        public Vector3 toCliff;
        public float height;
        /// <summary>Seconds after a wave set starts that this spot is hit.</summary>
        public float delay;
        public bool hit;
    }

    readonly List<Spot> spots = new();
    readonly HashSet<Vector2Int> known = new();
    ParticleSystem particles;
    Material material;
    float waveTimer, nextWave;

    void Awake()
    {
        particles = GetComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystem.MainModule main = particles.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.gravityModifier = 1f;
        main.maxParticles = 4000;
        ParticleSystem.EmissionModule emission = particles.emission;
        emission.enabled = false;
        ParticleSystem.ShapeModule shape = particles.shape;
        shape.enabled = false;

        // Spray puffs up and thins out as it rises and falls
        ParticleSystem.SizeOverLifetimeModule size = particles.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 2.2f));
        ParticleSystem.ColorOverLifetimeModule color = particles.colorOverLifetime;
        color.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.7f, 0.5f), new GradientAlphaKey(0f, 1f) });
        color.color = fade;
        ParticleSystem.LimitVelocityOverLifetimeModule drag = particles.limitVelocityOverLifetime;
        drag.enabled = true;
        drag.drag = 0.25f;
        drag.multiplyDragByParticleSize = false;

        // Square, unlit and see-through, so the spray looks like the blocky world around it
        var renderer = GetComponent<ParticleSystemRenderer>();
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader != null)
        {
            material = new Material(shader) { name = "Wave Spray" };
            VoxelWorld.MakeTransparent(material);
            material.mainTexture = Texture2D.whiteTexture;
            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", Texture2D.whiteTexture);
            renderer.sharedMaterial = material;
        }
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        particles.Play();
        nextWave = 2f;
    }

    void OnDestroy()
    {
        if (material != null) Destroy(material);
    }

    void Update()
    {
        if (world == null || world.Generator == null || world.IsLoading) return;
        Vector3 viewer = Camera.main != null ? Camera.main.transform.position : world.transform.position;
        FindSpots(viewer);
        Crash();
    }

    /// <summary>Forgets far spots and looks at a few random columns around the viewer for new ones.</summary>
    void FindSpots(Vector3 viewer)
    {
        var flat = new Vector2(viewer.x, viewer.z);
        for (int i = spots.Count - 1; i >= 0; i--)
        {
            Vector3 p = spots[i].position;
            if ((new Vector2(p.x, p.z) - flat).sqrMagnitude <= Radius * Radius * 1.5f) continue;
            known.Remove(new Vector2Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z)));
            spots.RemoveAt(i);
        }
        if (spots.Count >= MaxSpots) return;

        TerrainGenerator gen = world.Generator;
        int sea = gen.SeaLevel;
        for (int s = 0; s < SamplesPerFrame; s++)
        {
            Vector2 offset = Random.insideUnitCircle * Radius;
            int x = Mathf.FloorToInt(viewer.x + offset.x), z = Mathf.FloorToInt(viewer.z + offset.y);
            var key = new Vector2Int(x, z);
            if (known.Contains(key) || gen.SampleColumn(x, z).height >= sea - 1) continue;

            // Water here, a tall cliff of a spraying biome within 2 blocks
            for (int dir = 0; dir < 4; dir++)
            {
                int dx = dir == 0 ? 1 : dir == 1 ? -1 : 0, dz = dir == 2 ? 1 : dir == 3 ? -1 : 0;
                VoxelColumn cliff = default;
                bool found = false;
                for (int k = 1; k <= 2 && !found; k++)
                {
                    cliff = gen.SampleColumn(x + dx * k, z + dz * k);
                    found = cliff.height >= sea + 4 && cliff.biome != null && cliff.biome.waveSpray > 0f;
                }
                if (!found) continue;
                known.Add(key);
                spots.Add(new Spot
                {
                    position = new Vector3(x + 0.5f, sea + 0.9f, z + 0.5f),
                    toCliff = new Vector3(dx, 0f, dz),
                    height = cliff.biome.waveSpray,
                    delay = Random.Range(0f, 1.2f)
                });
                break;
            }
            if (spots.Count >= MaxSpots) break;
        }
    }

    /// <summary>Wave sets roll in every few seconds, each spot getting hit a moment apart.</summary>
    void Crash()
    {
        waveTimer += Time.deltaTime;
        if (waveTimer >= nextWave)
        {
            waveTimer = 0f;
            nextWave = Random.Range(3f, 6f);
            for (int i = 0; i < spots.Count; i++)
            {
                Spot s = spots[i];
                s.hit = false;
                s.delay = Random.Range(0f, 1.2f);
                spots[i] = s;
            }
        }

        for (int i = 0; i < spots.Count; i++)
        {
            Spot s = spots[i];
            if (s.hit || waveTimer < s.delay) continue;
            s.hit = true;
            spots[i] = s;
            Burst(s);
        }
    }

    void Burst(Spot s)
    {
        float gravity = -Physics.gravity.y;
        float strength = Random.Range(0.45f, 1f);
        var emit = new ParticleSystem.EmitParams();

        // A tall plume up the cliff face, drifting back out to sea
        int plume = Mathf.RoundToInt(Mathf.Lerp(12f, 30f, strength));
        for (int i = 0; i < plume; i++)
        {
            float h = s.height * strength * Random.Range(0.4f, 1f);
            // A bit extra to make up for the drag
            float up = Mathf.Sqrt(2f * gravity * h) * 1.2f;
            Vector3 side = Vector3.Cross(s.toCliff, Vector3.up) * Random.Range(-1.5f, 1.5f);
            emit.position = s.position + s.toCliff * Random.Range(0.3f, 1.2f) + side;
            emit.velocity = Vector3.up * up - s.toCliff * Random.Range(0.5f, 2.5f) + side * 0.6f;
            emit.startLifetime = up / gravity * Random.Range(1.3f, 1.9f);
            emit.startSize = Random.Range(0.35f, 1f);
            emit.startColor = new Color(1f, 1f, 1f, Random.Range(0.6f, 0.95f));
            particles.Emit(emit, 1);
        }

        // Foam fanning out low over the water
        for (int i = 0; i < 14; i++)
        {
            Vector2 d = Random.insideUnitCircle.normalized;
            var flat = new Vector3(d.x, 0f, d.y);
            if (Vector3.Dot(flat, s.toCliff) > 0f) flat -= 2f * Vector3.Dot(flat, s.toCliff) * s.toCliff;
            emit.position = s.position + s.toCliff * 0.8f;
            emit.velocity = flat * Random.Range(1.5f, 4f) + Vector3.up * Random.Range(1.5f, 4f);
            emit.startLifetime = Random.Range(0.8f, 1.5f);
            emit.startSize = Random.Range(0.4f, 0.9f);
            emit.startColor = new Color(0.95f, 0.98f, 1f, 0.85f);
            particles.Emit(emit, 1);
        }
    }
}
