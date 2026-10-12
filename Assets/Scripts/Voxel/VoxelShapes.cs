using UnityEngine;

/// <summary>
/// Per-voxel shape. 0 = full cube. Every other shape is a full-height slope piece: solid below a surface through its
/// 4 top corners (each at height 0 or 1), split into two triangles along one of the diagonals. That covers straight
/// slopes, inner and outer corners and diagonal ramps. Corner index = x + 2 * z.
/// </summary>
public static class VoxelShapes
{
    public const byte Cube = 0;

    /// <summary>Directions a straight slope can rise toward: +X, -X, +Z, -Z.</summary>
    public static readonly Vector2Int[] Directions = { new(1, 0), new(-1, 0), new(0, 1), new(0, -1) };

    /// <summary>Diagonals a corner slope can point toward: +X+Z, -X+Z, -X-Z, +X-Z.</summary>
    public static readonly Vector2Int[] Diagonals = { new(1, 1), new(-1, 1), new(-1, -1), new(1, -1) };

    // 16 corner combinations, each split along the main (0,0)-(1,1) or the anti (1,0)-(0,1) diagonal
    const int CornerCombos = 16;
    const int ShapeCount = 1 + 2 * CornerCombos;

    static readonly float[] cornerHeights = new float[256 * 4];
    static readonly bool[] splitAlongMain = new bool[256];

    static VoxelShapes()
    {
        for (int shape = 0; shape < 256; shape++)
        {
            bool valid = shape != Cube && shape < ShapeCount;
            int code = valid ? (shape - 1) % CornerCombos : 15;
            for (int c = 0; c < 4; c++)
                cornerHeights[shape * 4 + c] = (code >> c) & 1;
            splitAlongMain[shape] = !valid || shape - 1 < CornerCombos;
        }
    }

    /// <summary>
    /// Shape from corner heights (0 or 1). All 1 is a cube. antiSplit picks the diagonal the top creases along; by
    /// default it runs through the odd corner, so outer corners form a ridge and inner corners a valley.
    /// </summary>
    public static byte FromCorners(int q00, int q10, int q01, int q11, bool? antiSplit = null)
    {
        if (q00 == 1 && q10 == 1 && q01 == 1 && q11 == 1) return Cube;

        bool anti;
        if (q00 + q11 == q10 + q01) anti = false; // planar, both splits look the same
        else if (antiSplit.HasValue) anti = antiSplit.Value;
        else
        {
            int main = System.Math.Abs(q00 - q11), other = System.Math.Abs(q10 - q01);
            anti = main != other ? other > main : q10 + q01 > q00 + q11;
        }
        return (byte)(1 + q00 + 2 * q10 + 4 * q01 + 8 * q11 + (anti ? CornerCombos : 0));
    }

    public static bool IsCube(byte shape) => shape == Cube || shape >= ShapeCount;

    /// <summary>Height of the solid top at a corner of the cell (x and z are 0 or 1).</summary>
    public static float CornerHeight(byte shape, int x, int z) => cornerHeights[shape * 4 + x + 2 * z];

    /// <summary>Whether the top surface is split along the (0,0)-(1,1) diagonal rather than (1,0)-(0,1).</summary>
    public static bool SplitsAlongMainDiagonal(byte shape) => splitAlongMain[shape];

    /// <summary>Height of the solid top at (x, z) inside the cell (0-1 each), following the two top triangles.</summary>
    public static float SurfaceHeight(byte shape, float x, float z)
    {
        if (IsCube(shape)) return 1f;
        float h00 = CornerHeight(shape, 0, 0), h10 = CornerHeight(shape, 1, 0);
        float h01 = CornerHeight(shape, 0, 1), h11 = CornerHeight(shape, 1, 1);
        if (SplitsAlongMainDiagonal(shape))
            return z >= x
                ? h00 + (h11 - h01) * x + (h01 - h00) * z
                : h00 + (h10 - h00) * x + (h11 - h10) * z;
        return x + z <= 1f
            ? h00 + (h10 - h00) * x + (h01 - h00) * z
            : h01 - h11 + h10 + (h11 - h01) * x + (h11 - h10) * z;
    }

    /// <summary>Index into Directions if the shape is a straight slope, otherwise -1.</summary>
    public static int StraightSlopeDirection(byte shape)
    {
        for (int d = 0; d < 4; d++)
            if (Slope(d) == shape) return d;
        return -1;
    }

    public static int DirectionIndex(Vector2Int dir) => System.Array.IndexOf(Directions, dir);

    public static int DiagonalIndex(Vector2Int diagonal) => System.Array.IndexOf(Diagonals, diagonal);

    // ---------- Presets ----------

    /// <summary>Straight slope rising toward Directions[direction].</summary>
    public static byte Slope(int direction)
    {
        Vector2Int d = Directions[direction];
        var q = new int[4];
        for (int z = 0; z < 2; z++)
        for (int x = 0; x < 2; x++)
            q[x + 2 * z] = (d.x != 0 ? x == (d.x > 0 ? 1 : 0) : z == (d.y > 0 ? 1 : 0)) ? 1 : 0;
        return FromCorners(q[0], q[1], q[2], q[3]);
    }

    /// <summary>Convex corner: only the corner toward Diagonals[diagonal] is high.</summary>
    public static byte OuterCorner(int diagonal) => Corner(diagonal, false);

    /// <summary>Concave corner: every corner except the one opposite Diagonals[diagonal] is high.</summary>
    public static byte InnerCorner(int diagonal) => Corner(diagonal, true);

    /// <summary>Rotates a shape by 90 degree steps around Y, matching Quaternion.Euler(0, 90 * quarterTurns, 0).</summary>
    public static byte Rotate(byte shape, int quarterTurns)
    {
        if (IsCube(shape)) return Cube;
        var q = new int[4];
        for (int c = 0; c < 4; c++) q[c] = (int)cornerHeights[shape * 4 + c];
        bool anti = !splitAlongMain[shape];

        for (int turn = 0; turn < ((quarterTurns % 4) + 4) % 4; turn++)
        {
            var rotated = new int[4];
            for (int z = 0; z < 2; z++)
            for (int x = 0; x < 2; x++)
                rotated[z + 2 * (1 - x)] = q[x + 2 * z];
            q = rotated;
            // A quarter turn maps one diagonal onto the other
            anti = !anti;
        }
        return FromCorners(q[0], q[1], q[2], q[3], anti);
    }

    static byte Corner(int diagonal, bool inner)
    {
        Vector2Int d = Diagonals[diagonal];
        var q = new int[4];
        for (int z = 0; z < 2; z++)
        for (int x = 0; x < 2; x++)
        {
            bool toward = x == (d.x > 0 ? 1 : 0) && z == (d.y > 0 ? 1 : 0);
            bool opposite = x == (d.x > 0 ? 0 : 1) && z == (d.y > 0 ? 0 : 1);
            q[x + 2 * z] = inner ? (opposite ? 0 : 1) : (toward ? 1 : 0);
        }
        return FromCorners(q[0], q[1], q[2], q[3]);
    }
}
