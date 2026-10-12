using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Vertex lists for a chunk's terrain and water. Building touches no Unity objects, so it runs on worker threads;
/// the lists are uploaded to meshes on the main thread. One instance per thread at a time.
/// </summary>
public class ChunkMesh
{
    const int Size = ChunkData.Size, Height = ChunkData.Height;

    public readonly List<Vector3> verts = new();
    public readonly List<Vector3> normals = new();
    public readonly List<Vector2> uvs = new();
    public readonly List<Color> colors = new();
    public readonly List<int> tris = new();
    public readonly List<Vector3> waterVerts = new();
    public readonly List<Vector3> waterNormals = new();
    public readonly List<Vector2> waterUvs = new();
    public readonly List<Color> waterColors = new();
    public readonly List<int> waterTris = new();
    public readonly List<Vector3> plantVerts = new();
    public readonly List<Vector3> plantNormals = new();
    public readonly List<Vector2> plantUvs = new();
    public readonly List<Color> plantColors = new();
    public readonly List<int> plantTris = new();

    readonly Vector3[] faceVerts = new Vector3[4];
    readonly ChunkLight light = new();
    LightTable lightTable;

    // Order: +X, -X, +Y, -Y, +Z, -Z
    static readonly Vector3Int[] FaceDirs =
    {
        Vector3Int.right, Vector3Int.left, Vector3Int.up, Vector3Int.down, Vector3Int.forward, Vector3Int.back
    };

