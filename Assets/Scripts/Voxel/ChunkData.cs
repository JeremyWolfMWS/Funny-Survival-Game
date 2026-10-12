using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// A chunk's blocks, shapes and water, plus a 1-block border from neighboring chunks so faces on chunk edges can be
/// culled without neighbor lookups. Generation touches no Unity objects, so it runs on worker threads.
/// </summary>
public class ChunkData
{
    public const int Size = Chunk.Size;
    public const int Height = Chunk.Height;
    public const int Padded = Size + 2;

    public readonly Vector2Int coord;
    public readonly byte[] voxels = new byte[Padded * Padded * Height];
    /// <summary>Shape per voxel (see VoxelShapes), 0 = cube.</summary>
    public readonly byte[] shapes = new byte[Padded * Padded * Height];
    /// <summary>Water per voxel (see Fluid), 0 = none.</summary>
    public readonly byte[] fluids = new byte[Padded * Padded * Height];
    /// <summary>Grass tint per column (px + pz * Padded), see BlockDatabase.GetUVRect.</summary>
    public readonly byte[] tints = new byte[Padded * Padded];

    // Columns sampled during generation: the padded area plus the extra reach slopes, trees and smoothing need
    const int MaxGridSize = Padded + 2 * (TerrainGenerator.ColumnReach + TerrainGenerator.MaxSlopeSmoothing);
    [System.ThreadStatic] static VoxelColumn[] columnGrid;

    public ChunkData(Vector2Int coord) => this.coord = coord;

    public static int Index(int px, int y, int pz) => px + pz * Padded + y * Padded * Padded;

    public void Generate(TerrainGenerator gen, BlockDatabase db, List<KeyValuePair<Vector3Int, VoxelEdit>> edits)
    {
        columnGrid ??= new VoxelColumn[MaxGridSize * MaxGridSize];
        VoxelColumn[] grid = columnGrid;

        int sea = gen.SeaLevel;
        int ox = coord.x * Size - 1;
        int oz = coord.y * Size - 1;
        int margin = TerrainGenerator.ColumnReach + gen.SmoothingReach;
        int gridSize = Padded + 2 * margin;
        int gx = ox - margin;
        int gz = oz - margin;

        for (int iz = 0; iz < gridSize; iz++)
        for (int ix = 0; ix < gridSize; ix++)
            grid[ix + iz * gridSize] = gen.SampleColumn(gx + ix, gz + iz);
        gen.SmoothSlopes(grid, gridSize, gx, gz);

        VoxelColumn Column(int wx, int wz) => grid[(wx - gx) + (wz - gz) * gridSize];
        System.Func<int, int, VoxelColumn> column = Column;

        for (int pz = 0; pz < Padded; pz++)
        for (int px = 0; px < Padded; px++)
        {
            int wx = ox + px, wz = oz + pz;
            VoxelColumn col = Column(wx, wz);
            tints[px + pz * Padded] = col.tint;
            CaveColumn cave = gen.GetCaveColumn(wx, wz, col);
            for (int y = 0; y <= col.height; y++)
                voxels[Index(px, y, pz)] = gen.IsCave(wx, y, wz, cave) ? (byte)0 : gen.ApplyOres(wx, y, wz, col.GetBlock(y), col.biome);

            for (int y = col.height + 1; y <= sea && y < Height; y++)
                fluids[Index(px, y, pz)] = Fluid.Source;

            int slopeY = col.height + 1;
            // No slope over a cave mouth, it would float
            bool topCarved = voxels[Index(px, col.height, pz)] == 0;
            if (!topCarved && gen.TryGetSlope(wx, wz, column, out byte shape))
            {
                byte block = col.GetBlock(col.height);
                // Under water a slope keeps the water if it can, otherwise the water wins
                if (slopeY > sea || (db.Get(block)?.waterLoggable ?? false))
                {
                    int i = Index(px, slopeY, pz);
                    voxels[i] = block;
                    shapes[i] = shape;
                }
            }

            if (col.height < sea && sea < Height && col.biome.waterSurfaceId != 0)
            {
                int i = Index(px, sea, pz);
                voxels[i] = col.biome.waterSurfaceId;
                shapes[i] = VoxelShapes.Cube;
                fluids[i] = Fluid.None;
            }
        }

        PlaceTrees(gen, column, ox, oz);
        PlacePlants(gen, column, ox, oz);

        if (edits != null)
            foreach (var kv in edits)
                Set(kv.Key, kv.Value);
    }

