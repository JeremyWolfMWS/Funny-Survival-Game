using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Geometry of a single block shape (cube or slope) in the unit cell, used for item icons, dropped blocks and the
/// mining crack overlay. Chunks build the same faces with neighbor culling.
/// </summary>
public static class BlockShapeMesh
{
    public struct Vertex
    {
        public Vector3 position;
        public Vector3 normal;
        /// <summary>0-1 across the face. Walls use the height as V so partial walls show the matching texture part.</summary>
        public Vector2 uv;
        /// <summary>BlockDatabase.FaceTop / FaceSide / FaceBottom.</summary>
        public int faceType;
    }

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

    static readonly Dictionary<(byte id, byte shape), Mesh> blockMeshes = new();
    static readonly Dictionary<byte, Mesh> overlayMeshes = new();
    static readonly List<Vertex> scratch = new();

    /// <summary>Appends the triangles (3 vertices each) of every face of the shape.</summary>
    public static void Build(byte shape, List<Vertex> output)
    {
        var corners = new Vector3[4];
        for (int f = 0; f < 6; f++)
        {
            for (int c = 0; c < 4; c++)
            {
                Vector3 p = FaceCorners[f, c];
                if (p.y > 0f) p.y = VoxelShapes.CornerHeight(shape, (int)p.x, (int)p.z);
                corners[c] = p;
            }

            bool vertical = f != 2 && f != 3;
            // Low side of a slope has no wall
            if (vertical && corners[1].y <= 0f && corners[2].y <= 0f) continue;
            int faceType = f == 2 ? BlockDatabase.FaceTop : f == 3 ? BlockDatabase.FaceBottom : BlockDatabase.FaceSide;

            if (f == 2 && !VoxelShapes.IsCube(shape))
            {
                // Corner order on top: (0,0), (0,1), (1,1), (1,0)
                if (VoxelShapes.SplitsAlongMainDiagonal(shape))
                {
                    AddTopTriangle(output, corners, 0, 1, 2);
                    AddTopTriangle(output, corners, 0, 2, 3);
                }
                else
                {
                    AddTopTriangle(output, corners, 0, 1, 3);
                    AddTopTriangle(output, corners, 1, 2, 3);
                }
                continue;
            }

            Vertex V(int c) => new()
            {
                position = corners[c],
                normal = FaceDirs[f],
                uv = vertical ? new Vector2(CornerUVs[c].x, corners[c].y) : CornerUVs[c],
                faceType = faceType
            };
            // Walls of a slope are triangles, so one half of the quad can be degenerate
            if (corners[1] != corners[0]) { output.Add(V(0)); output.Add(V(1)); output.Add(V(2)); }
            if (corners[2] != corners[3]) { output.Add(V(0)); output.Add(V(2)); output.Add(V(3)); }
        }
    }

    static void AddTopTriangle(List<Vertex> output, Vector3[] corners, int a, int b, int c)
    {
        Vector3 normal = Vector3.Cross(corners[b] - corners[a], corners[c] - corners[a]).normalized;
        foreach (int i in new[] { a, b, c })
            output.Add(new Vertex
            {
                position = corners[i], normal = normal, uv = new Vector2(corners[i].x, corners[i].z),
                faceType = BlockDatabase.FaceTop
            });
    }

    /// <summary>
    /// A block shape textured from the block atlas, centered on its pivot, with classic face shading in its vertex
    /// colors if lighting is given. Cached until ClearCache; don't destroy.
    /// </summary>
    public static Mesh GetBlockMesh(BlockDatabase db, byte id, byte shape, LightTable lighting = null)
    {
        if (blockMeshes.TryGetValue((id, shape), out Mesh mesh) && mesh != null) return mesh;

        scratch.Clear();
        Build(shape, scratch);
        var verts = new List<Vector3>(scratch.Count);
        var normals = new List<Vector3>(scratch.Count);
        var uvs = new List<Vector2>(scratch.Count);
        var colors = new List<Color>(scratch.Count);
        var tris = new List<int>(scratch.Count);
        foreach (Vertex v in scratch)
        {
            Rect r = db.GetUVRect(id, v.faceType);
            tris.Add(verts.Count);
            verts.Add(v.position - Vector3.one * 0.5f);
            normals.Add(v.normal);
            uvs.Add(r.min + Vector2.Scale(v.uv, r.size));
            colors.Add(lighting != null ? lighting.Get(LightTable.FaceShade(v.normal), LightTable.MaxLight) : Color.white);
        }

        mesh = new Mesh { name = $"Block {id} shape {shape}" };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(colors);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        blockMeshes[(id, shape)] = mesh;
        return mesh;
    }

    /// <summary>
    /// The shape blown up a hair past the block, with 0-1 UVs on every face, for drawing cracks over a block in the
    /// world. Pivot at the block's minimum corner. Cached; don't destroy.
    /// </summary>
    public static Mesh GetOverlayMesh(byte shape)
    {
        if (overlayMeshes.TryGetValue(shape, out Mesh mesh) && mesh != null) return mesh;

        scratch.Clear();
        Build(shape, scratch);
        var verts = new List<Vector3>(scratch.Count);
        var uvs = new List<Vector2>(scratch.Count);
        var tris = new List<int>(scratch.Count);
        var center = Vector3.one * 0.5f;
        foreach (Vertex v in scratch)
        {
            tris.Add(verts.Count);
            verts.Add(center + (v.position - center) * 1.004f + v.normal * 0.002f);
            // Top faces of slopes would stretch a wall-style UV, so every face maps 0-1 by its own corners
            uvs.Add(v.uv);
        }

        mesh = new Mesh { name = $"Crack overlay shape {shape}" };
        mesh.SetVertices(verts);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        overlayMeshes[shape] = mesh;
        return mesh;
    }

