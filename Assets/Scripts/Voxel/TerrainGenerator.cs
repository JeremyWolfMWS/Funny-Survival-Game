using System.Collections.Generic;
using UnityEngine;

public struct VoxelColumn
{
    public int height;
    public BiomeConfig biome;
    public int strataShift;
    public int seaLevel;
    /// <summary>The biomes' Caves setting blended across borders, so tunnels fade out instead of ending in a wall.</summary>
    public float caves;
    /// <summary>Grass tint (see BlockDatabase.GetUVRect). Picked per column near borders, which blends them in a speckle.</summary>
    public byte tint;

    public byte GetBlock(int y) => biome.GetBlock(y, height, strataShift, seaLevel);
}

/// <summary>Everything about caves that is the same for a whole column, worked out once per column.</summary>
public struct CaveColumn
{
    public int surface;
    public int seaLevel;
    /// <summary>Tunnel radius in noise units, 0 = no tunnels here.</summary>
    public float tunnelWidth;
    /// <summary>0-1, how much tunnels may break through the surface here.</summary>
    public float entrance;
    public float cavernSize;
}

/// <summary>
/// Deterministic terrain sampling. Everything chunk generation uses is safe to call from several threads at once;
/// GetColumn (and GetBiome) are main thread only.
/// </summary>
public class TerrainGenerator
{
    readonly WorldGenConfig config;
    // Climate biomes compete by climate, region biomes are laid over them in patches
    readonly BiomeConfig[] biomes, regions;
    readonly BiomeConfig forceBiome;
    [System.ThreadStatic] static float[] distances, weights;
    readonly List<Color> tints = new();
    readonly Vector2 heightOffset, plateauOffset, strataOffset, temperatureOffset, humidityOffset;
    readonly Vector2 slopeOffset, mountainOffset, continentOffset, regionOffset, coastOffset;
    readonly uint seedHash;
    readonly List<Ore> ores = new();
    // Furthest inland any coast biome or cliff reaches
    readonly float maxCoastReach;
    // How the climate, region and continent noise is spread, so biomes, rarity and ocean coverage get their share
    static readonly VoxelNoise.Spread climateSpread = new(3), regionSpread = new(2), continentSpread = new(4);

    struct Ore
    {
        public byte blockId, replacesId;
        public int minY, maxY, veinSize;
        public float frequency, density;
        /// <summary>Null = every biome.</summary>
        public BiomeConfig[] biomes;
    }

    /// <summary>How far (in columns) TryGetSlope looks around the column it decides for.</summary>
    public const int SlopeReach = 3;

    /// <summary>Most slope smoothing passes. Each pass makes a column depend on raw columns 1 block further away.</summary>
    public const int MaxSlopeSmoothing = 8;

    /// <summary>Furthest leaves and limbs can reach from a trunk.</summary>
    public const int TreeRadius = 7;

    /// <summary>How far around a chunk's padded area columns are needed: slopes, plus trees and the ground around them.</summary>
    public const int ColumnReach = SlopeReach > TreeRadius + 2 ? SlopeReach : TreeRadius + 2;

    // Slopes are switched on/off in patches this size (~1 / scale blocks) so straight edges stay uniform
    const float SlopeNoiseScale = 0.07f;

    public int SeaLevel => config.seaLevel;

    /// <summary>The biomes' grass colors, for BlockDatabase.BuildAtlas. Biome.tintIndex i + 1 is Tints[i].</summary>
    public IReadOnlyList<Color> Tints => tints;

    /// <summary>Every biome that can show up: climate, region and coast biomes.</summary>
    public IReadOnlyList<BiomeConfig> AllBiomes => allBiomes;
    readonly List<BiomeConfig> allBiomes;

    /// <summary>Slope smoothing passes, which is also how far smoothing reaches (see SmoothSlopes).</summary>
    public int SmoothingReach => Mathf.Clamp(config.slopeSmoothing, 0, MaxSlopeSmoothing);

    readonly Dictionary<long, VoxelColumn> smoothedCache = new();
    VoxelColumn[] smoothingScratch;

    /// <param name="blocks">The world's blocks, including generated ore blocks (usually not config.blockDatabase).</param>
    public TerrainGenerator(WorldGenConfig config, BlockDatabase blocks, int seed)
    {
        this.config = config;

        // Every biome that can show up: the listed ones, the forced one and the coasts they reference
        var all = new List<BiomeConfig>();
        void Collect(BiomeConfig b)
        {
            if (b == null || all.Contains(b)) return;
            all.Add(b);
            if (b.coasts == null) return;
            foreach (CoastType coast in b.coasts)
                if (coast != null) Collect(coast.biome);
        }
        // The forced biome goes first, so its grass color is what icons and dropped blocks show
        Collect(config.forceBiome);
        foreach (BiomeConfig b in config.biomes) Collect(b);
        allBiomes = all;

        var climate = new List<BiomeConfig>();
        var region = new List<BiomeConfig>();
        foreach (BiomeConfig b in config.biomes)
        {
            if (b == null) continue;
            if (b.placement == BiomePlacement.Climate && !climate.Contains(b)) climate.Add(b);
            else if (b.placement == BiomePlacement.Region && !region.Contains(b)) region.Add(b);
        }
        if (climate.Count == 0 && config.forceBiome == null)
            Debug.LogError("WorldGenConfig has no biomes with Placement = Climate.", config);
        biomes = climate.ToArray();
        regions = region.ToArray();
        // Plain reference, Unity's null check isn't for worker threads
        forceBiome = config.forceBiome != null ? config.forceBiome : null;

        foreach (BiomeConfig b in all) b.ResolveBlocks(blocks);

        foreach (BiomeConfig b in all)
        {
            if (b.coasts == null) continue;
            foreach (CoastType c in b.coasts)
                if (c != null && c.biome != null)
                    maxCoastReach = Mathf.Max(maxCoastReach, c.biome.coastWidth * 1.3f + (c.biome.cliffHeight > 0f ? CliffFade : 0f));
        }

        foreach (BiomeConfig b in all)
        {
            int i = tints.IndexOf(b.grassColor);
            if (!b.tintGrass) b.tintIndex = 0;
            else if (i >= 0) b.tintIndex = (byte)(i + 1);
            else if (tints.Count < 64)
            {
                tints.Add(b.grassColor);
                b.tintIndex = (byte)tints.Count;
            }
        }

        if (config.oreManager != null)
            foreach (OreDefinition ore in config.oreManager.ores)
            {
                if (ore == null || string.IsNullOrWhiteSpace(ore.name)) continue;
                byte blockId = blocks.GetId(ore.BlockName, config.oreManager);
                byte replacesId = blocks.GetId(ore.replaces, config.oreManager);
                if (blockId == 0 || replacesId == 0) continue;

                BiomeConfig[] oreBiomes = null;
                if (ore.biomes != null && ore.biomes.Length > 0)
                {
                    var found = all.FindAll(b => System.Array.Exists(ore.biomes, n =>
                        string.Equals(n?.Trim(), b.name, System.StringComparison.OrdinalIgnoreCase)));
                    if (found.Count == 0)
                        Debug.LogWarning($"Ore '{ore.name}' only spawns in biomes '{string.Join(", ", ore.biomes)}', " +
                                         "none of which are in the world.", config.oreManager);
                    oreBiomes = found.ToArray();
                }
                ores.Add(new Ore
                {
                    blockId = blockId, replacesId = replacesId, minY = ore.minY, maxY = ore.maxY,
                    veinSize = Mathf.Max(1, ore.veinSize), frequency = ore.frequency, density = ore.density,
                    biomes = oreBiomes
                });
            }

        var rng = new System.Random(seed);
        heightOffset = RandomOffset(rng);
        plateauOffset = RandomOffset(rng);
        strataOffset = RandomOffset(rng);
        temperatureOffset = RandomOffset(rng);
        humidityOffset = RandomOffset(rng);
        slopeOffset = RandomOffset(rng);
        seedHash = (uint)rng.Next();
        caveOffsetA = new Vector3(rng.Next(-10000, 10000), rng.Next(-10000, 10000), rng.Next(-10000, 10000)) * 0.01f;
        caveOffsetB = new Vector3(rng.Next(-10000, 10000), rng.Next(-10000, 10000), rng.Next(-10000, 10000)) * 0.01f;
        // New offsets go last so existing seeds keep their terrain
        mountainOffset = RandomOffset(rng);
        cavernOffset = new Vector3(rng.Next(-10000, 10000), rng.Next(-10000, 10000), rng.Next(-10000, 10000)) * 0.01f;
        caveRegionOffset = RandomOffset(rng);
        caveEntranceOffset = RandomOffset(rng);
        continentOffset = RandomOffset(rng);
        regionOffset = RandomOffset(rng);
        coastOffset = RandomOffset(rng);
    }

