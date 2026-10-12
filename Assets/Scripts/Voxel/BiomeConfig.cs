using System.Collections.Generic;
using UnityEngine;

public enum TreeShape { Round, Pine, Ball, Oak, Palm, Cactus, Bones, Willow }

public enum BiomePlacement { Climate, Region, Coast }

[System.Serializable]
public class TreeType
{
    [Tooltip("Round = small blocky oak, Pine = layered cone, Ball = a round canopy, Oak = a broad dome of leafy " +
             "clumps on branching limbs, like a lone oak in a field (Leaf Radius 5+ makes big ones). Palm = a leaning " +
             "trunk with drooping fronds. Cactus = a column with arms (no leaves). Bones = a half buried ribcage, Log " +
             "is the bone block (no leaves, grows on slopes too). Willow = a round crown with hanging strands.")]
    public TreeShape shape = TreeShape.Round;
    [BlockName] public string log = "Log";
    [BlockName] public string leaves = "Leaves";
    [Tooltip("Chance for each surface column to grow one.")]
    [Range(0, 0.2f)] public float chance = 0.01f;
    [Tooltip("Trunk height in blocks. For Oak, the bare trunk below the crown.")]
    [Min(1)] public int minHeight = 4;
    [Min(1)] public int maxHeight = 6;
    [Tooltip("How far leaves reach out from the trunk. Oak crowns vary between 75% and 100% of this.")]
    [Range(1, TerrainGenerator.TreeRadius)] public int leafRadius = 2;

    [System.NonSerialized] public byte logId, leavesId;
}

[System.Serializable]
public class PlantType
{
    [Tooltip("A block with Plant ticked in the Block Database, e.g. tall grass or a flower.")]
    [BlockName] public string block = "Tall Grass";
    [Tooltip("Chance for each surface column to grow one.")]
    [Range(0, 1)] public float chance = 0.1f;

    [System.NonSerialized] public byte id;
}

[System.Serializable]
public class CoastType
{
    [Tooltip("A biome with Placement = Coast. Empty = that stretch of shore stays this biome.")]
    public BiomeConfig biome;
    [Tooltip("Chance compared with the other coast types.")]
    [Min(0)] public float weight = 1f;
}

[CreateAssetMenu(fileName = "New Biome", menuName = "Voxel/Biome")]
public class BiomeConfig : ScriptableObject
{
    [Header("Placement")]
    [Tooltip("Climate = wherever the climate is closest to its temperature and humidity. Region = rare patches laid " +
             "over the climate biomes, see Region Size and Rarity. Coast = only on the shores of biomes that list it " +
             "under Coasts.")]
    public BiomePlacement placement = BiomePlacement.Climate;
    [Tooltip("Region biomes: roughly how wide one patch is, in blocks.")]
    [Min(64)] public float regionSize = 600f;
    [Tooltip("Region biomes: about how much of the world they cover.")]
    [Range(0.01f, 0.5f)] public float regionRarity = 0.08f;

    [Header("Climate (where this biome appears)")]
    [Range(0, 1)] public float temperature = 0.5f;
    [Range(0, 1)] public float humidity = 0.5f;

    [Header("Base Terrain")]
    public float baseHeight = 32f;
    public float heightScale = 12f;
    public float noiseScale = 0.02f;
    [Range(1, 8)] public int octaves = 4;
    [Range(0, 1)] public float persistence = 0.5f;
    public float lacunarity = 2f;
    [Tooltip("Ridged noise gives sharp mountain ridges.")]
    public bool ridged;
    [Tooltip("Remaps the 0-1 noise before it's multiplied by Height Scale.")]
    public AnimationCurve heightCurve = AnimationCurve.Linear(0, 0, 1, 1);

