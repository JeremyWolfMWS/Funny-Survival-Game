using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Boxy, Minecraft-style models put together in code: textured boxes hung on pivots. Textures tile once per block at
/// 16 pixels, like the world's blocks, so parts of any size keep the same pixel scale.
/// </summary>
public static class BlockyModel
{
    static readonly Dictionary<Vector3, Mesh> boxMeshes = new();
    static readonly Dictionary<Texture2D, Material> materials = new();

    /// <summary>An empty transform to rotate a group of parts around.</summary>
    public static Transform Pivot(Transform parent, string name, Vector3 localPosition)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = localPosition;
        return t;
    }

    /// <summary>A box of the given size centered at center, in the parent's space.</summary>
    public static MeshRenderer Box(Transform parent, string name, Vector3 center, Vector3 size, Material material)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = center;
        go.AddComponent<MeshFilter>().sharedMesh = BoxMesh(size);
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        return renderer;
    }

    /// <summary>A box mesh centered on its pivot, UVs in blocks so the texture repeats every block. Cached; don't destroy.</summary>
    public static Mesh BoxMesh(Vector3 size)
    {
        if (boxMeshes.TryGetValue(size, out Mesh mesh) && mesh != null) return mesh;

        Vector3 h = size * 0.5f;
        var verts = new List<Vector3>(24);
        var normals = new List<Vector3>(24);
        var uvs = new List<Vector2>(24);
        var tris = new List<int>(36);
        void Face(Vector3 normal, Vector3 right, Vector3 up, float width, float height, Vector2 offset)
        {
            int i = verts.Count;
            Vector3 c = Vector3.Scale(normal, h);
            Vector3 r = right * (width * 0.5f), u = up * (height * 0.5f);
            verts.Add(c - r - u); verts.Add(c - r + u); verts.Add(c + r + u); verts.Add(c + r - u);
            for (int k = 0; k < 4; k++) normals.Add(normal);
            uvs.Add(offset); uvs.Add(offset + new Vector2(0f, height));
            uvs.Add(offset + new Vector2(width, height)); uvs.Add(offset + new Vector2(width, 0f));
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2); tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
        }
        // Each face starts somewhere else in the texture so neighboring faces don't repeat the same pixels
        Face(Vector3.forward, Vector3.left, Vector3.up, size.x, size.y, new Vector2(0.00f, 0.00f));
        Face(Vector3.back, Vector3.right, Vector3.up, size.x, size.y, new Vector2(0.31f, 0.56f));
        Face(Vector3.right, Vector3.forward, Vector3.up, size.z, size.y, new Vector2(0.62f, 0.19f));
        Face(Vector3.left, Vector3.back, Vector3.up, size.z, size.y, new Vector2(0.12f, 0.75f));
        Face(Vector3.up, Vector3.right, Vector3.forward, size.x, size.z, new Vector2(0.44f, 0.37f));
        Face(Vector3.down, Vector3.right, Vector3.back, size.x, size.z, new Vector2(0.81f, 0.94f));

        mesh = new Mesh { name = $"Box {size.x}x{size.y}x{size.z}" };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        boxMeshes[size] = mesh;
        return mesh;
    }

    /// <summary>A matte lit material showing the texture with crisp pixels. Cached per texture; don't destroy.</summary>
    public static Material Material(Texture2D texture)
    {
        if (materials.TryGetValue(texture, out Material mat) && mat != null) return mat;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        mat = new Material(shader) { name = texture.name, mainTexture = texture };
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", texture);
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0f);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0f);
        materials[texture] = mat;
        return mat;
    }

    /// <summary>
    /// A 16x16 tiling texture: a flat base with sparse clusters of short darker strokes and a few lighter ones.
    /// slant tilts the strokes (0 = straight down, like hanging fur).
    /// </summary>
    public static Texture2D Speckled(string name, Color baseColor, Color dark, Color light, int seed,
                                     int clusters = 7, float lightShare = 0.3f, int slant = 0)
    {
        const int size = ProceduralArt.Size;
        var px = new Color[size * size];
        for (int i = 0; i < px.Length; i++) px[i] = baseColor;
        var rng = new System.Random(seed);
        for (int c = 0; c < clusters; c++)
        {
            Color tone = rng.NextDouble() < lightShare ? light : dark;
            int cx = rng.Next(size), cy = rng.Next(size);
            int strokes = 2 + rng.Next(2);
            for (int s = 0; s < strokes; s++)
            {
                int x = cx + rng.Next(-2, 3), y = cy + rng.Next(-2, 3);
                int length = 2 + rng.Next(2);
                for (int k = 0; k < length; k++)
                {
                    int wx = ((x + (slant != 0 ? k * slant / 2 : 0)) % size + size) % size;
                    int wy = ((y - k) % size + size) % size;
                    px[wx + wy * size] = tone;
                }
            }
        }
        Texture2D tex = ProceduralArt.ToTexture(px, name);
        tex.wrapMode = TextureWrapMode.Repeat;
        return tex;
    }

    /// <summary>A plain color texture, for small details like eyes and claws.</summary>
    public static Texture2D Solid(string name, Color color)
    {
        var px = new Color[ProceduralArt.Size * ProceduralArt.Size];
        for (int i = 0; i < px.Length; i++) px[i] = color;
        Texture2D tex = ProceduralArt.ToTexture(px, name);
        tex.wrapMode = TextureWrapMode.Repeat;
        return tex;
    }
}