    // ---------- Caves ----------

    readonly Vector3 caveOffsetA, caveOffsetB, cavernOffset;
    readonly Vector2 caveRegionOffset, caveEntranceOffset;

    // Under land without a cave mouth, tunnels shrink away over CaveRoofFade blocks and stop CaveRoof blocks down
    const int CaveRoof = 4, CaveRoofFade = 4;

    /// <summary>Works out the cave settings for column (x, z), for IsCave.</summary>
    public CaveColumn GetCaveColumn(int x, int z, in VoxelColumn column)
    {
        var cave = new CaveColumn { surface = column.height, seaLevel = config.seaLevel };
        float amount = config.caveAmount * column.caves;
        if (config.caves && amount > 0f)
        {
            // Broad noise decides where tunnel clusters are; they get thinner toward a cluster's edge and vanish
            float s = config.caveScale * 0.25f;
            float region = Spread(Mathf.PerlinNoise((x + caveRegionOffset.x) * s, (z + caveRegionOffset.y) * s));
            float cluster = Mathf.Clamp01((region - (1f - amount)) / 0.15f);
            cave.tunnelWidth = config.caveWidth * cluster;

            if (config.caveEntrances && config.caveEntranceChance > 0f && cave.tunnelWidth > 0f)
            {
                float e = config.caveScale * 0.5f;
                float n = Spread(Mathf.PerlinNoise((x + caveEntranceOffset.x) * e, (z + caveEntranceOffset.y) * e));
                cave.entrance = Mathf.Clamp01((n - (1f - config.caveEntranceChance)) / 0.05f);
            }
        }
        cave.cavernSize = config.caverns ? config.cavernSize * column.caves : 0f;
        return cave;
    }

    // Perlin noise bunches up around 0.5; this spreads it out so a threshold of t leaves roughly 1 - t above it
    static float Spread(float n) => Mathf.Clamp01((n - 0.5f) * 2.2f + 0.5f);

    /// <summary>Whether a tunnel or cavern carves out (x, y, z). cave comes from GetCaveColumn(x, z).</summary>
    public bool IsCave(int x, int y, int z, in CaveColumn cave) => IsTunnel(x, y, z, cave) || IsCavern(x, y, z, cave);

    /// <summary>
    /// Tunnels follow the lines where two noise fields both cross zero. 4 blocks under sea beds and the land right by
    /// the shore stay solid so water never leaks in. Elsewhere they taper off a few blocks under the surface, except
    /// at the rare cave mouths.
    /// </summary>
    bool IsTunnel(int x, int y, int z, in CaveColumn cave)
    {
        float w = cave.tunnelWidth;
        if (w <= 0f || y < 1 || y > config.caveMaxY) return false;

        int depth = cave.surface - y;
        if (cave.surface < cave.seaLevel) { if (depth < 4) return false; }
        else if (cave.surface <= cave.seaLevel + 1) { if (depth < 1) return false; }
        else
        {
            float roof = Mathf.Clamp01((depth - CaveRoof) / (float)CaveRoofFade);
            w *= Mathf.Lerp(roof, 1f, cave.entrance);
            if (depth < 0 || w <= 0f) return false;
        }

        float s = config.caveScale;
        // Squashed vertically so tunnels wander sideways more than up and down
        float fx = x * s, fy = y * s * 1.6f, fz = z * s;
        float a = VoxelNoise.Perlin3(fx + caveOffsetA.x, fy + caveOffsetA.y, fz + caveOffsetA.z);
        if (Mathf.Abs(a) > w) return false;
        float b = VoxelNoise.Perlin3(fx + caveOffsetB.x, fy + caveOffsetB.y, fz + caveOffsetB.z);
        return a * a + b * b < w * w;
    }

    /// <summary>
    /// Caverns are blobs where squashed 3D noise is high, deep under the surface, thinning out near their top and
    /// bottom limits.
    /// </summary>
    bool IsCavern(int x, int y, int z, in CaveColumn cave)
    {
        int top = Mathf.Min(config.cavernMaxY, cave.surface - Mathf.Max(6, config.cavernMinDepth));
        if (cave.cavernSize <= 0f || y < 2 || y > top) return false;

        float fade = Mathf.Clamp01((top - y) / 8f) * Mathf.Clamp01((y - 2) / 6f);
        float s = config.cavernScale;
        Vector3 o = cavernOffset;
        float n = VoxelNoise.Perlin3(x * s + o.x, y * s * 2f + o.y, z * s + o.z) * 0.8f
                + VoxelNoise.Perlin3(x * s * 3f + o.z, y * s * 6f + o.x, z * s * 3f + o.y) * 0.2f;
        float threshold = Mathf.Lerp(0.4f, 0f, cave.cavernSize) + (1f - fade) * 0.5f;
        return n > threshold;
    }