    [Header("Plateaus / Mesas / Buttes (height 0 = off)")]
    public float plateauHeight = 0f;
    public float plateauNoiseScale = 0.01f;
    [Tooltip("Plateaus rise where the plateau noise is above this.")]
    [Range(0, 1)] public float plateauThreshold = 0.55f;
    [Tooltip("Noise range over which a plateau rises to full height. Smaller = steeper cliffs.")]
    [Range(0.005f, 0.5f)] public float plateauEdgeWidth = 0.05f;
    [Tooltip("Number of ledges on the plateau sides. 0 = smooth ramp.")]
    [Range(0, 12)] public int plateauSteps = 0;

    [Header("Mountains (height 0 = off)")]
    [Tooltip("How many blocks the highest peaks rise above the rest of the terrain. The world is 128 blocks tall.")]
    [Min(0)] public float mountainHeight = 0f;
    [Tooltip("Smaller = wider mountains, with ranges further apart.")]
    public float mountainNoiseScale = 0.008f;
    [Tooltip("How much of the biome is mountainous.")]
    [Range(0, 1)] public float mountainCoverage = 0.4f;
    [Tooltip("Higher = sharper ridges and pointier peaks, lower = rounder mountains.")]
    [Range(0.5f, 4f)] public float mountainSharpness = 1.5f;

    [Header("Dunes (height 0 = off)")]
    [Tooltip("Height of the dune crests in blocks.")]
    [Min(0)] public float duneHeight = 0f;
    [Tooltip("Blocks from one crest to the next.")]
    [Min(4)] public float duneSpacing = 40f;
    [Tooltip("Direction the wind blows, in degrees. Dunes have a gentle side facing the wind and a steep side away from it.")]
    [Range(0, 360)] public float duneWind = 30f;

    [Header("Terracing of the whole terrain (0 = off)")]
    public int terraceStep = 0;
    [Range(0, 1)] public float terraceStrength = 1f;

    [Header("Slopes (smooth steps and the foot of cliffs, including corners, using the surface block)")]
    [Tooltip("Share of the area where steps get slopes. Decided in patches, so an edge is either sloped or not. 0 = no slopes.")]
    [Range(0, 1)] public float slopeChance = 1f;

    [Header("Blocks (names from the Block Database)")]
    [BlockName] public string surfaceBlock = "Grass";
    [BlockName] public string subSurfaceBlock = "Dirt";
    public int subSurfaceDepth = 3;
    [BlockName] public string deepBlock = "Stone";
    [Tooltip("Ground more than Bottom Depth blocks under the surface turns into this, e.g. Stone under desert " +
             "sandstone so stone ores can form. Empty = Deep Block all the way down.")]
    [BlockName] public string bottomBlock = "";
    [Min(1)] public int bottomDepth = 12;
    [Tooltip("Surface columns at or above this Y use Peak Block instead (e.g. snow caps). 0 = off.")]
    public int peakHeight = 0;
    [BlockName] public string peakBlock = "";
    [Tooltip("Surface block for ground under water (lake and sea beds). Empty = Sub Surface Block.")]
    [BlockName] public string underwaterSurfaceBlock = "";
    [Tooltip("Replaces the top layer of water, e.g. Ice in cold biomes. Empty = open water.")]
    [BlockName] public string waterSurfaceBlock = "";

    [Header("Grass Color")]
    [Tooltip("Recolor blocks with Biome Tint on (grass, leaves, tall grass) in this biome. Only their green pixels change.")]
    public bool tintGrass = true;
    [Tooltip("What the green parts of those blocks look like on average. Alpha = strength.")]
    public Color grassColor = new Color32(145, 189, 89, 255);

    [Header("Caves")]
    [Tooltip("How many caves this biome has, times the World Gen Config's Cave Amount. 0 = solid ground.")]
    [Range(0, 1)] public float caves = 1f;