    [System.ThreadStatic] static bool[] treeLeaves;

    // Trees rooted up to TreeRadius outside the padded area can still reach into it with leaves
    void PlaceTrees(TerrainGenerator gen, System.Func<int, int, VoxelColumn> column, int ox, int oz)
    {
        treeLeaves ??= new bool[Padded * Padded * Height];
        bool[] isLeaf = treeLeaves;
        System.Array.Clear(isLeaf, 0, isLeaf.Length);

        int rootX = 0, rootZ = 0;
        byte logId = 0, leavesId = 0;
        // Leaves only fill air and wood only replaces air and leaves, so overlapping trees come out the same in
        // every chunk whatever order they're built in
        TerrainGenerator.TreeBlockSink place = (dx, y, dz, log) =>
        {
            int px = rootX + dx - ox, pz = rootZ + dz - oz;
            if (px < 0 || px >= Padded || pz < 0 || pz >= Padded || y < 0 || y >= Height) return;
            int i = Index(px, y, pz);
            if (log)
            {
                if (voxels[i] != 0 && !isLeaf[i]) return;
                voxels[i] = logId;
                shapes[i] = VoxelShapes.Cube;
                isLeaf[i] = false;
            }
            else
            {
                if (voxels[i] != 0 || leavesId == 0) return;
                voxels[i] = leavesId;
                isLeaf[i] = true;
            }
            fluids[i] = Fluid.None;
        };

        int r = TerrainGenerator.TreeRadius;
        for (int tz = oz - r; tz < oz + Padded + r; tz++)
        for (int tx = ox - r; tx < ox + Padded + r; tx++)
        {
            if (!gen.TryGetTree(tx, tz, column, out TreeType tree, out int trunk)) continue;
            rootX = tx;
            rootZ = tz;
            logId = tree.logId;
            leavesId = tree.leavesId;
            gen.BuildTree(tx, tz, column(tx, tz).height, tree, trunk, place);
        }
    }

    // Plants only grow on the bare surface block: not under a slope or tree, in water, on snow caps or over a cave mouth
    void PlacePlants(TerrainGenerator gen, System.Func<int, int, VoxelColumn> column, int ox, int oz)
    {
        for (int pz = 0; pz < Padded; pz++)
        for (int px = 0; px < Padded; px++)
        {
            VoxelColumn c = column(ox + px, oz + pz);
            int y = c.height + 1;
            if (y >= Height) continue;
            int i = Index(px, y, pz);
            if (voxels[i] != 0 || fluids[i] != Fluid.None || voxels[Index(px, c.height, pz)] != c.biome.SurfaceId) continue;
            byte plant = gen.GetPlant(ox + px, oz + pz, c);
            if (plant != 0) voxels[i] = plant;
        }
    }

    public bool Contains(Vector3Int worldPos, out int index)
    {
        int px = worldPos.x - coord.x * Size + 1;
        int pz = worldPos.z - coord.y * Size + 1;
        index = 0;
        if (px < 0 || px >= Padded || pz < 0 || pz >= Padded || worldPos.y < 0 || worldPos.y >= Height) return false;
        index = Index(px, worldPos.y, pz);
        return true;
    }

    public VoxelEdit Get(Vector3Int worldPos) =>
        Contains(worldPos, out int i) ? new VoxelEdit { id = voxels[i], shape = shapes[i], fluid = fluids[i] } : default;

    public void Set(Vector3Int worldPos, VoxelEdit voxel)
    {
        if (!Contains(worldPos, out int i)) return;
        voxels[i] = voxel.id;
        shapes[i] = voxel.id == 0 ? VoxelShapes.Cube : voxel.shape;
        fluids[i] = voxel.fluid;
    }
}