    static Vector2 RandomOffset(System.Random rng) => new(rng.Next(-10000, 10000), rng.Next(-10000, 10000));

    public Vector2 GetClimate(int wx, int wz)
    {
        float s = config.climateNoiseScale;
        float t = VoxelNoise.FBm(wx + temperatureOffset.x, wz + temperatureOffset.y, s, 3, 0.5f, 2f);
        float h = VoxelNoise.FBm(wx + humidityOffset.x, wz + humidityOffset.y, s, 3, 0.5f, 2f);
        return new Vector2(Contrast(climateSpread.Even(t)), Contrast(climateSpread.Even(h)));
    }

    float Contrast(float v) => Mathf.Clamp01((v - 0.5f) * config.climateContrast + 0.5f);

    public BiomeConfig GetBiome(int wx, int wz) => SampleColumn(wx, wz).biome;

    /// <summary>
    /// The column as chunks generate it, slope smoothing included. Samples a whole neighborhood, so chunk generation
    /// uses SampleColumn plus SmoothSlopes on its grid instead.
    /// </summary>
    public VoxelColumn GetColumn(int wx, int wz)
    {
        int reach = SmoothingReach;
        if (reach == 0) return SampleColumn(wx, wz);

        long key = ((long)wx << 32) ^ (uint)wz;
        if (smoothedCache.TryGetValue(key, out VoxelColumn cached)) return cached;
        if (smoothedCache.Count > 4096) smoothedCache.Clear();

        int size = 2 * reach + 1;
        if (smoothingScratch == null || smoothingScratch.Length < size * size) smoothingScratch = new VoxelColumn[size * size];
        for (int z = 0; z < size; z++)
        for (int x = 0; x < size; x++)
            smoothingScratch[x + z * size] = SampleColumn(wx - reach + x, wz - reach + z);
        SmoothSlopes(smoothingScratch, size, wx - reach, wz - reach);

        VoxelColumn result = smoothingScratch[reach + reach * size];
        smoothedCache[key] = result;
        return result;
    }

    /// <summary>
    /// Slope blocks rise 1 block per block. Where the ground climbs faster, three heights meet at one corner and any
    /// slope there leaves a triangular wall. Each pass raises the lowest column at such corners by 1, filling the
    /// steep spot in from below. grid is size x size columns starting at (originX, originZ); after n passes the
    /// columns at least n from its edge are right.
    /// </summary>
    public void SmoothSlopes(VoxelColumn[] grid, int size, int originX, int originZ)
    {
        int passes = SmoothingReach;
        if (passes == 0) return;
        var heights = new int[size * size];
        var raised = new bool[size * size];

        for (int pass = 0; pass < passes; pass++)
        {
            for (int i = 0; i < size * size; i++) heights[i] = grid[i].height;
            System.Array.Clear(raised, 0, raised.Length);

            for (int z = 1; z < size - 1; z++)
            for (int x = 1; x < size - 1; x++)
            {
                int h = heights[x + z * size];
                for (int corner = 0; corner < 4 && !raised[x + z * size]; corner++)
                {
                    int vx = x + (corner & 1), vz = z + (corner >> 1);
                    int min = int.MaxValue, max = int.MinValue;
                    for (int cz = vz - 1; cz <= vz; cz++)
                    for (int cx = vx - 1; cx <= vx; cx++)
                    {
                        int n = heights[cx + cz * size];
                        if (n < min) min = n;
                        if (n > max) max = n;
                    }
                    if (h != min || max - min < 2) continue;

                    bool middle = false;
                    for (int cz = vz - 1; cz <= vz; cz++)
                    for (int cx = vx - 1; cx <= vx; cx++)
                    {
                        int n = heights[cx + cz * size];
                        if (n > min && n < max) middle = true;
                    }
                    if (middle && HasSlopes(originX + x, originZ + z, grid[x + z * size].biome))
                        raised[x + z * size] = true;
                }
            }

            for (int i = 0; i < size * size; i++)
                if (raised[i]) grid[i].height = Mathf.Min(grid[i].height + 1, Chunk.Height - 2);
        }
    }

    /// <summary>The column straight from the noise, before slope smoothing.</summary>
    public VoxelColumn SampleColumn(int wx, int wz)
    {
        BiomeConfig dominant, tintBiome;
        float height, caves;

        if (forceBiome is not null)
        {
            dominant = tintBiome = forceBiome;
            height = dominant.SampleHeight(wx, wz, heightOffset, plateauOffset, mountainOffset);
            caves = dominant.caves;
        }
        else
        {
            if (distances == null || distances.Length < biomes.Length)
            {
                distances = new float[biomes.Length];
                weights = new float[biomes.Length];
            }
            Vector2 climate = GetClimate(wx, wz);
            float minDist = float.MaxValue;
            int best = 0;
            for (int i = 0; i < biomes.Length; i++)
            {
                distances[i] = (biomes[i].Climate - climate).sqrMagnitude;
                if (distances[i] < minDist) { minDist = distances[i]; best = i; }
            }
            dominant = tintBiome = biomes[best];

            // Gaussian weights relative to the closest biome, so the dominant one always has weight 1
            float bw = config.blendWidth * config.blendWidth;
            float total = 0f;
            height = caves = 0f;
            for (int i = 0; i < biomes.Length; i++)
            {
                float w = Mathf.Exp(-(distances[i] - minDist) / bw);
                if (w < 0.001f) w = 0f;
                weights[i] = w;
                if (w == 0f) continue;
                height += biomes[i].SampleHeight(wx, wz, heightOffset, plateauOffset, mountainOffset) * w;
                caves += biomes[i].caves * w;
                total += w;
            }
            height /= total;
            caves /= total;
            float oceanBias = 0f;
            for (int i = 0; i < biomes.Length; i++) oceanBias += biomes[i].oceanBias * weights[i];
            oceanBias /= total;

            // Each column takes one nearby biome's grass color, by weight
            float roll = Hash01(wx, 0, wz, 4) * total;
            for (int i = 0; i < biomes.Length; i++)
            {
                roll -= weights[i];
                if (weights[i] > 0f && roll < 0f) { tintBiome = biomes[i]; break; }
            }

            // How much of the blend the dominant biome is: 1 deep inside it, 0.5 on its border
            float share = 1f / total;
            OverlayRegions(wx, wz, ref dominant, ref tintBiome, ref height, ref caves, ref oceanBias, ref share);
            if (config.oceans && config.seaLevel > 0)
                ApplyCoast(wx, wz, oceanBias, share, ref dominant, ref tintBiome, ref height);
        }

        return new VoxelColumn
        {
            height = Mathf.Clamp(Mathf.FloorToInt(height), 1, Chunk.Height - 2),
            biome = dominant,
            strataShift = dominant.GetStrataShift(wx, wz, strataOffset),
            seaLevel = config.seaLevel,
            caves = caves,
            tint = tintBiome.tintIndex
        };
    }