    /// <summary>Forgets cached meshes, e.g. after the atlas is rebuilt.</summary>
    public static void ClearCache()
    {
        foreach (Mesh m in blockMeshes.Values) if (m != null) Object.Destroy(m);
        blockMeshes.Clear();
    }

    // ---------- Isometric icons ----------

    /// <summary>
    /// Renders an isometric picture of a block shape from the atlas pixels (top, left and right faces, shaded like
    /// an inventory block). Plants are drawn flat instead. The atlas must be CPU readable.
    /// </summary>
    public static Texture2D RenderIcon(BlockDatabase db, Color32[] atlas, int atlasWidth, int atlasHeight, byte id, byte shape, int size)
    {
        var pixels = new Color32[size * size];
        if (db.IsPlant(id)) DrawFlat(db.GetUVRect(id, BlockDatabase.FaceSide), atlas, atlasWidth, atlasHeight, pixels, size);
        else DrawIsometric(db, atlas, atlasWidth, atlasHeight, id, shape, pixels, size);

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = $"Icon {db.GetName(id)}",
            // Shown smaller than rendered, where point filtering would drop texels unevenly
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        return tex;
    }

    // Plants show their texture as is, see-through pixels included
    static void DrawFlat(Rect rect, Color32[] atlas, int atlasWidth, int atlasHeight, Color32[] pixels, int size)
    {
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int tx = Mathf.Clamp(Mathf.FloorToInt((rect.x + (x + 0.5f) / size * rect.width) * atlasWidth), 0, atlasWidth - 1);
            int ty = Mathf.Clamp(Mathf.FloorToInt((rect.y + (y + 0.5f) / size * rect.height) * atlasHeight), 0, atlasHeight - 1);
            pixels[x + y * size] = atlas[tx + ty * atlasWidth];
        }
    }

    static void DrawIsometric(BlockDatabase db, Color32[] atlas, int atlasWidth, int atlasHeight, byte id, byte shape, Color32[] pixels, int size)
    {
        scratch.Clear();
        Build(shape, scratch);

        var depth = new float[size * size];
        System.Array.Fill(depth, float.NegativeInfinity);

        float pad = size * 0.04f, s = size - 2f * pad;
        // Seen from the -X, +Y, -Z side: +X goes right and up, +Z left and up, +Y straight up
        Vector2 Project(Vector3 p) => new(
            pad + s * 0.5f + (p.x - p.z) * s * 0.5f,
            pad + p.y * s * 0.5f + (p.x + p.z) * s * 0.25f);
        float Nearness(Vector3 p) => p.y - p.x - p.z;
        var toViewer = new Vector3(-1f, 1f, -1f);

        for (int t = 0; t + 2 < scratch.Count; t += 3)
        {
            Vertex va = scratch[t], vb = scratch[t + 1], vc = scratch[t + 2];
            if (Vector3.Dot(va.normal, toViewer) <= 0.001f) continue;

            Vector2 a = Project(va.position), b = Project(vb.position), c = Project(vc.position);
            float area = Cross(b - a, c - a);
            if (Mathf.Abs(area) < 1e-5f) continue;

            Rect rect = db.GetUVRect(id, va.faceType);
            float shade = Mathf.Clamp(0.55f + 0.45f * va.normal.y - 0.2f * va.normal.x - 0.05f * va.normal.z, 0.4f, 1f);
            float za = Nearness(va.position), zb = Nearness(vb.position), zc = Nearness(vc.position);

            int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
            int maxX = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
            int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
            int maxY = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));

            for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                float w0 = Cross(b - p, c - p) / area, w1 = Cross(c - p, a - p) / area, w2 = 1f - w0 - w1;
                const float edge = -0.01f;
                if (w0 < edge || w1 < edge || w2 < edge) continue;

                int i = x + y * size;
                float z = w0 * za + w1 * zb + w2 * zc;
                if (z <= depth[i]) continue;

                Vector2 uv = w0 * va.uv + w1 * vb.uv + w2 * vc.uv;
                int tx = Mathf.Clamp(Mathf.FloorToInt((rect.x + Mathf.Clamp01(uv.x) * rect.width) * atlasWidth),
                    Mathf.CeilToInt(rect.xMin * atlasWidth), Mathf.FloorToInt(rect.xMax * atlasWidth) - 1);
                int ty = Mathf.Clamp(Mathf.FloorToInt((rect.y + Mathf.Clamp01(uv.y) * rect.height) * atlasHeight),
                    Mathf.CeilToInt(rect.yMin * atlasHeight), Mathf.FloorToInt(rect.yMax * atlasHeight) - 1);
                Color32 texel = atlas[Mathf.Clamp(tx, 0, atlasWidth - 1) + Mathf.Clamp(ty, 0, atlasHeight - 1) * atlasWidth];
                // Holes in leaves stay see-through
                if (texel.a < 128) continue;
                depth[i] = z;
                pixels[i] = new Color32((byte)(texel.r * shade), (byte)(texel.g * shade), (byte)(texel.b * shade), 255);
            }
        }
    }

    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

    /// <summary>A transparent unlit material, for overlays.</summary>
    public static Material CreateOverlayMaterial(string name)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
        if (shader == null) shader = Shader.Find("Unlit/Transparent");
        var mat = new Material(shader) { name = name };
        VoxelWorld.MakeTransparent(mat);
        mat.SetFloat("_Cull", (float)CullMode.Back);
        return mat;
    }
}
