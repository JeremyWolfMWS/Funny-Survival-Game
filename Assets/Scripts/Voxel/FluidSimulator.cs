using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Flowing water. Sources never change on their own. Other water falls straight down, spreads sideways one level
/// weaker per block while resting on something, dries up when cut off, and two sources next to each other on solid
/// ground make a new source. Only cells that changed (or whose neighbors changed) are updated.
/// </summary>
public class FluidSimulator
{
    static readonly Vector3Int[] Sides =
    {
        Vector3Int.right, Vector3Int.left, new(0, 0, 1), new(0, 0, -1)
    };

    readonly VoxelWorld world;
    readonly HashSet<Vector3Int> pending = new();
    readonly List<Vector3Int> batch = new();
    readonly List<(Vector3Int pos, byte fluid)> changes = new();

    public FluidSimulator(VoxelWorld world) => this.world = world;

    public void ScheduleAround(Vector3Int p)
    {
        pending.Add(p);
        pending.Add(p + Vector3Int.up);
        pending.Add(p + Vector3Int.down);
        foreach (Vector3Int d in Sides) pending.Add(p + d);
    }

    public void Tick(int maxUpdates)
    {
        if (pending.Count == 0) return;

        batch.Clear();
        foreach (Vector3Int p in pending)
        {
            batch.Add(p);
            if (batch.Count >= maxUpdates) break;
        }
        foreach (Vector3Int p in batch) pending.Remove(p);

        // Decide everything first, then apply, so water moves one block per step in every direction
        changes.Clear();
        foreach (Vector3Int p in batch)
        {
            if (!world.IsBlockLoaded(p)) continue;
            byte current = world.GetFluid(p);
            byte next = Compute(p, current);
            if (next != current) changes.Add((p, next));
        }

        foreach (var (pos, fluid) in changes)
        {
            VoxelEdit v = world.GetVoxel(pos);
            v.fluid = fluid;
            world.SetVoxel(pos, v);
        }
    }

    byte Compute(Vector3Int p, byte current)
    {
        if (Fluid.IsSource(current)) return current;
        VoxelEdit self = world.GetVoxel(p);
        // Flowing water only lives in air; blocks only hold sources
        if (self.id != 0) return Fluid.None;

        if (world.GetFluid(p + Vector3Int.up) != Fluid.None) return Fluid.Flowing(Fluid.MaxLevel, true);

        int sources = 0, best = 0;
        foreach (Vector3Int d in Sides)
        {
            Vector3Int n = p + d;
            byte nf = world.GetFluid(n);
            if (nf == Fluid.None) continue;
            if (Fluid.IsSource(nf)) sources++;
            if (!RestsOnSomething(n)) continue;
            int level = Fluid.IsSource(nf) || Fluid.IsFalling(nf) ? Fluid.MaxLevel : Fluid.Level(nf);
            best = Mathf.Max(best, level - 1);
        }

        if (sources >= 2 && RestsOnSomething(p)) return Fluid.Source;
        return Fluid.Flowing(best, false);
    }

    bool RestsOnSomething(Vector3Int p)
    {
        if (p.y <= 0) return true;
        VoxelEdit below = world.GetVoxel(p + Vector3Int.down);
        return below.id != 0 || Fluid.IsSource(below.fluid);
    }
}
