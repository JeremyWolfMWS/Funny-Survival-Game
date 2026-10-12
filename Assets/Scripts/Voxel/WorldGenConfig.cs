using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "WorldGenConfig", menuName = "Voxel/World Gen Config")]
public class WorldGenConfig : ScriptableObject
{
    public BlockDatabase blockDatabase;

    [Tooltip("Climate biomes are placed at their (temperature, humidity) point, and the closest one to the local " +
             "climate wins. Region biomes are laid over them in rare patches. Coast biomes show up on the shores of " +
             "biomes that list them, whether or not they're in this list.")]
    public List<BiomeConfig> biomes = new();

    [Header("Climate")]
    [Tooltip("Smaller = bigger biomes.")]
    public float climateNoiseScale = 0.003f;
    [Tooltip("The climate is evened out so every biome gets its fair share. Above 1 favors biomes near the 0/1 " +
             "extremes, below 1 the ones near the middle.")]
    public float climateContrast = 1f;
    [Tooltip("Width of biome transitions in climate space. Bigger = smoother, longer blends.")]
    [Range(0.01f, 1f)] public float blendWidth = 0.2f;

    [Header("Water")]
    [Tooltip("Terrain below this Y is filled with water up to it. 0 = no oceans or lakes.")]
    public int seaLevel = 30;

    [Header("Oceans and Coasts")]
    [Tooltip("Open seas between the land, with beaches and other coast biomes along their shores.")]
    public bool oceans = true;
    [Tooltip("Smaller = bigger continents and oceans.")]
    public float oceanScale = 0.0012f;
    [Tooltip("About how much of the world is sea. Biomes push it up or down with their Ocean Bias.")]
    [Range(0f, 0.9f)] public float oceanCoverage = 0.3f;
    [Tooltip("How far the open sea floor is below sea level.")]
    [Min(1)] public float oceanDepth = 16f;
    [Tooltip("Blocks from the waterline out to full ocean depth.")]
    [Min(2)] public float shelfWidth = 40f;
    [Tooltip("Blocks inland over which the land climbs from the beach up to its normal height.")]
    [Min(2)] public float shoreRamp = 32f;
    [Tooltip("Coast biomes come in stretches of shore about this long, each picked by weight.")]
    [Min(16)] public float coastStretch = 90f;

    [Header("Slopes")]
    [Tooltip("Passes that fill in spots too steep for slope blocks to follow, which would otherwise leave triangular " +
             "notches between slopes. Only where slopes are on. 0 = off.")]
    [Range(0, TerrainGenerator.MaxSlopeSmoothing)] public int slopeSmoothing = TerrainGenerator.MaxSlopeSmoothing;

    [Header("Ores")]
    [Tooltip("Ores, their blocks, drops and where they spawn.")]
    public OreDatabase oreManager;

    [Header("Caves")]
    [Tooltip("Winding tunnels underground. They never break through the sea floor.")]
    public bool caves = true;
    [Tooltip("How much of the world has tunnels. They come in clusters with solid ground between them, and thin out " +
             "toward the cluster edges. Each biome scales this with its own Caves setting.")]
    [Range(0f, 1f)] public float caveAmount = 0.3f;
    [Tooltip("Smaller = longer, lazier tunnels.")]
    public float caveScale = 0.03f;
    [Tooltip("Tunnel width. 0.07 is about 4 blocks wide and 3 tall.")]
    [Range(0.02f, 0.3f)] public float caveWidth = 0.07f;
    [Tooltip("Tunnels stay below this Y.")]
    public int caveMaxY = 80;
    [Tooltip("Tunnels may cut through the surface, leaving cave mouths on land. Elsewhere they stay a few blocks down.")]
    public bool caveEntrances = true;
    [Tooltip("Share of tunnels near the surface that open up into a cave mouth.")]
    [Range(0f, 1f)] public float caveEntranceChance = 0.1f;

    [Tooltip("Big open chambers deep underground.")]
    public bool caverns = true;
    [Tooltip("Smaller = bigger chambers, further apart.")]
    public float cavernScale = 0.02f;
    [Tooltip("How much of the deep underground is open cavern.")]
    [Range(0f, 1f)] public float cavernSize = 0.2f;
    [Tooltip("Caverns stay below this Y.")]
    public int cavernMaxY = 24;
    [Tooltip("Caverns stay at least this many blocks under the surface.")]
    [Min(6)] public int cavernMinDepth = 20;

    [Header("Debug")]
    [Tooltip("If set, the whole world uses only this biome. Handy while designing one.")]
    public BiomeConfig forceBiome;
}
