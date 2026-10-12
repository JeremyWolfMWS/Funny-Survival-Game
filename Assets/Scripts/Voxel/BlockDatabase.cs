using System.Collections.Generic;
using UnityEngine;

public enum ToolType { None, Pickaxe, Axe, Shovel, Sword }

public enum BlockInteraction { None, CraftingTable, CuttingTable }

[System.Serializable]
public class BlockDefinition
{
    public string name = "Block";

    [Tooltip("Texture used on the sides. Also used for top/bottom when those are empty.")]
    public Texture2D sideTexture;
    public Texture2D topTexture;
    public Texture2D bottomTexture;

    [Header("Looks")]
    [Tooltip("Green pixels take on the biome's Grass Color (grass, leaves, tall grass). Brown and other colors stay as " +
             "painted, so a grass block's dirt isn't tinted. Paint the parts to tint in any green.")]
    public bool biomeTint;
    [Tooltip("Drawn as two crossed see-through planes, like tall grass and flowers. You walk through it, and it pops " +
             "off when the block under it goes. Uses the side texture; transparent pixels are cut out.")]
    public bool plant;

    [Header("Mining")]
    [Tooltip("Roughly seconds to break by hand with the right tool type. 0 = instant.")]
    [Min(0)] public float hardness = 1f;
    [Tooltip("Tool type that mines this faster and can meet Required Tier.")]
    public ToolType preferredTool;
    [Tooltip("Minimum tier of the preferred tool needed for drops (0 = anything, 1 = wood, 2 = stone, 3 = iron, " +
             "4 = gems and steel, 5 = diamond and titanium, 6 = cursed).")]
    [Min(0)] public int requiredTier;
    [Tooltip("If off, the block only drops when mined with a Weaving Pickaxe.")]
    public bool mineableWithoutWeavingPickaxe = true;
    [Tooltip("Item dropped when mined normally. Empty = the block itself. A Weaving Pickaxe always drops the block itself.")]
    public string drop = "";
    [Min(0)] public int dropCount = 1;
    [Range(0, 1)] public float dropChance = 1f;

    [Header("Properties")]
    [Tooltip("Can share its space with a water source (placed into water, or filled with a bucket).")]
    public bool waterLoggable;
    [Tooltip("0 = normal grip, 1 = almost no grip (ice-like). Affects anything walking on it.")]
    [Range(0, 1)] public float slipperiness;
    [Tooltip("Damage per second to a player touching it. 0 = harmless.")]
    [Min(0)] public float damagePerSecond;
    [Tooltip("The Cutting Table can turn this block into slopes.")]
    public bool slopesInCuttingTable = true;
    [Tooltip("What right clicking the block does.")]
    public BlockInteraction interaction;

    public Texture2D GetTexture(int faceType) => faceType switch
    {
        BlockDatabase.FaceTop when topTexture != null => topTexture,
        BlockDatabase.FaceBottom when bottomTexture != null => bottomTexture,
        _ => sideTexture
    };
}

[CreateAssetMenu(fileName = "BlockDatabase", menuName = "Voxel/Block Database")]
public class BlockDatabase : ScriptableObject
{
    [Tooltip("Solid blocks only, air is built in. Block ID = list index + 1 (ID 0 is air). Max 255 blocks.")]
    public List<BlockDefinition> blocks = new();

    [Tooltip("Texture for water. Packed into the same atlas as the blocks.")]
    public Texture2D waterTexture;

    [Tooltip("Pixel size of one atlas tile. 0 = use the largest block texture.")]
    public int tileSize = 0;

    [Tooltip("Crack stages drawn over a block while mining it, from barely cracked to almost broken. " +
             "Transparent where there's no crack. Empty = Breaking Sheet.")]
    public Texture2D[] breakingFrames = new Texture2D[0];

    [Tooltip("All crack stages in one image: square frames left to right, then top to bottom; empty frames are " +
             "skipped. Used when Breaking Frames is empty. Empty = Assets/Resources/Breaking Animation.")]
    public Texture2D breakingSheet;
    [Tooltip("Pixel size of one frame in the breaking sheet. 0 = the biggest size its width and height both divide " +
             "into (16 for a 32x48 sheet).")]
    [Min(0)] public int breakingFrameSize = 0;