    // Corners per face in bottom-left, top-left, top-right, bottom-right order (clockwise seen from outside)
    static readonly Vector3[,] FaceCorners =
    {
        { new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1) },
        { new(0, 0, 1), new(0, 1, 1), new(0, 1, 0), new(0, 0, 0) },
        { new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0) },
        { new(1, 0, 0), new(1, 0, 1), new(0, 0, 1), new(0, 0, 0) },
        { new(1, 0, 1), new(1, 1, 1), new(0, 1, 1), new(0, 0, 1) },
        { new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0) },
    };

    static readonly Vector2[] CornerUVs = { new(0, 0), new(0, 1), new(1, 1), new(1, 0) };

    static int Index(int px, int y, int pz) => ChunkData.Index(px, y, pz);

    public void Clear()
    {
        verts.Clear(); normals.Clear(); uvs.Clear(); colors.Clear(); tris.Clear();
        waterVerts.Clear(); waterNormals.Clear(); waterUvs.Clear(); waterColors.Clear(); waterTris.Clear();
        plantVerts.Clear(); plantNormals.Clear(); plantUvs.Clear(); plantColors.Clear(); plantTris.Clear();
    }

    /// <param name="lighting">Classic face shading and sunlight as vertex colors. Null = all white.</param>
    public void Build(ChunkData data, BlockDatabase db, LightTable lighting)
    {
        Clear();
        byte[] voxels = data.voxels, shapes = data.shapes, fluids = data.fluids;
        lightTable = lighting;
        if (lighting != null) light.Compute(data, db);

        for (int y = 0; y < Height; y++)
        for (int pz = 1; pz <= Size; pz++)
        for (int px = 1; px <= Size; px++)
        {
            int index = Index(px, y, pz);
            byte id = voxels[index];
            byte shape = shapes[index];
            if (fluids[index] != Fluid.None && (id == 0 || !VoxelShapes.IsCube(shape)))
                AddWater(data, db, px, y, pz);
            if (id == 0) continue;

            byte tint = data.tints[px + pz * ChunkData.Padded];
            if (db.IsPlant(id))
            {
                AddPlant(new Vector3(px - 1, y, pz - 1), db.GetUVRect(id, BlockDatabase.FaceSide, tint), Shade(1f, px, y, pz));
                continue;
            }

            for (int f = 0; f < 6; f++)
            {
                Vector3Int d = FaceDirs[f];
                int ny = y + d.y;
                if (ny < 0) continue;
                if (ny < Height)
                {
                    int n = Index(px + d.x, ny, pz + d.z);
                    // Faces behind see-through blocks still show, leaves next to leaves included
                    if (voxels[n] != 0 && !db.IsSeeThrough(voxels[n]) && IsFaceHidden(f, shape, shapes[n])) continue;
                }

                bool vertical = f != 2 && f != 3;
                for (int c = 0; c < 4; c++)
                {
                    Vector3 p = FaceCorners[f, c];
                    if (p.y > 0f) p.y = VoxelShapes.CornerHeight(shape, (int)p.x, (int)p.z);
                    faceVerts[c] = p;
                }
                // Low side of a slope has no wall
                if (vertical && faceVerts[1].y <= 0f && faceVerts[2].y <= 0f) continue;

                int faceType = f == 2 ? BlockDatabase.FaceTop : f == 3 ? BlockDatabase.FaceBottom : BlockDatabase.FaceSide;
                Rect uv = db.GetUVRect(id, faceType, tint);
                var origin = new Vector3(px - 1, y, pz - 1);

                if (f == 2 && !VoxelShapes.IsCube(shape))
                {
                    // A sloped top is inside its own cell, which lets light in
                    int topLight = Light(px, y, pz);
                    // faceVerts order on top: (0,0), (0,1), (1,1), (1,0)
                    if (VoxelShapes.SplitsAlongMainDiagonal(shape))
                    {
                        AddTopTriangle(origin, uv, topLight, 0, 1, 2);
                        AddTopTriangle(origin, uv, topLight, 0, 2, 3);
                    }
                    else
                    {
                        AddTopTriangle(origin, uv, topLight, 0, 1, 3);
                        AddTopTriangle(origin, uv, topLight, 1, 2, 3);
                    }
                    continue;
                }

                // Faces are lit by the cell they face
                Color color = Shade(LightTable.FaceShade(d), px + d.x, y + d.y, pz + d.z);
                int start = verts.Count;
                for (int c = 0; c < 4; c++)
                {
                    // Walls map texture V to height so partial walls show the matching part of the texture
                    Vector2 corner = vertical ? new Vector2(CornerUVs[c].x, faceVerts[c].y) : CornerUVs[c];
                    verts.Add(origin + faceVerts[c]);
                    normals.Add(d);
                    uvs.Add(uv.min + Vector2.Scale(corner, uv.size));
                    colors.Add(color);
                }

                // Walls of a slope are triangles, so one half of the quad can be degenerate
                if (faceVerts[1] != faceVerts[0]) { tris.Add(start); tris.Add(start + 1); tris.Add(start + 2); }
                if (faceVerts[2] != faceVerts[3]) { tris.Add(start); tris.Add(start + 2); tris.Add(start + 3); }
            }
        }
    }

    void AddWater(ChunkData data, BlockDatabase db, int px, int y, int pz)
    {
        byte[] voxels = data.voxels, shapes = data.shapes, fluids = data.fluids;
        int index = Index(px, y, pz);
        bool waterAbove = y + 1 < Height && fluids[Index(px, y + 1, pz)] != Fluid.None;
        float h = Fluid.SurfaceHeight(fluids[index], waterAbove);
        Rect uv = db.WaterUVRect;
        var origin = new Vector3(px - 1, y, pz - 1);

        for (int f = 0; f < 6; f++)
        {
            Vector3Int d = FaceDirs[f];
            int ny = y + d.y;
            if (ny < 0 || ny >= Height) continue;
            int n = Index(px + d.x, ny, pz + d.z);
            bool solidNeighbor = voxels[n] != 0 && VoxelShapes.IsCube(shapes[n]) && !db.IsSeeThrough(voxels[n]);

            float bottom = 0f, top = h;
            if (f == 2)
            {
                if (waterAbove || solidNeighbor) continue;
            }
            else if (f == 3)
            {
                if (fluids[n] != Fluid.None || solidNeighbor) continue;
            }
            else
            {
                if (solidNeighbor) continue;
                bool nAbove = fluids[Index(px + d.x, y + 1, pz + d.z)] != Fluid.None;
                bottom = Fluid.SurfaceHeight(fluids[n], nAbove);
                if (bottom >= top) continue;
            }

            for (int c = 0; c < 4; c++)
            {
                Vector3 p = FaceCorners[f, c];
                p.y = f == 2 ? top : f == 3 ? 0f : (c == 1 || c == 2 ? top : bottom);
                // Pull walls in a hair so slope walls in waterlogged cells draw in front instead of z-fighting
                faceVerts[c] = p - (Vector3)d * 0.001f;
            }

            Color color = Shade(LightTable.FaceShade(d), px, y, pz);
            int start = waterVerts.Count;
            for (int c = 0; c < 4; c++)
            {
                Vector3 p = faceVerts[c];
                Vector2 corner = f == 2 || f == 3 ? CornerUVs[c] : new Vector2(CornerUVs[c].x, p.y);
                waterVerts.Add(origin + p);
                waterNormals.Add(d);
                waterUvs.Add(uv.min + Vector2.Scale(corner, uv.size));
                waterColors.Add(color);
            }
            waterTris.Add(start); waterTris.Add(start + 1); waterTris.Add(start + 2);
            waterTris.Add(start); waterTris.Add(start + 2); waterTris.Add(start + 3);
        }
    }

    // Two planes crossing corner to corner. The plant material draws both sides; normals point up so plants are lit
    // like the ground they stand on.
    void AddPlant(Vector3 origin, Rect uv, Color color)
    {
        const float a = 0.15f, b = 0.85f;
        AddPlantQuad(origin, uv, color, new Vector3(a, 0f, a), new Vector3(b, 0f, b));
        AddPlantQuad(origin, uv, color, new Vector3(a, 0f, b), new Vector3(b, 0f, a));
    }

    void AddPlantQuad(Vector3 origin, Rect uv, Color color, Vector3 from, Vector3 to)
    {
        int start = plantVerts.Count;
        plantVerts.Add(origin + from);
        plantVerts.Add(origin + from + Vector3.up);
        plantVerts.Add(origin + to + Vector3.up);
        plantVerts.Add(origin + to);
        for (int c = 0; c < 4; c++)
        {
            plantNormals.Add(Vector3.up);
            plantUvs.Add(uv.min + Vector2.Scale(CornerUVs[c], uv.size));
            plantColors.Add(color);
        }
        plantTris.Add(start); plantTris.Add(start + 1); plantTris.Add(start + 2);
        plantTris.Add(start); plantTris.Add(start + 2); plantTris.Add(start + 3);
    }

    // Sloped tops get their own vertices per triangle so ridges and valleys stay flat shaded
    void AddTopTriangle(Vector3 origin, Rect uv, int lightLevel, int a, int b, int c)
    {
        Vector3 normal = Vector3.Cross(faceVerts[b] - faceVerts[a], faceVerts[c] - faceVerts[a]).normalized;
        Color color = lightTable != null ? lightTable.Get(LightTable.FaceShade(normal), lightLevel) : Color.white;
        int start = verts.Count;
        AddTopVertex(origin, uv, faceVerts[a], normal, color);
        AddTopVertex(origin, uv, faceVerts[b], normal, color);
        AddTopVertex(origin, uv, faceVerts[c], normal, color);
        tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
    }

    void AddTopVertex(Vector3 origin, Rect uv, Vector3 p, Vector3 normal, Color color)
    {
        verts.Add(origin + p);
        normals.Add(normal);
        uvs.Add(uv.min + Vector2.Scale(new Vector2(p.x, p.z), uv.size));
        colors.Add(color);
    }

    int Light(int px, int y, int pz) => lightTable != null ? light.Get(px, y, pz) : LightTable.MaxLight;

    Color Shade(float faceShade, int px, int y, int pz) =>
        lightTable != null ? lightTable.Get(faceShade, light.Get(px, y, pz)) : Color.white;

    /// <summary>Whether face f of a voxel is fully covered by the neighbor in that direction.</summary>
    static bool IsFaceHidden(int f, byte shape, byte neighborShape)
    {
        // Every shape has a full bottom, but only cubes have a full top
        if (f == 2) return VoxelShapes.IsCube(shape);
        if (f == 3) return VoxelShapes.IsCube(neighborShape);

        Vector3Int d = FaceDirs[f];
        for (int c = 1; c <= 2; c++)
        {
            Vector3 p = FaceCorners[f, c];
            int x = (int)p.x, z = (int)p.z;
            if (VoxelShapes.CornerHeight(neighborShape, x - d.x, z - d.z) < VoxelShapes.CornerHeight(shape, x, z))
                return false;
        }
        return true;
    }
}