    // ---------- Regions, oceans and coasts ----------

    /// <summary>Lays the region biomes' patches over the climate biomes, blending their height in at the edges.</summary>
    void OverlayRegions(int wx, int wz, ref BiomeConfig dominant, ref BiomeConfig tintBiome, ref float height, ref float caves,
                        ref float oceanBias, ref float share)
    {
        for (int i = 0; i < regions.Length; i++)
        {
            BiomeConfig r = regions[i];
            float n = VoxelNoise.FBm(wx + regionOffset.x + i * 1733f, wz + regionOffset.y - i * 977f, 1f / r.regionSize, 2, 0.5f, 2f);
            float threshold = regionSpread.Cut(1f - r.regionRarity);
            float k = Mathf.SmoothStep(0f, 1f, (n - threshold) / 0.04f);
            if (k <= 0f) continue;
            height = Mathf.Lerp(height, r.SampleHeight(wx, wz, heightOffset, plateauOffset, mountainOffset), k);
            caves = Mathf.Lerp(caves, r.caves, k);
            oceanBias = Mathf.Lerp(oceanBias, r.oceanBias, k);
            share = k >= 0.5f ? k : share * (1f - k);
            if (k >= 0.5f) dominant = r;
            if (Hash01(wx, 1, wz, 4) < k) tintBiome = r;
        }
    }

    // Coast biomes reach this far out to sea (their sea bed); cliffs slope back down over CliffFade blocks behind them
    const float CoastSeaward = 10f, CliffFade = 24f;

    float Continent(float x, float z) =>
        VoxelNoise.FBm(x + continentOffset.x, z + continentOffset.y, config.oceanScale, 4, 0.5f, 2f);

    /// <summary>Roughly how many blocks (wx, wz) is from the shore: positive inland, negative out at sea.</summary>
    float ShoreDistance(int wx, int wz, float oceanBias)
    {
        // Ocean Bias -1..1 moves the local share of sea by up to half the world either way
        float threshold = continentSpread.Cut(Mathf.Clamp(config.oceanCoverage - oceanBias * 0.5f, 0.02f, 0.95f));
        float c = Continent(wx, wz);
        float e = c - threshold;
        if (Mathf.Abs(e) > 0.15f) return e > 0f ? 1000f : -1000f;
        // Height above the cut divided by how fast the noise changes is the distance to where it crosses the cut
        float gx = Continent(wx + 1, wz) - c, gz = Continent(wx, wz + 1) - c;
        float slope = Mathf.Max(Mathf.Sqrt(gx * gx + gz * gz), config.oceanScale * 0.15f);
        return Mathf.Clamp(e / slope, -1000f, 1000f);
    }

    /// <summary>Lowers the land into the sea and gives the shore its beach, cliffs and coast biome.</summary>
    void ApplyCoast(int wx, int wz, float oceanBias, float share, ref BiomeConfig dominant, ref BiomeConfig tintBiome, ref float height)
    {
        float d = ShoreDistance(wx, wz, oceanBias);
        float sea = config.seaLevel, land = height;

        // Land eases down to a beach just above the water, the sea floor drops away to the open ocean depth
        float shore = d >= 0f
            ? Mathf.Lerp(sea + 0.2f, land, Mathf.SmoothStep(0f, 1f, (d - 3f) / config.shoreRamp))
            : Mathf.Min(land, sea - 1.5f - config.oceanDepth * Mathf.SmoothStep(0f, 1f, -d / config.shelfWidth));
        height = shore;

        List<CoastType> coasts = dominant.coasts;
        if (coasts == null || coasts.Count == 0 || d < -CoastSeaward || d > maxCoastReach) return;

        PickCoast(coasts, wx, wz, out BiomeConfig coast, out BiomeConfig cliffBiome, out float cliff);
        // Cliffs sink away toward the biome's border, the next biome along the shore may not have them
        cliff *= Mathf.SmoothStep(0f, 1f, (share - 0.5f) / 0.3f);
        if (cliff > 0f)
        {
            height = Mathf.Lerp(shore, CliffHeight(wx, wz, d, land, cliffBiome), cliff);
            if (cliff >= 0.5f) coast = cliffBiome;
        }
        if (coast == null) return;

        float edge = coast.coastWidth * (0.7f + 0.6f * Mathf.PerlinNoise((wx + coastOffset.x) * 0.15f, (wz + coastOffset.y) * 0.15f));
        if (d <= edge) dominant = tintBiome = coast;
    }

    /// <summary>
    /// Stretches of shore (cells about Coast Stretch wide, with wobbly borders) each get a coast type by weight.
    /// nearest is this column's, cliff how much cliff coast is around (blended over the middle of the cells so cliffs
    /// taper off instead of ending in a wall) and cliffBiome which cliff coast that is.
    /// </summary>
    void PickCoast(List<CoastType> coasts, int wx, int wz, out BiomeConfig nearest, out BiomeConfig cliffBiome, out float cliff)
    {
        float cell = config.coastStretch;
        float px = wx + (Mathf.PerlinNoise((wx + coastOffset.x) * 0.015f, (wz + coastOffset.y) * 0.015f) - 0.5f) * cell * 0.8f;
        float pz = wz + (Mathf.PerlinNoise((wz + coastOffset.y) * 0.015f, (wx + coastOffset.x) * 0.015f) - 0.5f) * cell * 0.8f;
        float gx = px / cell, gz = pz / cell;
        int x0 = Mathf.FloorToInt(gx), z0 = Mathf.FloorToInt(gz);
        float fx = gx - x0, fz = gz - z0;
        nearest = CoastAt(coasts, x0 + (fx >= 0.5f ? 1 : 0), z0 + (fz >= 0.5f ? 1 : 0));

        float sx = Mathf.SmoothStep(0f, 1f, (fx - 0.3f) / 0.4f), sz = Mathf.SmoothStep(0f, 1f, (fz - 0.3f) / 0.4f);
        cliff = 0f;
        cliffBiome = null;
        float best = 0f;
        for (int i = 0; i < 4; i++)
        {
            int ix = i & 1, iz = i >> 1;
            BiomeConfig b = CoastAt(coasts, x0 + ix, z0 + iz);
            if (b == null || b.cliffHeight <= 0f) continue;
            float w = (ix == 1 ? sx : 1f - sx) * (iz == 1 ? sz : 1f - sz);
            cliff += w;
            if (w > best) { best = w; cliffBiome = b; }
        }
    }

