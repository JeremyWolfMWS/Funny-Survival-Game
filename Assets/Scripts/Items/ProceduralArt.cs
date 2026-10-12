using UnityEngine;

/// <summary>Plain = as colored, Gem = polished facets with a sparkle, Opal = black with flecks of every color, Glowing = white hot with a halo.</summary>
public enum ArtLook { Plain, Gem, Opal, Glowing }

/// <summary>
/// Small pixel-art pictures painted in code: item icons and ore textures. Pixel arrays are row by row with y = 0 at
/// the bottom, like Texture2D.SetPixels. Icons are 16x16 with a transparent background.
/// </summary>
public static class ProceduralArt
{
    public const int Size = 16;

    public static readonly Color HandleColor = new Color32(115, 82, 45, 255);

    public static Texture2D ToTexture(Color[] px, string name, int size = Size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = name, filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
        };
        tex.SetPixels(px);
        tex.Apply(false, false);
        return tex;
    }

    /// <summary>Pixels of any texture scaled to size x size. Works without Read/Write enabled.</summary>
    public static Color[] ReadPixels(Texture2D source, int size)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture rt = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        rt.filterMode = FilterMode.Point;
        Graphics.Blit(source, rt);
        RenderTexture.active = rt;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        Color[] px = tex.GetPixels();
        if (Application.isPlaying) Object.Destroy(tex);
        else Object.DestroyImmediate(tex);
        return px;
    }

    // ---------- Ore textures ----------

    /// <summary>Blobs of ore color scattered over a base texture (stone noise if null). Same seed, same picture.</summary>
    public static Color[] Ore(Color[] basePixels, int size, Color ore, int seed, ArtLook look = ArtLook.Plain)
    {
        var r = new System.Random(seed);
        Color[] px = basePixels != null && basePixels.Length == size * size
            ? (Color[])basePixels.Clone()
            : Noise(r, size, new Color32(125, 125, 125, 255), 0.08f);

        var mask = new bool[size * size];
        int blob = Mathf.Max(2, size / 8);
        int count = Mathf.Max(3, size * size / 64);
        for (int c = 0; c < count; c++)
        {
            int cx = r.Next(1, size - blob), cy = r.Next(1, size - blob);
            for (int dy = 0; dy < blob; dy++)
            for (int dx = 0; dx < blob; dx++)
                if (r.NextDouble() < 0.85)
                {
                    Color c2 = Shade(ore, 1f + Signed(r) * 0.12f);
                    c2.a = 1f;
                    px[cx + dx + (cy + dy) * size] = c2;
                    mask[cx + dx + (cy + dy) * size] = true;
                }
        }
        ApplyLook(px, mask, size, look, ore, seed, false);
        return px;
    }

    /// <summary>
    /// Restyles the masked pixels (one material) of a size x size picture. halo spills a glow onto transparent
    /// pixels around them; on opaque textures the glow brightens the neighbors instead.
    /// </summary>
    public static void ApplyLook(Color[] px, bool[] mask, int size, ArtLook look, Color color, int seed, bool halo = true)
    {
        if (look == ArtLook.Plain) return;
        var r = new System.Random(seed ^ 0x5bd1e995);
        bool Masked(int x, int y) => x >= 0 && x < size && y >= 0 && y < size && mask[x + y * size];

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int i = x + y * size;
            if (mask[i])
            {
                switch (look)
                {
                    case ArtLook.Gem:
                        // Light facets along diagonals, darker on the lower right
                        float facet = (x + y) % 4 == 0 ? 1.3f : (x - y + 64) % 5 == 0 ? 0.8f : 1f;
                        px[i] = Shade(color, facet * (Masked(x + 1, y - 1) ? 1f : 0.8f));
                        break;
                    case ArtLook.Opal:
                        px[i] = r.NextDouble() < 0.45
                            ? Color.HSVToRGB((float)r.NextDouble(), 0.75f, 1f)
                            : Shade(new Color32(24, 18, 36, 255), 1f + Signed(r) * 0.2f);
                        break;
                    case ArtLook.Glowing:
                        bool edge = !Masked(x - 1, y) || !Masked(x + 1, y) || !Masked(x, y - 1) || !Masked(x, y + 1);
                        px[i] = edge ? color : Color.Lerp(color, new Color(1f, 1f, 0.8f), r.NextDouble() < 0.3 ? 0.85f : 0.5f);
                        break;
                }
                px[i].a = 1f;
                continue;
            }

            if (look != ArtLook.Glowing) continue;
            int near = 0;
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                if (Masked(x + dx, y + dy)) near += dx == 0 || dy == 0 ? 2 : 1;
            if (near == 0) continue;
            float strength = Mathf.Min(1f, near / 4f);
            if (px[i].a >= 0.5f) px[i] = Color.Lerp(px[i], color, 0.35f * strength);
            else if (halo) px[i] = new Color(color.r, color.g, color.b, 0.25f + 0.3f * strength);
        }

        if (look == ArtLook.Gem)
        {
            // A couple of white sparkles
            for (int s = 0, tries = 0; s < 2 && tries < 200; tries++)
            {
                int i = r.Next(size * size);
                if (!mask[i]) continue;
                px[i] = Color.white;
                s++;
            }
        }
    }

    /// <summary>ApplyLook on every opaque pixel, for icons that are all one material (gems, lumps, ingots).</summary>
    public static Color[] WithLook(Color[] px, ArtLook look, Color color, int seed)
    {
        var mask = new bool[px.Length];
        for (int i = 0; i < px.Length; i++) mask[i] = px[i].a > 0.5f;
        ApplyLook(px, mask, Size, look, color, seed);
        return px;
    }

    static Color[] Noise(System.Random r, int size, Color baseColor, float variation)
    {
        var px = new Color[size * size];
        for (int i = 0; i < px.Length; i++) px[i] = Shade(baseColor, 1f + Signed(r) * variation);
        return px;
    }

    // ---------- Item icons ----------

    public static Color[] Clear() => new Color[Size * Size];

    public static Color[] Stick(Color c)
    {
        Color[] px = Clear();
        for (int i = 2; i <= 13; i++)
        {
            Plot(px, i, i, c);
            Plot(px, i + 1, i, Shade(c, 0.7f));
        }
        return px;
    }

    public static Color[] StringIcon(Color c)
    {
        Color[] px = Clear();
        for (int x = 1; x < 15; x++)
        {
            int y = 8 + Mathf.RoundToInt(Mathf.Sin(x * 0.9f) * 3f);
            Plot(px, x, y, c);
            Plot(px, x, y - 1, Shade(c, 0.75f));
        }
        return px;
    }

    public static Color[] Ingot(Color c)
    {
        Color[] px = Clear();
        for (int y = 4; y <= 10; y++)
        for (int x = 2 + (y - 4) / 2; x <= 13 - (y - 4) / 2; x++)
            Plot(px, x, y, Shade(c, y >= 9 ? 1.05f : y <= 5 ? 0.7f : 0.9f));
        return px;
    }

    public static Color[] Gem(Color c)
    {
        Color[] px = Clear();
        for (int y = 2; y <= 13; y++)
        {
            int half = y <= 9 ? (y - 2) * 5 / 7 : (13 - y) * 5 / 4 + 1;
            for (int x = 8 - half; x <= 7 + half; x++)
                Plot(px, x, y, Shade(c, x < 8 ? 1.1f : 0.8f));
        }
        Plot(px, 6, 10, Color.white);
        return px;
    }

    /// <summary>A rough rock-like lump, e.g. for coal.</summary>
    public static Color[] Lump(Color c, int seed)
    {
        Color[] px = Clear();
        var r = new System.Random(seed);
        for (int y = 3; y <= 12; y++)
        for (int x = 3; x <= 12; x++)
        {
            float dx = x - 7.5f, dy = y - 7.5f;
            float edge = 4.6f + (float)r.NextDouble() * 0.9f;
            if (dx * dx + dy * dy > edge * edge) continue;
            Plot(px, x, y, Shade(c, (dx < 0f && dy > 0f ? 1.15f : 0.85f) + Signed(r) * 0.08f));
        }
        return px;
    }

    public static Color[] Bucket(Color c, bool water)
    {
        Color[] px = Clear();
        for (int y = 2; y <= 11; y++)
        {
            int inset = (11 - y) / 3;
            for (int x = 3 + inset; x <= 12 - inset; x++)
                Plot(px, x, y, Shade(c, x <= 4 + inset ? 1.05f : x >= 11 - inset ? 0.7f : 0.88f));
        }
        for (int x = 3; x <= 12; x++) Plot(px, x, 11, water ? new Color32(50, 100, 220, 255) : Shade(c, 0.45f));
        for (int x = 4; x <= 11; x++) Plot(px, x, 14 - (x == 4 || x == 11 ? 1 : 0), Shade(c, 0.6f));
        Plot(px, 3, 12, Shade(c, 0.6f));
        Plot(px, 12, 12, Shade(c, 0.6f));
        return px;
    }

    /// <summary>A tool icon with its head restyled (see ArtLook).</summary>
    public static Color[] Tool(ToolType type, Color head, Color handle, ArtLook look, int seed)
    {
        Color[] px = Tool(type, head, handle);
        if (look == ArtLook.Plain) return px;
        Color dark = Shade(head, 0.7f);
        var mask = new bool[px.Length];
        for (int i = 0; i < px.Length; i++)
            mask[i] = px[i].a > 0.5f && (Same(px[i], head) || Same(px[i], dark));
        ApplyLook(px, mask, Size, look, head, seed);
        return px;
    }

    static bool Same(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) < 0.002f && Mathf.Abs(a.g - b.g) < 0.002f && Mathf.Abs(a.b - b.b) < 0.002f;

    public static Color[] Tool(ToolType type, Color head, Color handle)
    {
        Color[] px = Clear();
        // Handle from bottom left toward the top right; a sword's is a short grip
        int handleLength = type == ToolType.Sword ? 3 : 10;
        for (int i = 1; i <= handleLength; i++)
        {
            Plot(px, i, i, handle);
            Plot(px, i + 1, i, Shade(handle, 0.7f));
        }

        Color dark = Shade(head, 0.7f);
        switch (type)
        {
            case ToolType.Sword:
                // Crossguard, then a two pixel wide blade up to a point
                for (int t = -2; t <= 2; t++) Plot(px, 4 + t, 4 - t, dark);
                for (int i = 5; i <= 13; i++)
                {
                    Plot(px, i, i, head);
                    Plot(px, i + 1, i, dark);
                    Plot(px, i, i + 1, head);
                }
                Plot(px, 14, 14, head);
                break;
            case ToolType.Pickaxe:
                // Curved head across the end of the handle
                for (int t = -6; t <= 6; t++)
                {
                    int x = 11 + t, y = 11 - t;
                    int bend = Mathf.Abs(t) >= 5 ? -1 : 0;
                    Plot(px, x + bend, y + bend, head);
                    Plot(px, x + 1 + bend, y + bend, dark);
                }
                break;
            case ToolType.Axe:
                // Blade on one side of the handle end
                for (int y = 8; y <= 15; y++)
                for (int x = 7; x <= 14; x++)
                {
                    int along = x + y, side = y - x;
                    if (along >= 19 && along <= 26 && side >= 1 && side <= 6)
                        Plot(px, x, y, side >= 5 ? dark : head);
                }
                break;
            case ToolType.Shovel:
                // Rounded blade at the end of the handle
                for (int y = 9; y <= 15; y++)
                for (int x = 9; x <= 15; x++)
                {
                    float dx = x - 12.3f, dy = y - 12.3f;
                    if (dx * dx + dy * dy <= 7.5f) Plot(px, x, y, dx + dy > 1.5f ? dark : head);
                }
                break;
        }
        return px;
    }

    /// <summary>Thread wrapped around the handle and trailing off the head, for woven tools.</summary>
    public static Color[] Weave(Color[] px, Color c)
    {
        for (int i = 2; i <= 9; i += 2)
        {
            Plot(px, i, i + 1, c);
            Plot(px, i + 1, i - 1, c);
        }
        Plot(px, 13, 13, c);
        Plot(px, 14, 12, c);
        Plot(px, 3, 13, c);
        Plot(px, 2, 14, c);
        return px;
    }

    // ---------- Helpers ----------

    static void Plot(Color[] px, int x, int y, Color c)
    {
        if (x >= 0 && x < Size && y >= 0 && y < Size) px[x + y * Size] = c;
    }

    public static int StableHash(string s)
    {
        int h = 17;
        foreach (char c in s ?? "") h = h * 31 + char.ToLowerInvariant(c);
        return h & 0x7FFFFFFF;
    }

    static float Signed(System.Random r) => (float)r.NextDouble() * 2f - 1f;

    public static Color Shade(Color c, float k) => new(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), c.a);
}