    [Header("Oceans and Coasts")]
    [Tooltip("Shifts the ocean noise where this biome is: negative = more sea (islands and bays), positive = more land.")]
    [Range(-1, 1)] public float oceanBias = 0f;
    [Tooltip("What this biome's shores turn into, picked by weight per stretch of coast.")]
    public List<CoastType> coasts = new();
    [Tooltip("Coast biomes: how far inland from the waterline they reach, in blocks.")]
    [Min(1)] public float coastWidth = 6f;
    [Tooltip("Coast biomes: instead of a beach, cliffs rise this many blocks above sea level straight out of deep " +
             "water. 0 = a beach.")]
    [Min(0)] public float cliffHeight = 0f;
    [Tooltip("0 = smooth cliff walls, 1 = jagged spires and sea stacks.")]
    [Range(0, 1)] public float cliffJaggedness = 0.6f;

    [Header("Atmosphere")]
    [Tooltip("Fog distance here as a share of the normal one. Lower = foggier.")]
    [Range(0.05f, 1f)] public float fogDistance = 1f;
    [Tooltip("Fog color here. Alpha = how much it replaces the Voxel World's fog color.")]
    public Color fogColor = new(1f, 1f, 1f, 0f);
    [Tooltip("Waves crashing into this biome's cliffs throw spray about this many blocks up. 0 = none.")]
    [Min(0)] public float waveSpray = 0f;

    [Header("Trees (grow on flat surface blocks above water)")]
    public List<TreeType> trees = new();

    [Header("Plants (grow on the surface block above water, where nothing else is)")]
    public List<PlantType> plants = new();

    [Header("Rock Strata (empty = use Deep Block)")]
    [Tooltip("Horizontal bands of rock, repeated going up. Visible on cliff faces.")]
    [BlockName] public string[] strataBlocks = new string[0];
    [Min(1)] public int strataThickness = 3;
    public float strataWarpScale = 0.02f;
    [Tooltip("How many blocks the bands wobble up and down.")]
    public float strataWarpAmount = 0f;

    // Block names resolved to IDs by ResolveBlocks, so generation doesn't do string lookups per voxel
    [System.NonSerialized] byte surfaceId, subSurfaceId, deepId, bottomId, peakId, underwaterId;
    [System.NonSerialized] byte[] strataIds = new byte[0];
    [System.NonSerialized] public byte waterSurfaceId;
    /// <summary>Which BuildAtlas tint this biome's grass uses, 0 = untinted. Set by TerrainGenerator.</summary>
    [System.NonSerialized] public byte tintIndex;

    public Vector2 Climate => new(temperature, humidity);

    public byte SurfaceId => surfaceId;

    public void ResolveBlocks(BlockDatabase db)
    {
        surfaceId = db.GetId(surfaceBlock, this);
        subSurfaceId = db.GetId(subSurfaceBlock, this);
        deepId = db.GetId(deepBlock, this);
        bottomId = db.GetId(bottomBlock, this);
        peakId = db.GetId(peakBlock, this);
        underwaterId = string.IsNullOrWhiteSpace(underwaterSurfaceBlock) ? subSurfaceId : db.GetId(underwaterSurfaceBlock, this);
        waterSurfaceId = db.GetId(waterSurfaceBlock, this);
        foreach (TreeType tree in trees)
        {
            if (tree == null) continue;
            tree.logId = tree.chance > 0f ? db.GetId(tree.log, this) : (byte)0;
            tree.leavesId = tree.chance > 0f ? db.GetId(tree.leaves, this) : (byte)0;
        }
        foreach (PlantType plant in plants)
        {
            if (plant == null) continue;
            plant.id = plant.chance > 0f ? db.GetId(plant.block, this) : (byte)0;
            if (plant.id != 0 && !(db.Get(plant.id)?.plant ?? false))
                Debug.LogWarning($"Biome {name} grows '{plant.block}', which doesn't have Plant ticked in the Block Database. " +
                                 "It will be a full block.", this);
        }

        strataIds = new byte[strataBlocks?.Length ?? 0];
        for (int i = 0; i < strataIds.Length; i++)
            strataIds[i] = db.GetId(strataBlocks[i], this);
    }

