using System.Collections.Generic;
using UnityEngine;

public enum OreDrop { Ingot, Gem, Lump, OreBlock }

[System.Serializable]
public class OreDefinition
{
    [Tooltip("The material, e.g. Iron. Names the block (Iron Ore) and the drop (Iron) unless set below.")]
    public string name = "Iron";

    [Header("Block")]
    [Tooltip("Empty = \"<Name> Ore\". If the Block Database already has a block with this name, that block is used " +
             "as it is and the block settings here are ignored.")]
    public string blockName = "";
    [Tooltip("Empty = the replaced block's texture with specks of Color.")]
    public Texture2D texture;
    [Tooltip("Ore specks in the generated texture and the drop's icon.")]
    public Color color = new Color32(216, 175, 147, 255);
    [Tooltip("Gem = polished with a sparkle, Opal = black with flecks of every color, Glowing = white hot with a halo.")]
    public ArtLook look = ArtLook.Plain;
    [Tooltip("Roughly seconds to break by hand with a pickaxe.")]
    [Min(0)] public float hardness = 3f;
    [Tooltip("Pickaxe tier needed for drops (1 = wood, 2 = stone, 3 = iron, 4 = gems and steel, 5 = diamond and titanium).")]
    [Min(0)] public int requiredTier = 2;
    [Tooltip("Damage per second to a player touching the ore block. 0 = harmless.")]
    [Min(0)] public float damagePerSecond;

    [Header("Drop")]
    [Tooltip("Ore Block drops the ore block itself. The others drop an item with that kind of icon.")]
    public OreDrop drop = OreDrop.Ingot;
    [Tooltip("Empty = Name. Made automatically if no Item Database has it.")]
    public string dropItem = "";
    [Min(1)] public int dropCount = 1;

    [Header("Spawning")]
    [Tooltip("Only this block is turned into ore.")]
    [BlockName] public string replaces = "Stone";
    public int minY = 1;
    public int maxY = 50;
    [Tooltip("Chance for each vein-sized area to hold a vein.")]
    [Range(0, 1)] public float frequency = 0.1f;
    [Tooltip("How much of a vein's area is ore.")]
    [Range(0, 1)] public float density = 0.35f;
    [Tooltip("Size of the area one vein fills, in blocks.")]
    [Range(1, 8)] public int veinSize = 4;
    [Tooltip("Biome asset names it's limited to. Empty = every biome.")]
    public string[] biomes = new string[0];

    public string BlockName => string.IsNullOrWhiteSpace(blockName) ? $"{name?.Trim()} Ore" : blockName.Trim();
    public string DropName => drop == OreDrop.OreBlock ? BlockName : string.IsNullOrWhiteSpace(dropItem) ? name?.Trim() : dropItem.Trim();
}

/// <summary>A material crafted from others, like steel from iron and coal: an ingot item and its recipe.</summary>
[System.Serializable]
public class AlloyDefinition
{
    public string name = "Steel";
    [Tooltip("Laid in a row on the crafting grid, left to right.")]
    public string[] ingredients = { "Iron", "Coal", "Iron" };
    [Tooltip("How many of each ingredient every cell takes.")]
    [Min(1)] public int amountPerCell = 1;
    [Min(1)] public int count = 1;
    public Color color = new Color32(150, 160, 172, 255);
}

