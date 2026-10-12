using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Minecraft Alpha style brightness: a fixed shade per face direction times a light level (0-15), turned into vertex
/// colors. Built on the main thread, read from worker threads.
/// </summary>
public class LightTable
{
    public const int MaxLight = 15;

    readonly Color[] levels = new Color[MaxLight + 1];
    readonly float gamma;

    /// <param name="darkest">Brightness at light level 0.</param>
    /// <param name="linear">The project renders in linear color space, so colors must be converted from gamma.</param>
    /// <param name="sunlit">Tint in full sunlight.</param>
    /// <param name="shade">Tint in the dark, blended toward sunlit as it gets brighter.</param>
    public LightTable(float darkest, bool linear, Color sunlit, Color shade)
    {
        gamma = linear ? 2.2f : 1f;
        for (int i = 0; i <= MaxLight; i++)
        {
            // Minecraft's curve: drops off slowly near full light, then steeply into the dark
            float f = 1f - i / (float)MaxLight;
            float b = (1f - f) / (f * 3f + 1f);
            Color c = Color.Lerp(shade, sunlit, b) * (b * (1f - darkest) + darkest);
            levels[i] = new Color(Mathf.Pow(c.r, gamma), Mathf.Pow(c.g, gamma), Mathf.Pow(c.b, gamma), 1f);
        }
    }

    /// <summary>Top faces 100%, north/south 80%, east/west 60%, bottom 50%, blended for sloped faces.</summary>
    public static float FaceShade(Vector3 n) =>
        n.x * n.x * 0.6f + n.z * n.z * 0.8f + n.y * n.y * (n.y > 0f ? 1f : 0.5f);

    public Color Get(float shade, int light)
    {
        float s = Mathf.Pow(shade, gamma);
        Color c = levels[Mathf.Clamp(light, 0, MaxLight)];
        return new Color(c.r * s, c.g * s, c.b * s, 1f);
    }
}

/// <summary>
/// Sunlight for a chunk's padded area: full strength straight down from the sky, then spreading sideways and into
/// caves losing 1 level per block. Light only spreads within the chunk and its 1-block border, so it can stop short
/// at a chunk edge deep in a cave. One instance per thread at a time.
/// </summary>
public class ChunkLight
{
    const int Padded = ChunkData.Padded, Height = ChunkData.Height, Layer = Padded * Padded;

    readonly byte[] light = new byte[Layer * Height];
    readonly List<int>[] buckets = new List<int>[LightTable.MaxLight + 1];

    public ChunkLight()
    {
        for (int i = 0; i < buckets.Length; i++) buckets[i] = new List<int>();
    }

    /// <summary>Light level in a padded cell. Above the world is full sunlight, below it dark.</summary>
    public int Get(int px, int y, int pz)
    {
        if (y >= Height) return LightTable.MaxLight;
        if (y < 0) return 0;
        return light[ChunkData.Index(px, y, pz)];
    }

    // Slopes and plants only fill part of their cell and cutout blocks have holes, so light passes through them and
    // lights their own surface
    static bool LetsLightThrough(byte id, byte shape, BlockDatabase db) => id == 0 || db.IsSeeThrough(id) || !VoxelShapes.IsCube(shape);

    public void Compute(ChunkData data, BlockDatabase db)
    {
        byte[] voxels = data.voxels, shapes = data.shapes, fluids = data.fluids;
        System.Array.Clear(light, 0, light.Length);
        foreach (List<int> b in buckets) b.Clear();

        for (int pz = 0; pz < Padded; pz++)
        for (int px = 0; px < Padded; px++)
        {
            int level = LightTable.MaxLight;
            for (int y = Height - 1; y >= 0; y--)
            {
                int i = ChunkData.Index(px, y, pz);
                if (!LetsLightThrough(voxels[i], shapes[i], db)) break;
                // Water and leaves dim the light going down, like in Minecraft
                if (fluids[i] != Fluid.None) level = Mathf.Max(0, level - 2);
                else if (db.IsCutout(voxels[i])) level = Mathf.Max(0, level - 1);
                light[i] = (byte)level;
                if (level > 1) buckets[level].Add(i);
            }
        }

        // Brightest first, so every cell gets its final level the first time it's reached
        for (int level = LightTable.MaxLight; level > 1; level--)
        {
            List<int> bucket = buckets[level];
            for (int k = 0; k < bucket.Count; k++)
            {
                int i = bucket[k];
                if (light[i] != level) continue;
                int px = i % Padded, pz = i / Padded % Padded, y = i / Layer;
                if (px > 0) Spread(i - 1, level, voxels, shapes, fluids, db);
                if (px < Padded - 1) Spread(i + 1, level, voxels, shapes, fluids, db);
                if (pz > 0) Spread(i - Padded, level, voxels, shapes, fluids, db);
                if (pz < Padded - 1) Spread(i + Padded, level, voxels, shapes, fluids, db);
                if (y > 0) Spread(i - Layer, level, voxels, shapes, fluids, db);
                if (y < Height - 1) Spread(i + Layer, level, voxels, shapes, fluids, db);
            }
        }
    }

    void Spread(int n, int level, byte[] voxels, byte[] shapes, byte[] fluids, BlockDatabase db)
    {
        int next = level - (fluids[n] != Fluid.None ? 3 : db.IsCutout(voxels[n]) ? 2 : 1);
        if (next <= light[n] || next <= 0 || !LetsLightThrough(voxels[n], shapes[n], db)) return;
        light[n] = (byte)next;
        buckets[next].Add(n);
    }
}