    BiomeConfig CoastAt(List<CoastType> coasts, int cellX, int cellZ)
    {
        float total = 0f;
        foreach (CoastType c in coasts)
            if (c != null) total += c.weight;
        float roll = Hash01(cellX, 9, cellZ, 31) * total;
        foreach (CoastType c in coasts)
        {
            if (c == null || c.weight <= 0f) continue;
            roll -= c.weight;
            if (roll < 0f) return c.biome;
        }
        return null;
    }

    /// <summary>
    /// Ragged cliffs rising straight out of deep water to cliffHeight, with sea stacks in the surf, falling back to
    /// the land's height behind their tops.
    /// </summary>
    float CliffHeight(int wx, int wz, float d, float land, BiomeConfig b)
    {
        float sea = config.seaLevel, j = b.cliffJaggedness;
        float ridges = VoxelNoise.FBm(wx + coastOffset.y, wz + coastOffset.x, 0.07f, 3, 0.55f, 2.2f, true);
        float spikes = Hash01(wx, 2, wz, 32);
        float top = sea + b.cliffHeight * Mathf.Lerp(1f, 0.3f + 0.9f * ridges, j) + (spikes - 0.5f) * 5f * j;

        if (d < 0f)
        {
            if (-d < 9f && ridges > 0.7f && j > 0f)
                return Mathf.Lerp(sea - 2f, top, Mathf.Clamp01((ridges - 0.7f) / 0.12f) * (1f + d / 9f));
            return sea - 2f - config.oceanDepth * Mathf.SmoothStep(0f, 1f, -d / 6f);
        }
        float wall = Mathf.Lerp(sea - 2f, top, Mathf.SmoothStep(0f, 1f, d / 2.5f));
        float back = Mathf.SmoothStep(0f, 1f, (d - b.coastWidth) / CliffFade);
        return Mathf.Lerp(wall, Mathf.Max(land, sea + 2f), back);
    }

    // ---------- Ores ----------

    /// <summary>Swaps in ore for the block at (x, y, z) in a column of biome if an ore vein covers it.</summary>
    public byte ApplyOres(int x, int y, int z, byte block, BiomeConfig biome)
    {
        for (int i = 0; i < ores.Count; i++)
        {
            Ore ore = ores[i];
            if (block != ore.replacesId || y < ore.minY || y > ore.maxY) continue;
            if (ore.biomes != null && System.Array.IndexOf(ore.biomes, biome) < 0) continue;
            // Veins are decided per vein-sized cube, then filled in per block
            int v = ore.veinSize;
            if (Hash01(FloorDiv(x, v), FloorDiv(y, v), FloorDiv(z, v), 100 + i) < ore.frequency && Hash01(x, y, z, 200 + i) < ore.density)
                return ore.blockId;
        }
        return block;
    }

    static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

    // ---------- Trees ----------

    /// <summary>
    /// Whether a tree grows on column (x, z), which kind, and how tall its trunk is. Each of the biome's tree types
    /// gets its chance of the column. Trees need the biome's surface block above water (and not carved out by a cave)
    /// and flat ground all around, so no slope sits where the trunk goes. Oaks need level ground under their whole
    /// 2x2 trunk and roots. column must cover 2 blocks around (x, z).
    /// </summary>
    public bool TryGetTree(int x, int z, System.Func<int, int, VoxelColumn> column, out TreeType tree, out int trunkHeight)
    {
        tree = null;
        trunkHeight = 0;
        VoxelColumn c = column(x, z);
        BiomeConfig b = c.biome;
        if (b.trees == null || b.trees.Count == 0 || c.height < config.seaLevel) return false;

        float roll = Hash01(x, 0, z, 1), total = 0f;
        foreach (TreeType t in b.trees)
        {
            if (t == null) continue;
            total += t.chance;
            if (roll < total) { tree = t; break; }
        }
        if (tree == null || tree.logId == 0 || c.GetBlock(c.height) != b.SurfaceId) return false;

        // Bones lie wherever they fell, slopes included
        bool oak = tree.shape == TreeShape.Oak;
        int far = oak ? 2 : 1;
        for (int dz = -1; dz <= far && tree.shape != TreeShape.Bones; dz++)
        for (int dx = -1; dx <= far; dx++)
        {
            int height = column(x + dx, z + dz).height;
            if (height > c.height || (oak && height != c.height)) return false;
        }
        if (IsCave(x, c.height, z, GetCaveColumn(x, z, c))) return false;

        int min = Mathf.Max(1, Mathf.Min(tree.minHeight, tree.maxHeight)), max = Mathf.Max(min, tree.maxHeight);
        trunkHeight = min + Mathf.FloorToInt(Hash01(x, 1, z, 2) * (max - min + 1));
        return true;
    }

    /// <summary>The plant growing on column (x, z), or 0. Only says what the biome would grow; ChunkData checks there's room.</summary>
    public byte GetPlant(int x, int z, in VoxelColumn c)
    {
        List<PlantType> plants = c.biome.plants;
        if (plants == null || plants.Count == 0 || c.height < config.seaLevel) return 0;
        float roll = Hash01(x, 0, z, 5), total = 0f;
        foreach (PlantType p in plants)
        {
            if (p == null) continue;
            total += p.chance;
            if (roll < total) return p.id;
        }
        return 0;
    }

    /// <summary>One block of a tree: dx/dz from the trunk, y in the world, log = wood (otherwise leaves).</summary>
    public delegate void TreeBlockSink(int dx, int y, int dz, bool log);