/// <summary>
/// Every ore in the world. When the world starts, each ore gets a block (with a generated texture unless one is set),
/// a drop item with an icon, and spawns underground in veins.
/// </summary>
[CreateAssetMenu(fileName = "Ore Manager", menuName = "Voxel/Ore Manager")]
public class OreDatabase : ScriptableObject
{
    public List<OreDefinition> ores = new()
    {
        new() { name = "Coal", color = new Color32(40, 40, 46, 255), requiredTier = 1, drop = OreDrop.Lump, minY = 1, maxY = 70, frequency = 0.14f, density = 0.4f },
        new() { name = "Iron", color = new Color32(216, 175, 147, 255), requiredTier = 2, drop = OreDrop.Ingot, minY = 1, maxY = 55, frequency = 0.12f, density = 0.35f },
        new() { name = "Silver", color = new Color32(205, 218, 235, 255), requiredTier = 2, drop = OreDrop.Ingot, minY = 1, maxY = 35, frequency = 0.06f, density = 0.3f },
        new() { name = "Gold", color = new Color32(250, 210, 60, 255), requiredTier = 3, drop = OreDrop.Ingot, minY = 1, maxY = 30, frequency = 0.05f, density = 0.3f },
        new() { name = "Topaz", color = new Color32(255, 185, 50, 255), look = ArtLook.Gem, requiredTier = 3, drop = OreDrop.Gem, minY = 1, maxY = 24, frequency = 0.035f, density = 0.3f },
        new() { name = "Sapphire", color = new Color32(60, 110, 235, 255), look = ArtLook.Gem, requiredTier = 3, drop = OreDrop.Gem, minY = 1, maxY = 24, frequency = 0.035f, density = 0.3f },
        new() { name = "Emerald", color = new Color32(40, 200, 90, 255), look = ArtLook.Gem, requiredTier = 3, drop = OreDrop.Gem, minY = 1, maxY = 24, frequency = 0.035f, density = 0.3f },
        new() { name = "Ruby", color = new Color32(225, 30, 60, 255), look = ArtLook.Gem, requiredTier = 3, drop = OreDrop.Gem, minY = 1, maxY = 24, frequency = 0.035f, density = 0.3f },
        new() { name = "Diamond", color = new Color32(120, 235, 240, 255), look = ArtLook.Gem, requiredTier = 4, drop = OreDrop.Gem, minY = 1, maxY = 14, frequency = 0.025f, density = 0.3f, hardness = 4f },
        new() { name = "Titanium", color = new Color32(190, 195, 215, 255), requiredTier = 4, drop = OreDrop.Ingot, minY = 1, maxY = 18, frequency = 0.03f, density = 0.3f, hardness = 5f },
        new() { name = "Black Opal", color = new Color32(25, 20, 40, 255), look = ArtLook.Opal, requiredTier = 5, drop = OreDrop.Gem, replaces = "Sandstone", minY = 1, maxY = 45, frequency = 0.03f, density = 0.3f, hardness = 4f, biomes = new[] { "Great Desert" } },
        new() { name = "Uranium", color = new Color32(120, 255, 60, 255), look = ArtLook.Glowing, requiredTier = 5, drop = OreDrop.Lump, replaces = "Red Rock", minY = 1, maxY = 60, frequency = 0.035f, density = 0.3f, hardness = 5f, damagePerSecond = 1.5f, biomes = new[] { "Great Basin" } },
    };

    [Tooltip("Materials crafted from the ore drops, e.g. steel.")]
    public List<AlloyDefinition> alloys = new() { new() };

