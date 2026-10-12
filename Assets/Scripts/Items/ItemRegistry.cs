using System.Collections.Generic;
using UnityEngine;

/// <summary>Runtime info about one item ID.</summary>
public class ItemInfo
{
    public int id;
    public string name;
    /// <summary>Picture shown in the inventory. Blocks get an isometric render.</summary>
    public Texture2D icon;
    public int maxStack = 64;
    /// <summary>Block placed by this item, 0 for none.</summary>
    public byte blockId;
    /// <summary>Places the block as a slope (auto-shaped into corners).</summary>
    public bool isSlope;
    /// <summary>Set for tools, materials and other non-block items.</summary>
    public ItemDefinition definition;

    public bool IsTool => definition != null && definition.toolType != ToolType.None;

    /// <summary>Shape used to show this item as a block (in icons, drops and the hand).</summary>
    public byte DisplayShape => isSlope ? ItemRegistry.SlopeDisplayShape : VoxelShapes.Cube;
}

/// <summary>A stack of items in an inventory slot. id 0 = empty.</summary>
[System.Serializable]
public struct ItemStack
{
    public int id;
    public int count;
    /// <summary>Uses left for tools.</summary>
    public int durability;

    public bool IsEmpty => id == 0 || count <= 0;

    public static readonly ItemStack Empty = default;

    public ItemStack(int id, int count, int durability = 0)
    {
        this.id = id;
        this.count = count;
        this.durability = durability;
    }
}

/// <summary>
/// All item IDs: 0 is empty, then every block, every block's slope ("Name Slope"), then the Item Database entries.
/// Names are looked up case-insensitively.
/// </summary>
public class ItemRegistry
{
    public const string SlopeSuffix = " Slope";
    const int IconSize = 64;

    /// <summary>Slope rising to the right-back, which reads well from the isometric icon angle.</summary>
    public static readonly byte SlopeDisplayShape = VoxelShapes.Slope(0);

    readonly List<ItemInfo> items = new() { null };
    readonly Dictionary<string, int> byName = new(System.StringComparer.OrdinalIgnoreCase);
    readonly int[] blockItem = new int[256];
    readonly int[] slopeItem = new int[256];
    readonly List<Texture2D> renderedIcons = new();

    /// <summary>Pass the (readable) block atlas to give block items isometric icons.</summary>
    public ItemRegistry(BlockDatabase blocks, ItemDatabase itemDatabase, Texture2D atlas = null)
    {
        Color32[] atlasPixels = atlas != null && atlas.isReadable ? atlas.GetPixels32() : null;
        Texture2D Icon(byte id, byte shape, BlockDefinition b)
        {
            if (atlasPixels == null) return b.GetTexture(BlockDatabase.FaceSide);
            Texture2D icon = BlockShapeMesh.RenderIcon(blocks, atlasPixels, atlas.width, atlas.height, id, shape, IconSize);
            renderedIcons.Add(icon);
            return icon;
        }

        int blockCount = Mathf.Min(blocks.blocks.Count, BlockDatabase.MaxBlocks);
        for (int i = 0; i < blockCount; i++)
        {
            BlockDefinition b = blocks.blocks[i];
            var id = (byte)(i + 1);
            blockItem[id] = Add(new ItemInfo { name = b.name, icon = Icon(id, VoxelShapes.Cube, b), blockId = id });
        }
        for (int i = 0; i < blockCount; i++)
        {
            BlockDefinition b = blocks.blocks[i];
            var id = (byte)(i + 1);
            slopeItem[id] = Add(new ItemInfo
            {
                name = b.name + SlopeSuffix, icon = Icon(id, SlopeDisplayShape, b), blockId = id, isSlope = true
            });
        }

        if (itemDatabase != null)
            foreach (ItemDefinition def in itemDatabase.items)
                Add(new ItemInfo
                {
                    name = def.name, icon = def.icon, definition = def,
                    maxStack = def.toolType != ToolType.None || def.action != ItemAction.None ? 1 : Mathf.Max(1, def.maxStack)
                });
    }

    int Add(ItemInfo info)
    {
        info.id = items.Count;
        items.Add(info);
        if (!string.IsNullOrWhiteSpace(info.name) && !byName.ContainsKey(info.name.Trim()))
            byName[info.name.Trim()] = info.id;
        return info.id;
    }

    /// <summary>Destroys the icons this registry rendered.</summary>
    public void Dispose()
    {
        foreach (Texture2D t in renderedIcons) if (t != null) Object.Destroy(t);
        renderedIcons.Clear();
    }

    public int Count => items.Count;

    /// <summary>Info for an ID, or null for empty/unknown.</summary>
    public ItemInfo Get(int id) => id > 0 && id < items.Count ? items[id] : null;

    /// <summary>Item ID by name (case-insensitive), 0 if unknown.</summary>
    public int GetId(string name) =>
        !string.IsNullOrWhiteSpace(name) && byName.TryGetValue(name.Trim(), out int id) ? id : 0;

    public int BlockItem(byte blockId) => blockItem[blockId];

    public int SlopeItem(byte blockId) => slopeItem[blockId];

    /// <summary>A fresh stack, with full durability for tools.</summary>
    public ItemStack CreateStack(int id, int count = 1)
    {
        ItemInfo info = Get(id);
        if (info == null) return ItemStack.Empty;
        return new ItemStack(id, Mathf.Min(count, info.maxStack), info.definition?.durability ?? 0);
    }
}