    /// <summary>
    /// Every block of the tree TryGetTree found on column (x, z), whose ground is at height ground. Comes out the same
    /// every time, so each chunk a tree overlaps builds the same part of it. Nothing goes further than TreeRadius from
    /// the trunk. Blocks may be reported more than once.
    /// </summary>
    public void BuildTree(int x, int z, int ground, TreeType tree, int trunkHeight, TreeBlockSink sink)
    {
        switch (tree.shape)
        {
            case TreeShape.Oak: BuildOak(x, z, ground, tree, trunkHeight, sink); return;
            case TreeShape.Palm: BuildPalm(x, z, ground, tree, trunkHeight, sink); return;
            case TreeShape.Cactus: BuildCactus(x, z, ground, trunkHeight, sink); return;
            case TreeShape.Bones: BuildBones(x, z, ground, trunkHeight, sink); return;
            case TreeShape.Willow: BuildWillow(x, z, ground, tree, trunkHeight, sink); return;
        }

        int top = ground + trunkHeight;
        LeafRange(tree, trunkHeight, out int minDy, out int maxDy);
        int r = LeafRadius(tree);
        for (int dy = minDy; dy <= maxDy; dy++)
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
            if (IsLeaf(tree, x, z, trunkHeight, dx, dy, dz)) sink(dx, top + dy, dz, false);
        for (int y = ground + 1; y <= top; y++) sink(0, y, 0, true);
    }

    static void LeafRange(TreeType tree, int trunkHeight, out int minDy, out int maxDy)
    {
        int r = LeafRadius(tree);
        switch (tree.shape)
        {
            case TreeShape.Pine: minDy = Mathf.Min(0, 2 - trunkHeight); maxDy = 1; break;
            case TreeShape.Ball: minDy = -r; maxDy = r; break;
            default: minDy = -2; maxDy = 1; break;
        }
    }

    static int LeafRadius(TreeType tree) => Mathf.Clamp(tree.leafRadius, 1, TreeRadius);

    [System.ThreadStatic] static Vector4[] oakClumps;
    const int MaxOakClumps = 32;

    /// <summary>
    /// A broad dome crown of overlapping leaf clumps (ragged balls) spread over its surface, filled in behind, on a
    /// trunk that splits into limbs reaching up into the clumps. Big ones get a 2x2 trunk with roots.
    /// </summary>
    void BuildOak(int x, int z, int ground, TreeType tree, int trunkHeight, TreeBlockSink sink)
    {
        float r = Mathf.Min(Mathf.Clamp(tree.leafRadius, 3, TreeRadius) * Mathf.Lerp(0.75f, 1f, Hash01(x, 3, z, 2)), TreeRadius - 0.5f);
        float h = r * 0.8f;
        int baseY = ground + trunkHeight;
        float cy = baseY + h * 0.45f;
        bool thick = r >= 5f;
        // A 2x2 trunk covers dx/dz 0-1, so the crown centers between its logs
        float c = thick ? 0.5f : 0f;

        oakClumps ??= new Vector4[MaxOakClumps];
        Vector4[] clumps = oakClumps;
        int count = Mathf.Min(MaxOakClumps, 6 + Mathf.RoundToInt(r * 2.5f));
        float spin = Hash01(x, 4, z, 2) * Mathf.PI * 2f;
        float clumpScale = Mathf.Clamp(r / 5.5f, 0.7f, 1.1f);
        for (int i = 0; i < count; i++)
        {
            // Spread like seeds in a sunflower over a band of the dome, from the top down to a little under its middle
            float up = Mathf.Lerp(1f, -0.35f, (i + 0.5f) / count);
            float around = Mathf.Sqrt(1f - up * up);
            float angle = spin + i * 2.39996f + (Hash01(x, 10 + i, z, 6) - 0.5f) * 0.8f;
            float size = Mathf.Lerp(1.6f, 2.6f, Hash01(x, 10 + i, z, 7)) * clumpScale;
            float reach = Mathf.Lerp(0.85f, 1f, Hash01(x, 10 + i, z, 8));
            float spread = (r - size * 0.6f) * reach * around;
            clumps[i] = new Vector4(c + Mathf.Cos(angle) * spread, cy + up * (h - size * 0.6f) * reach, c + Mathf.Sin(angle) * spread, size);
        }

        // Leaves: the clumps over a solid core, with a flat underside at the top of the trunk that the outer clumps
        // hang a little below
        for (int i = 0; i < count; i++)
        {
            Vector4 k = clumps[i];
            OakBlob(x, z, k.x, k.y, k.z, k.w, k.w * 0.8f, baseY - 1, sink);
        }
        OakBlob(x, z, c, cy, c, r * 0.8f, h * 0.8f, baseY, sink);

        // Limbs from the trunk up into the lower clumps
        int topY = Mathf.RoundToInt(cy + h * 0.2f);
        for (int i = 0; i < count; i++)
        {
            Vector4 k = clumps[i];
            if (k.y > cy + h * 0.5f || Hash01(x, 10 + i, z, 9) > 0.6f) continue;
            float startY = baseY - 1 + Hash01(x, 10 + i, z, 11) * 2f;
            var from = new Vector3(c, startY, c);
            var to = new Vector3(c + (k.x - c) * 0.85f, k.y - 0.5f, c + (k.z - c) * 0.85f);
            int steps = Mathf.CeilToInt(Vector3.Distance(from, to) * 2f);
            for (int s = 0; s <= steps; s++)
            {
                Vector3 p = Vector3.Lerp(from, to, s / (float)steps);
                OakBlock(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y), Mathf.RoundToInt(p.z), true, sink);
            }
        }

        int width = thick ? 2 : 1;
        for (int dz = 0; dz < width; dz++)
        for (int dx = 0; dx < width; dx++)
        for (int y = ground + 1; y <= topY; y++)
            sink(dx, y, dz, true);