    public float SampleHeight(int wx, int wz, Vector2 heightOffset, Vector2 plateauOffset, Vector2 mountainOffset)
    {
        float n = VoxelNoise.FBm(wx + heightOffset.x, wz + heightOffset.y, noiseScale, octaves, persistence, lacunarity, ridged);
        float h = baseHeight + heightCurve.Evaluate(n) * heightScale;

        if (mountainHeight > 0f)
        {
            // Broad noise picks where ranges go, ridged noise shapes the peaks within them
            float area = VoxelNoise.FBm(wx + mountainOffset.x, wz + mountainOffset.y, mountainNoiseScale * 0.35f, 2, 0.5f, 2f);
            float threshold = 0.5f + (0.5f - mountainCoverage) * 0.4f;
            float range = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((area - threshold) / 0.1f));
            if (range > 0f)
            {
                float ridges = VoxelNoise.FBm(wx + mountainOffset.y, wz + mountainOffset.x, mountainNoiseScale, 4, 0.5f, 2f, true);
                ridges = Mathf.Clamp01((ridges - 0.3f) / 0.6f);
                h += mountainHeight * range * (0.2f + 0.8f * Mathf.Pow(ridges, mountainSharpness));
            }
        }

        if (plateauHeight > 0f)
        {
            float p = VoxelNoise.FBm(wx + plateauOffset.x, wz + plateauOffset.y, plateauNoiseScale, 2, 0.5f, 2f);
            float ramp = Mathf.Clamp01((p - plateauThreshold) / plateauEdgeWidth);
            if (plateauSteps > 0) ramp = Mathf.Floor(ramp * plateauSteps) / plateauSteps;
            h += ramp * plateauHeight;
        }

        if (duneHeight > 0f)
        {
            float a = duneWind * Mathf.Deg2Rad, cos = Mathf.Cos(a), sin = Mathf.Sin(a);
            float along = (wx * cos + wz * sin) / duneSpacing;
            float across = (wz * cos - wx * sin) / duneSpacing;
            // Crest lines bend and wander instead of running dead straight
            along += (Mathf.PerlinNoise(across * 0.35f + mountainOffset.x, along * 0.2f + mountainOffset.y) - 0.5f) * 1.6f;
            float f = along - Mathf.Floor(along);
            // Long gentle rise on the windward side, short steep slip face behind the crest
            float profile = Mathf.SmoothStep(0f, 1f, f < 0.75f ? f / 0.75f : (1f - f) / 0.25f);
            float size = Mathf.PerlinNoise(across * 0.5f + mountainOffset.y, along * 0.15f + mountainOffset.x);
            h += duneHeight * profile * Mathf.Lerp(0.35f, 1f, size);
        }

        if (terraceStep > 1)
            h = Mathf.Lerp(h, Mathf.Floor(h / terraceStep) * terraceStep, terraceStrength);

        return h;
    }

    public int GetStrataShift(int wx, int wz, Vector2 offset)
    {
        if (strataWarpAmount <= 0f) return 0;
        float n = Mathf.PerlinNoise((wx + offset.x) * strataWarpScale, (wz + offset.y) * strataWarpScale);
        return Mathf.RoundToInt((n - 0.5f) * 2f * strataWarpAmount);
    }

    public byte GetBlock(int y, int surfaceY, int strataShift, int seaLevel)
    {
        if (y > surfaceY) return 0;

        int depth = surfaceY - y;
        if (depth == 0)
        {
            if (surfaceY < seaLevel) return underwaterId;
            return peakHeight > 0 && surfaceY >= peakHeight ? peakId : surfaceId;
        }
        if (depth <= subSurfaceDepth) return subSurfaceId;
        if (bottomId != 0 && depth > bottomDepth) return bottomId;

        if (strataIds.Length > 0)
        {
            int band = Mathf.FloorToInt((y + strataShift) / (float)strataThickness);
            int len = strataIds.Length;
            return strataIds[((band % len) + len) % len];
        }
        return deepId;
    }
}