    public const string DefaultBreakingSheet = "Breaking Animation";

    // Per tint, then per block ID: [top, side, bottom]. Tint 0 is the textures as painted.
    [System.NonSerialized] Rect[] uvRects;
    [System.NonSerialized] int uvStride, tintCount;
    [System.NonSerialized] Rect waterUV;
    [System.NonSerialized] bool[] plants, cutouts;

    public const int FaceTop = 0, FaceSide = 1, FaceBottom = 2;
    public const int MaxBlocks = byte.MaxValue;

    /// <summary>Tint used where no biome is involved: item icons, dropped blocks, debris.</summary>
    public int DefaultTint { get; set; }

    /// <summary>
    /// Packs every block face texture into one point-filtered atlas and caches the UV rect of each face. Blocks with
    /// Biome Tint get a recolored copy of their textures per tint; tint i + 1 in GetUVRect is tints[i].
    /// </summary>
    public Texture2D BuildAtlas(IReadOnlyList<Color> tints = null)
    {
        int count = Mathf.Min(blocks.Count, MaxBlocks) + 1;
        tintCount = tints?.Count ?? 0;
        uvStride = count * 3;
        uvRects = new Rect[(tintCount + 1) * uvStride];
        plants = new bool[count];
        DefaultTint = Mathf.Clamp(DefaultTint, 0, tintCount);

        // Slot 0 is reserved for faces without a texture
        var textures = new List<Texture2D> { null };
        var slotOf = new Dictionary<Texture2D, int>();
        var faceSlots = new int[(tintCount + 1) * uvStride];
        int maxSize = 1;

        for (int id = 1; id < count; id++)
        {
            plants[id] = blocks[id - 1].plant;
            for (int f = 0; f < 3; f++)
            {
                Texture2D tex = blocks[id - 1].GetTexture(f);
                if (tex == null) continue;
                if (!slotOf.TryGetValue(tex, out int slot))
                {
                    slot = textures.Count;
                    slotOf[tex] = slot;
                    textures.Add(tex);
                    maxSize = Mathf.Max(maxSize, tex.width, tex.height);
                }
                faceSlots[id * 3 + f] = slot;
            }
        }

        int waterSlot = 0;
        if (waterTexture != null)
        {
            waterSlot = textures.Count;
            textures.Add(waterTexture);
            maxSize = Mathf.Max(maxSize, waterTexture.width, waterTexture.height);
        }

        // Tinted copies go after the textures: (source slot, tint) per extra slot
        var tinted = new List<(int source, Color tint)>();
        var tintedSlotOf = new Dictionary<(int source, int tint), int>();
        for (int t = 1; t <= tintCount; t++)
        for (int id = 1; id < count; id++)
        for (int f = 0; f < 3; f++)
        {
            int source = faceSlots[id * 3 + f];
            int slot = source;
            if (source != 0 && blocks[id - 1].biomeTint && !tintedSlotOf.TryGetValue((source, t), out slot))
            {
                slot = textures.Count + tinted.Count;
                tintedSlotOf[(source, t)] = slot;
                tinted.Add((source, tints[t - 1]));
            }
            faceSlots[t * uvStride + id * 3 + f] = slot;
        }

        int slots = textures.Count + tinted.Count;
        int tile = tileSize > 0 ? tileSize : maxSize;
        int columns = Mathf.CeilToInt(Mathf.Sqrt(slots));
        int rows = Mathf.CeilToInt(slots / (float)columns);

        var atlas = new Texture2D(columns * tile, rows * tile, TextureFormat.RGBA32, false)
        {
            name = "Block Atlas",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        var missing = new Color32[tile * tile];
        System.Array.Fill(missing, new Color32(255, 0, 255, 255));
        atlas.SetPixels32(0, 0, tile, tile, missing);

        // Blitting through a RenderTexture works for any texture format and doesn't need Read/Write enabled
        RenderTexture previous = RenderTexture.active;
        RenderTexture rt = RenderTexture.GetTemporary(tile, tile, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        rt.filterMode = FilterMode.Point;
        for (int s = 1; s < textures.Count; s++)
        {
            Graphics.Blit(textures[s], rt);
            RenderTexture.active = rt;
            atlas.ReadPixels(new Rect(0, 0, tile, tile), (s % columns) * tile, (s / columns) * tile, false);
        }
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);

        for (int i = 0; i < tinted.Count; i++)
        {
            int source = tinted[i].source, slot = textures.Count + i;
            Color[] px = atlas.GetPixels((source % columns) * tile, (source / columns) * tile, tile, tile);
            ApplyTint(px, tinted[i].tint);
            atlas.SetPixels((slot % columns) * tile, (slot / columns) * tile, tile, tile, px);
        }
        // Stays readable so item icons can be rendered from it
        atlas.Apply(false, false);

        Color32[] atlasPixels = atlas.GetPixels32();
        cutouts = new bool[count];
        HasCutouts = false;
        for (int id = 1; id < count; id++)
        {
            if (plants[id]) continue;
            for (int f = 0; f < 3 && !cutouts[id]; f++)
                cutouts[id] = HasHoles(atlasPixels, atlas.width, faceSlots[id * 3 + f], columns, tile);
            HasCutouts |= cutouts[id];
        }

        var tileUV = new Vector2(1f / columns, 1f / rows);
        for (int i = 0; i < uvRects.Length; i++)
        {
            int s = faceSlots[i];
            uvRects[i] = TileRect(s % columns, s / columns, tileUV);
        }
        waterUV = TileRect(waterSlot % columns, waterSlot / columns, tileUV);
        return atlas;
    }

    /// <summary>
    /// Recolors the green pixels to tint, keeping their light and dark detail: on average they come out as the tint.
    /// Pixels that aren't green (dirt, flower petals) are left alone. tint.a is how strongly to recolor.
    /// </summary>
    public static void ApplyTint(Color[] px, Color tint)
    {
        var mask = new float[px.Length];
        float brightness = 0f, weight = 0f;
        for (int i = 0; i < px.Length; i++)
        {
            mask[i] = GreenAmount(px[i]);
            brightness += Luminance(px[i]) * mask[i];
            weight += mask[i];
        }
        if (weight <= 0f) return;
        float average = Mathf.Max(brightness / weight, 0.001f);

        for (int i = 0; i < px.Length; i++)
        {
            if (mask[i] <= 0f) continue;
            Color c = px[i];
            float k = Luminance(c) / average;
            var recolored = new Color(tint.r * k, tint.g * k, tint.b * k, c.a);
            px[i] = Color.Lerp(c, recolored, mask[i] * tint.a);
        }
    }

    // 1 for clearly green pixels, fading out toward yellow and cyan; 0 for browns, greys and see-through pixels
    static float GreenAmount(Color c)
    {
        if (c.a <= 0.01f) return 0f;
        Color.RGBToHSV(c, out float h, out float s, out float v);
        float hue = h * 360f;
        float hueAmount = Mathf.Clamp01((hue - 50f) / 20f) * Mathf.Clamp01((185f - hue) / 20f);
        return hueAmount * Mathf.Clamp01((s - 0.12f) / 0.13f) * Mathf.Clamp01(v / 0.1f);
    }

    static float Luminance(Color c) => 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;

    static bool HasHoles(Color32[] atlas, int atlasWidth, int slot, int columns, int tile)
    {
        if (slot == 0) return false;
        int x0 = slot % columns * tile, y0 = slot / columns * tile;
        for (int y = 0; y < tile; y++)
        for (int x = 0; x < tile; x++)
            if (atlas[x0 + x + (y0 + y) * atlasWidth].a < 128) return true;
        return false;
    }

    public Rect WaterUVRect => waterUV;

    /// <summary>Whether the block is drawn as a plant (see BlockDefinition.plant). Safe from worker threads after BuildAtlas.</summary>
    public bool IsPlant(byte id) => plants != null ? id < plants.Length && plants[id] : Get(id)?.plant ?? false;

    /// <summary>
    /// A full block whose textures have see-through pixels, like leaves: the holes are cut out, light gets through
    /// and blocks behind it are drawn. Known after BuildAtlas, safe from worker threads.
    /// </summary>
    public bool IsCutout(byte id) => cutouts != null && id < cutouts.Length && cutouts[id];

    /// <summary>Plants and cutout blocks: neighbors can be seen past them, so faces touching them are drawn.</summary>
    public bool IsSeeThrough(byte id) => IsPlant(id) || IsCutout(id);

    /// <summary>Some block is a cutout block, so the chunk material has to cut out see-through pixels.</summary>
    public bool HasCutouts { get; private set; }

    /// <summary>
    /// Breaking Sheet (or the built-in one in Resources) cut into its frames, skipping empty ones. Makes new
    /// textures every call, which the caller destroys. Works without Read/Write enabled on the sheet.
    /// </summary>
    public Texture2D[] SliceBreakingSheet()
    {
        Texture2D sheet = breakingSheet != null ? breakingSheet : Resources.Load<Texture2D>(DefaultBreakingSheet);
        if (sheet == null) return new Texture2D[0];

        int w = sheet.width, h = sheet.height;
        int size = breakingFrameSize > 0 ? breakingFrameSize : Gcd(w, h);
        Color32[] pixels = ReadPixels(sheet);
        var frames = new List<Texture2D>();
        for (int row = 0; row < h / size; row++)
        for (int column = 0; column < w / size; column++)
        {
            // Frames go top to bottom, texture rows bottom to top
            int x0 = column * size, y0 = h - (row + 1) * size;
            var frame = new Color32[size * size];
            bool empty = true;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                Color32 c = pixels[x0 + x + (y0 + y) * w];
                frame[x + y * size] = c;
                if (c.a > 0) empty = false;
            }
            if (empty) continue;

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = $"{sheet.name} {frames.Count}", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
            };
            tex.SetPixels32(frame);
            tex.Apply(false, true);
            frames.Add(tex);
        }
        return frames.ToArray();
    }

    static int Gcd(int a, int b) => b == 0 ? Mathf.Max(a, 1) : Gcd(b, a % b);

    static Color32[] ReadPixels(Texture2D source)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        Graphics.Blit(source, rt);
        RenderTexture.active = rt;
        var copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
        copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
        RenderTexture.active = previous;
        RenderTexture.ReleaseTemporary(rt);
        Color32[] pixels = copy.GetPixels32();
        Destroy(copy);
        return pixels;
    }

    /// <summary>Like GetId, but returns false instead of warning when the name is unknown.</summary>
    public bool TryGetId(string blockName, out byte id)
    {
        id = 0;
        if (string.IsNullOrWhiteSpace(blockName)) return false;
        string trimmed = blockName.Trim();
        for (int i = 0; i < blocks.Count && i < MaxBlocks; i++)
            if (string.Equals(blocks[i].name?.Trim(), trimmed, System.StringComparison.OrdinalIgnoreCase))
            {
                id = (byte)(i + 1);
                return true;
            }
        return false;
    }

    /// <summary>Block ID for a block name (case-insensitive). Empty name = air. Unknown names log a warning and return air.</summary>
    public byte GetId(string blockName, Object context = null)
    {
        if (string.IsNullOrWhiteSpace(blockName)) return 0;
        if (TryGetId(blockName, out byte id)) return id;

        Debug.LogWarning($"No block named '{blockName}' in {name}. Using air instead.", context != null ? context : this);
        return 0;
    }

    public string GetName(byte id) => Get(id)?.name;

    /// <summary>Block definition for an ID, or null for air and unknown IDs.</summary>
    public BlockDefinition Get(byte id) => id > 0 && id <= blocks.Count ? blocks[id - 1] : null;

    /// <summary>UV rect of a face in the atlas, colored with the Default Tint.</summary>
    public Rect GetUVRect(byte id, int faceType) => GetUVRect(id, faceType, DefaultTint);

    /// <summary>UV rect of a face in the atlas. tint 0 is the texture as painted, tint i + 1 the i-th BuildAtlas tint.</summary>
    public Rect GetUVRect(byte id, int faceType, int tint)
    {
        if (uvRects == null) return default;
        if (tint < 0 || tint > tintCount) tint = 0;
        int i = tint * uvStride + id * 3 + faceType;
        return i < uvRects.Length ? uvRects[i] : default;
    }

    static Rect TileRect(int column, int row, Vector2 size)
    {
        const float inset = 0.0005f;
        return new Rect(column * size.x + inset, row * size.y + inset, size.x - 2f * inset, size.y - 2f * inset);
    }
}

/// <summary>Shows a string field as a dropdown of block names from the project's Block Databases.</summary>
public class BlockNameAttribute : PropertyAttribute { }