        // Roots flaring out at the foot of big trunks, on the flat ground TryGetTree checked
        if (!thick) return;
        for (int i = 0; i < 4; i++)
        {
            if (Hash01(x, 20 + i, z, 12) > 0.6f) continue;
            int dx = i == 0 ? -1 : i == 1 ? 2 : Hash01(x, 20 + i, z, 13) < 0.5f ? 0 : 1;
            int dz = i == 2 ? -1 : i == 3 ? 2 : Hash01(x, 20 + i, z, 13) < 0.5f ? 0 : 1;
            sink(dx, ground + 1, dz, true);
        }
    }

    /// <summary>Leaves in a ragged ellipsoid centered at (cx, cy, cz) from the trunk, none below minY.</summary>
    void OakBlob(int x, int z, float cx, float cy, float cz, float radius, float height, int minY, TreeBlockSink sink)
    {
        int x0 = Mathf.FloorToInt(cx - radius), x1 = Mathf.CeilToInt(cx + radius);
        int z0 = Mathf.FloorToInt(cz - radius), z1 = Mathf.CeilToInt(cz + radius);
        int y0 = Mathf.Max(minY, Mathf.FloorToInt(cy - height)), y1 = Mathf.CeilToInt(cy + height);
        // A third of the outermost half block is left out, which roughens the surface without holes
        float solid = Mathf.Max(0f, 1f - 0.5f / radius);
        for (int y = y0; y <= y1; y++)
        for (int dz = z0; dz <= z1; dz++)
        for (int dx = x0; dx <= x1; dx++)
        {
            float ex = (dx - cx) / radius, ey = (y - cy) / height, ez = (dz - cz) / radius;
            float d = Mathf.Sqrt(ex * ex + ey * ey + ez * ez);
            if (d > 1f || (d > solid && Hash01(x + dx, y, z + dz, 10) < 0.35f)) continue;
            OakBlock(dx, y, dz, false, sink);
        }
    }

    static void OakBlock(int dx, int y, int dz, bool log, TreeBlockSink sink)
    {
        if (Mathf.Abs(dx) <= TreeRadius && Mathf.Abs(dz) <= TreeRadius) sink(dx, y, dz, log);
    }

    /// <summary>A trunk leaning further and further over toward its top, with fronds arching out and drooping.</summary>
    void BuildPalm(int x, int z, int ground, TreeType tree, int trunkHeight, TreeBlockSink sink)
    {
        float angle = Hash01(x, 3, z, 2) * Mathf.PI * 2f;
        float lean = Mathf.Lerp(0.5f, 3f, Hash01(x, 4, z, 2));
        int tx = 0, tz = 0;
        for (int i = 1; i <= trunkHeight; i++)
        {
            float t = i / (float)trunkHeight;
            int nx = Mathf.RoundToInt(Mathf.Cos(angle) * lean * t * t), nz = Mathf.RoundToInt(Mathf.Sin(angle) * lean * t * t);
            // A step under each sideways move keeps the trunk joined face to face
            if (nx != tx || nz != tz) sink(nx, ground + i - 1, nz, true);
            tx = nx;
            tz = nz;
            sink(tx, ground + i, tz, true);
        }

        int top = ground + trunkHeight;
        int length = Mathf.Clamp(tree.leafRadius, 2, TreeRadius - 3);
        sink(tx, top + 1, tz, false);
        for (int f = 0; f < 8; f++)
        {
            int fx = f < 4 ? (f == 0 ? 1 : f == 1 ? -1 : 0) : (f & 1) == 0 ? 1 : -1;
            int fz = f < 4 ? (f == 2 ? 1 : f == 3 ? -1 : 0) : f < 6 ? 1 : -1;
            int reach = f < 4 ? length : length - 1;
            // Level off the crown, then drooping a block per block, with the tip hanging down
            int y = top + 1;
            for (int k = 1; k <= reach; k++)
            {
                y = top + 1 - Mathf.Max(0, k - 2);
                OakBlock(tx + fx * k, y, tz + fz * k, false, sink);
            }
            OakBlock(tx + fx * reach, y - 1, tz + fz * reach, false, sink);
        }
    }

    /// <summary>A column with a few arms reaching out and up.</summary>
    void BuildCactus(int x, int z, int ground, int trunkHeight, TreeBlockSink sink)
    {
        for (int y = 1; y <= trunkHeight; y++) sink(0, ground + y, 0, true);
        if (trunkHeight < 4) return;
        for (int side = 0; side < 4; side++)
        {
            if (Hash01(x, 5 + side, z, 2) > 0.3f) continue;
            int dx = side == 0 ? 1 : side == 1 ? -1 : 0, dz = side == 2 ? 1 : side == 3 ? -1 : 0;
            int armY = ground + 2 + Mathf.FloorToInt(Hash01(x, 9 + side, z, 2) * (trunkHeight - 3));
            int up = 1 + Mathf.FloorToInt(Hash01(x, 13 + side, z, 2) * 2f);
            sink(dx, armY, dz, true);
            sink(dx * 2, armY, dz * 2, true);
            for (int k = 1; k <= up; k++) sink(dx * 2, armY + k, dz * 2, true);
        }
    }

    /// <summary>
    /// A ribcage: a spine with open ribs arching over it and a chunky skull, sunk up to 2 blocks into the ground.
    /// The trunk height sets how long it is.
    /// </summary>
    void BuildBones(int x, int z, int ground, int trunkHeight, TreeBlockSink sink)
    {
        bool alongX = Hash01(x, 3, z, 2) < 0.5f;
        int half = Mathf.Clamp(trunkHeight, 2, TreeRadius - 2);
        int y0 = ground + 1 - Mathf.FloorToInt(Hash01(x, 4, z, 2) * 3f);
        void Bone(int along, int y, int side)
        {
            if (alongX) sink(along, y, side, true);
            else sink(side, y, along, true);
        }

        for (int a = -half; a <= half; a++) Bone(a, y0, 0);
        for (int a = -half + 1; a <= half - 1; a += 2)
        {
            bool big = Mathf.Abs(a) < half * 0.6f;
            for (int s = -1; s <= 1; s += 2)
            {
                Bone(a, y0, s);
                Bone(a, y0 + 1, 2 * s);
                if (big) Bone(a, y0 + 2, 2 * s);
                Bone(a, y0 + (big ? 3 : 2), s);
            }
        }
        for (int a = -half - 2; a <= -half - 1; a++)
        for (int s = -1; s <= 1; s++)
        for (int y = y0; y <= y0 + 1; y++)
            if (y == y0 || s == 0 || a == -half - 1) Bone(a, y, s);
    }

    /// <summary>A wide, flattened crown with strands of leaves hanging from its rim.</summary>
    void BuildWillow(int x, int z, int ground, TreeType tree, int trunkHeight, TreeBlockSink sink)
    {
        int r = Mathf.Clamp(tree.leafRadius, 2, TreeRadius - 1);
        int top = ground + trunkHeight;
        for (int y = ground + 1; y <= top + 1; y++) sink(0, y, 0, true);

        float edge = r + 0.5f;
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            float flat = (dx * dx + dz * dz) / (edge * edge);
            if (flat > 1f) continue;
            // Thicker in the middle, a single layer at the rim
            int bottom = top - (flat < 0.5f ? 1 : 0), roof = top + 1 + (flat < 0.3f ? 1 : 0) + (flat < 0.7f ? 1 : 0);
            for (int y = bottom; y <= roof; y++)
                if (flat < 0.8f || Hash01(x + dx, y, z + dz, 3) < 0.75f) sink(dx, y, dz, false);

            if (flat < 0.45f || Hash01(x + dx, 5, z + dz, 3) > 0.55f) continue;
            int strand = 1 + Mathf.FloorToInt(Hash01(x + dx, 6, z + dz, 3) * (trunkHeight - 1));
            for (int y = bottom - 1; y >= bottom - strand && y > ground + 1; y--) sink(dx, y, dz, false);
        }
    }

    /// <summary>Whether a leaf block goes at offset (dx, dy, dz) from the top of a trunk. Deterministic per tree.</summary>
    bool IsLeaf(TreeType tree, int treeX, int treeZ, int trunkHeight, int dx, int dy, int dz)
    {
        LeafRange(tree, trunkHeight, out int minDy, out int maxDy);
        if (dy < minDy || dy > maxDy) return false;
        int big = LeafRadius(tree);
        int ax = Mathf.Abs(dx), az = Mathf.Abs(dz);

        switch (tree.shape)
        {
            case TreeShape.Pine:
            {
                // Layers alternate wide and narrow, widening toward the bottom, with a single leaf on the tip
                int d = 1 - dy;
                int wide = Mathf.Min(big, 1 + (d - 1) / 2);
                int r = d == 0 ? 0 : d % 2 == 0 && d > 2 ? wide - 1 : wide;
                return Mathf.Max(ax, az) <= r && ax + az <= (r <= 1 ? r : r + 1);
            }
            case TreeShape.Ball:
            {
                float edge = big + 0.5f;
                float dist = ax * ax + dy * dy + az * az;
                if (dist > edge * edge) return false;
                // The outer shell is a little ragged
                return dist < (edge - 1f) * (edge - 1f) || Hash01(treeX + dx, dy, treeZ + dz, 3) < 0.7f;
            }
            default:
            {
                // Wide layers around the top of the trunk, then narrow ones above
                int r = dy <= -1 ? big : Mathf.Max(1, big - 1);
                if (ax > r || az > r) return false;
                bool corner = ax == r && az == r;
                if (!corner) return true;
                // Corners are trimmed on the top layer and randomly elsewhere
                return dy < 1 && Hash01(treeX + dx, dy, treeZ + dz, 3) < 0.5f;
            }
        }
    }

    // ---------- Slopes ----------
    // Slopes fill the air cell on top of a column toward higher neighbors. Each top corner of that cell is raised if
    // it touches a higher column and no lower one (see CornerLevel). Corners only depend on the corner's position and
    // the cell's height, so neighboring cells always agree, and inner/outer corners fall out on their own.

    /// <summary>
    /// Whether the air cell on top of column (x, z) gets a sloped block, and its shape (see VoxelShapes).
    /// Cube means the cell is surrounded by steps and gets filled. column must cover SlopeReach blocks around (x, z).
    /// </summary>
    public bool TryGetSlope(int x, int z, System.Func<int, int, VoxelColumn> column, out byte shape)
    {
        int step = column(x, z).height + 1;
        int q00 = CornerLevel(x, z, step, column);
        int q10 = CornerLevel(x + 1, z, step, column);
        int q01 = CornerLevel(x, z + 1, step, column);
        int q11 = CornerLevel(x + 1, z + 1, step, column);

        shape = VoxelShapes.FromCorners(q00, q10, q01, q11, DiagonalEdgeSplit(x, z, step, q00, q10, q01, q11, column));
        return q00 + q10 + q01 + q11 > 0;
    }

    /// <summary>
    /// Corner pieces normally crease through their odd corner. Along a diagonal (staircase) edge that turns into an
    /// accordion of ridges and valleys, so there the crease runs across instead, making one flat diagonal ramp.
    /// Returns null to keep the default.
    /// </summary>
    bool? DiagonalEdgeSplit(int x, int z, int step, int q00, int q10, int q01, int q11, System.Func<int, int, VoxelColumn> column)
    {
        int odd;
        if (q10 == q01 && q01 == q11 && q00 != q11) odd = 0;
        else if (q00 == q01 && q01 == q11 && q10 != q00) odd = 1;
        else if (q00 == q10 && q10 == q11 && q01 != q00) odd = 2;
        else if (q00 == q10 && q10 == q01 && q11 != q00) odd = 3;
        else return null;

        int oddLevel = odd switch { 0 => q00, 1 => q10, 2 => q01, _ => q11 };
        int ox = odd & 1, oz = odd >> 1;
        int dx = ox == 1 ? 1 : -1, dz = oz == 1 ? 1 : -1;

        // Corners just past the two other side corners, on the odd corner's side. If they match the odd corner the
        // edge keeps going diagonally; on a straight edge they match the rest of the cell.
        int a = CornerLevel(x + 1 - ox, z + oz + dz, step, column);
        int b = CornerLevel(x + ox + dx, z + 1 - oz, step, column);
        if (a != oddLevel || b != oddLevel) return null;

        bool oddOnMain = odd == 0 || odd == 3;
        return oddOnMain;
    }

    /// <summary>
    /// 1 if a slope corner at lattice point (vx, vz) is raised for cells below stepHeight, otherwise 0. It rises
    /// against any higher column, cliffs included, but not where it touches lower ground, since the slope's side
    /// would stick out over the drop.
    /// </summary>
    int CornerLevel(int vx, int vz, int stepHeight, System.Func<int, int, VoxelColumn> column)
    {
        bool raised = false;
        for (int cz = vz - 1; cz <= vz; cz++)
        for (int cx = vx - 1; cx <= vx; cx++)
        {
            VoxelColumn c = column(cx, cz);
            if (c.height < stepHeight - 1) return 0;
            if (!raised && c.height >= stepHeight && HasSlopes(cx, cz, c.biome)) raised = true;
        }
        return raised ? 1 : 0;
    }

    bool HasSlopes(int x, int z, BiomeConfig biome)
    {
        float chance = biome.slopeChance;
        if (chance <= 0f) return false;
        if (chance >= 1f) return true;
        float n = Mathf.PerlinNoise((x + slopeOffset.x) * SlopeNoiseScale, (z + slopeOffset.y) * SlopeNoiseScale);
        // Perlin clusters around 0.5, stretch it so the chance roughly matches the covered area
        return Mathf.Clamp01((n - 0.5f) * 2f + 0.5f) < chance;
    }

    float Hash01(int x, int y, int z, int salt)
    {
        unchecked
        {
            uint h = seedHash ^ ((uint)x * 0x85EBCA6Bu) ^ ((uint)y * 0x27D4EB2Fu) ^ ((uint)z * 0xC2B2AE35u) ^ ((uint)salt * 0x165667B1u);
            h ^= h >> 16; h *= 0x7FEB352Du;
            h ^= h >> 15; h *= 0x846CA68Bu;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777216f;
        }
    }
}
