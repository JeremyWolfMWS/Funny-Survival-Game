/// <summary>
/// Water stored per voxel, next to the block. 0 = no water. Low 4 bits = level 1-8 (8 = full), plus flags for
/// source blocks (permanent, bucket-able) and falling water. A voxel with a block and water is waterlogged.
/// </summary>
public static class Fluid
{
    public const byte None = 0;
    public const byte MaxLevel = 8;
    const byte SourceFlag = 0x80;
    const byte FallingFlag = 0x40;

    public const byte Source = SourceFlag | MaxLevel;

    public static byte Flowing(int level, bool falling) =>
        level <= 0 ? None : (byte)((falling ? FallingFlag : 0) | System.Math.Min(level, MaxLevel));

    public static bool IsSource(byte fluid) => (fluid & SourceFlag) != 0;

    public static bool IsFalling(byte fluid) => (fluid & FallingFlag) != 0;

    public static int Level(byte fluid) => fluid & 0x0F;

    /// <summary>Height of the water surface in the cell, 0-1. Water with water above it fills the cell.</summary>
    public static float SurfaceHeight(byte fluid, bool waterAbove) =>
        fluid == None ? 0f : waterAbove ? 1f : IsFalling(fluid) ? 0.875f : Level(fluid) / (float)MaxLevel * 0.875f;
}