    /// <summary>Adds a block for each ore whose block isn't in blocks yet. Created textures go into textures.</summary>
    public void AddBlocks(BlockDatabase blocks, List<Texture2D> textures)
    {
        foreach (OreDefinition ore in ores)
        {
            if (ore == null || string.IsNullOrWhiteSpace(ore.name) || blocks.TryGetId(ore.BlockName, out _)) continue;
            if (blocks.blocks.Count >= BlockDatabase.MaxBlocks)
            {
                Debug.LogWarning($"Ore Manager: no room for '{ore.BlockName}', the Block Database is full.", this);
                break;
            }

            Texture2D tex = ore.texture;
            if (tex == null)
            {
                Texture2D baseTex = blocks.TryGetId(ore.replaces, out byte baseId) ? blocks.Get(baseId).GetTexture(BlockDatabase.FaceSide) : null;
                int size = baseTex != null ? Mathf.Clamp(baseTex.width, ProceduralArt.Size, 64) : ProceduralArt.Size;
                Color[] basePixels = baseTex != null ? ProceduralArt.ReadPixels(baseTex, size) : null;
                tex = ProceduralArt.ToTexture(ProceduralArt.Ore(basePixels, size, ore.color, ProceduralArt.StableHash(ore.name), ore.look), ore.BlockName, size);
                textures.Add(tex);
            }

            blocks.blocks.Add(new BlockDefinition
            {
                name = ore.BlockName, sideTexture = tex, hardness = ore.hardness, preferredTool = ToolType.Pickaxe,
                requiredTier = ore.requiredTier, drop = ore.drop == OreDrop.OreBlock ? "" : ore.DropName,
                dropCount = ore.dropCount, waterLoggable = true, slopesInCuttingTable = false,
                damagePerSecond = ore.damagePerSecond
            });
        }
    }

    /// <summary>Adds the drop items no Item Database has. exists tells whether an item or block name is taken.</summary>
    public void AddItems(List<ItemDefinition> items, System.Func<string, bool> exists, List<Texture2D> textures)
    {
        foreach (OreDefinition ore in ores)
        {
            if (ore == null || string.IsNullOrWhiteSpace(ore.name) || ore.drop == OreDrop.OreBlock || exists(ore.DropName)) continue;
            int seed = ProceduralArt.StableHash(ore.name);
            Color[] px = ore.drop switch
            {
                OreDrop.Gem => ProceduralArt.Gem(ore.color),
                OreDrop.Lump => ProceduralArt.Lump(ore.color, seed),
                _ => ProceduralArt.Ingot(ore.color)
            };
            Texture2D icon = ProceduralArt.ToTexture(ProceduralArt.WithLook(px, ore.look, ore.color, seed), ore.DropName);
            textures.Add(icon);
            items.Add(new ItemDefinition { name = ore.DropName, icon = icon });
        }
    }

    /// <summary>Adds each alloy's item (unless something has its name) and its recipe (unless one makes it already).</summary>
    public void AddAlloys(List<ItemDefinition> items, List<CraftingRecipe> recipes, System.Func<string, bool> exists, List<Texture2D> textures)
    {
        foreach (AlloyDefinition alloy in alloys)
        {
            if (alloy == null || string.IsNullOrWhiteSpace(alloy.name) || alloy.ingredients == null || alloy.ingredients.Length == 0) continue;
            string name = alloy.name.Trim();
            if (System.Array.Exists(alloy.ingredients, i => !exists(i?.Trim())))
            {
                Debug.LogWarning($"Ore Manager: some ingredients of {name} don't exist, so it can't be made.", this);
                continue;
            }
            if (!exists(name))
            {
                Texture2D icon = ProceduralArt.ToTexture(ProceduralArt.Ingot(alloy.color), name);
                textures.Add(icon);
                items.Add(new ItemDefinition { name = name, icon = icon });
            }
            if (recipes.Exists(r => string.Equals(r.result?.Trim(), name, System.StringComparison.OrdinalIgnoreCase))) continue;

            // One symbol per different ingredient
            var recipe = new CraftingRecipe { result = name, count = alloy.count, top = new string[0] };
            var row = new System.Text.StringBuilder();
            foreach (string ingredient in alloy.ingredients)
            {
                string item = ingredient.Trim();
                RecipeKey key = recipe.keys.Find(k => string.Equals(k.item, item, System.StringComparison.OrdinalIgnoreCase));
                if (key == null)
                {
                    key = new RecipeKey { symbol = ((char)('a' + recipe.keys.Count)).ToString(), item = item, amount = alloy.amountPerCell };
                    recipe.keys.Add(key);
                }
                row.Append(key.symbol);
            }
            recipe.bottom = new[] { row.ToString() };
            recipes.Add(recipe);
        }
    }
}
