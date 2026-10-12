using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class ToolMaterial
{
    [Tooltip("Put in front of the tool kind, e.g. Stone + Pickaxe = Stone Pickaxe.")]
    public string name = "Stone";
    [Tooltip("Shown in the tooltip, e.g. Battle-ready.")]
    public string tierName = "";
    [Tooltip("Item or block the tool heads are crafted from.")]
    public string ingredient = "Stone";
    [Tooltip("1 = wood, 2 = stone, 3 = iron, 4 = gems and steel, 5 = diamond and titanium, 6 = cursed. Compared " +
             "with a block's Required Tier.")]
    [Min(1)] public int tier = 2;
    [Tooltip("Mining speed multiplier on blocks of the matching tool type. Hand = 1.")]
    [Min(1)] public float miningSpeed = 4f;
    [Tooltip("Blocks a tool can break before it's used up. 0 = unbreakable.")]
    [Min(0)] public int durability = 132;
    [Tooltip("Added to the tool kind's damage.")]
    [Min(0)] public float damage = 1f;
    [Tooltip("Multiplies the tool kind's attack speed.")]
    [Min(0.1f)] public float attackSpeed = 1f;
    [Tooltip("Color of the tool heads in the generated icons.")]
    public Color color = new Color32(135, 135, 135, 255);
    [Tooltip("Gem = polished with a sparkle, Opal = black with flecks of every color, Glowing = white hot with a halo.")]
    public ArtLook look = ArtLook.Plain;

    [Header("Special")]
    [Tooltip("Chance that a use doesn't cost durability.")]
    [Range(0, 1)] public float keepDurabilityChance;
    [Tooltip("Works as any tool type and strikes an enemy's weakness.")]
    public bool adaptive;
    [Tooltip("Damage per second to whoever holds the tool.")]
    [Min(0)] public float heldBurnPerSecond;
    [Tooltip("Pulsing glow behind the icon and around the screen while held. Alpha = strength.")]
    public Color glow = Color.clear;
}

[System.Serializable]
public class ToolKind
{
    public string name = "Pickaxe";
    public ToolType type = ToolType.Pickaxe;
    [Tooltip("Recipe rows: M = material, S = handle, space = empty.")]
    public string[] pattern = { "MMM", " S ", " S " };
    [Tooltip("Damage per hit before the material's bonus.")]
    [Min(0)] public float damage = 2f;
    [Tooltip("Hits per second before the material's multiplier.")]
    [Min(0.1f)] public float attackSpeed = 1.2f;
}

[System.Serializable]
public class WovenTool
{
    [Tooltip("A material name from the list above.")]
    public string material = "Topaz";
    [Tooltip("A tool kind name from the list above.")]
    public string kind = "Pickaxe";
    [Tooltip("Put in front of the tool name, e.g. Weaving Topaz Pickaxe.")]
    public string prefix = "Weaving";
    [Tooltip("Item laid in every top layer cell of the tool's recipe.")]
    public string thread = "String";
    [Min(1)] public int threadPerCell = 4;
    public Color threadColor = new Color32(240, 240, 240, 255);
}

/// <summary>
/// Makes every material x kind tool (Stone Pickaxe, Iron Axe...) with icons and recipes when the world starts, plus
/// woven upgrades. Items or recipes with the same name in the Item / Recipe Database win over generated ones.
/// </summary>
[CreateAssetMenu(fileName = "Tool Manager", menuName = "Voxel/Tool Manager")]
public class ToolDatabase : ScriptableObject
{
    [Header("Handle")]
    public string handleItem = "Stick";
    [Tooltip("If no Item Database has the handle item it's made automatically, along with a recipe: 2 of this " +
             "stacked give 4 handles. Empty = no recipe.")]
    public string handleMadeFrom = "Planks";

    public List<ToolMaterial> materials = DefaultMaterials();

    public List<ToolKind> kinds = new()
    {
        new() { name = "Pickaxe", type = ToolType.Pickaxe, pattern = new[] { "MMM", " S ", " S " }, damage = 2f, attackSpeed = 1.2f },
        new() { name = "Axe", type = ToolType.Axe, pattern = new[] { "MM", "MS", " S" }, damage = 3.5f, attackSpeed = 0.9f },
        new() { name = "Shovel", type = ToolType.Shovel, pattern = new[] { "M", "S", "S" }, damage = 1.5f, attackSpeed = 1f },
        new() { name = "Sword", type = ToolType.Sword, pattern = new[] { "M", "M", "S" }, damage = 4f, attackSpeed = 1.6f },
    };

    static Color C(int r, int g, int b, int a = 255) => new Color32((byte)r, (byte)g, (byte)b, (byte)a);

    /// <summary>
    /// Wood, Stone, Battle-ready (iron, silver), Spiritual gems that often keep their durability, Glass Cannons (gold,
    /// diamond: fast and strong but fragile), Battle-hardened (steel, titanium: tough) and Cursed (black opal adapts to
    /// anything, uranium is the strongest and burns whoever holds it).
    /// </summary>
    public static List<ToolMaterial> DefaultMaterials() => new()
    {
        new() { name = "Wooden", tierName = "Wood", ingredient = "Planks", tier = 1, miningSpeed = 2f, durability = 60, damage = 0f, color = C(160, 120, 70) },
        new() { name = "Stone", tierName = "Stone", ingredient = "Stone", tier = 2, miningSpeed = 4f, durability = 132, damage = 1f, color = C(125, 125, 125) },

        new() { name = "Iron", tierName = "Battle-ready", ingredient = "Iron", tier = 3, miningSpeed = 6f, durability = 250, damage = 2f, color = C(225, 225, 230) },
        new() { name = "Silver", tierName = "Battle-ready", ingredient = "Silver", tier = 3, miningSpeed = 6.5f, durability = 220, damage = 2.5f, attackSpeed = 1.1f, color = C(200, 215, 235) },

        new() { name = "Topaz", tierName = "Spiritual", ingredient = "Topaz", tier = 4, miningSpeed = 7f, durability = 600, damage = 3f, color = C(255, 185, 50), look = ArtLook.Gem, keepDurabilityChance = 0.35f },
        new() { name = "Sapphire", tierName = "Spiritual", ingredient = "Sapphire", tier = 4, miningSpeed = 7.5f, durability = 650, damage = 3f, color = C(60, 110, 235), look = ArtLook.Gem, keepDurabilityChance = 0.35f },
        new() { name = "Emerald", tierName = "Spiritual", ingredient = "Emerald", tier = 4, miningSpeed = 8f, durability = 550, damage = 3f, color = C(40, 200, 90), look = ArtLook.Gem, keepDurabilityChance = 0.4f },
        new() { name = "Ruby", tierName = "Spiritual", ingredient = "Ruby", tier = 4, miningSpeed = 7f, durability = 600, damage = 3.5f, color = C(225, 30, 60), look = ArtLook.Gem, keepDurabilityChance = 0.35f },

        new() { name = "Golden", tierName = "Glass Cannon", ingredient = "Gold", tier = 3, miningSpeed = 12f, durability = 40, damage = 4f, attackSpeed = 1.2f, color = C(250, 210, 60) },
        new() { name = "Diamond", tierName = "Glass Cannon", ingredient = "Diamond", tier = 5, miningSpeed = 14f, durability = 90, damage = 6f, attackSpeed = 1.15f, color = C(120, 235, 240), look = ArtLook.Gem },

        new() { name = "Steel", tierName = "Battle-hardened", ingredient = "Steel", tier = 4, miningSpeed = 8f, durability = 1800, damage = 5f, attackSpeed = 0.9f, color = C(150, 160, 172) },
        new() { name = "Titanium", tierName = "Battle-hardened", ingredient = "Titanium", tier = 5, miningSpeed = 9f, durability = 2400, damage = 5.5f, attackSpeed = 0.95f, color = C(190, 195, 215) },

        new() { name = "Black Opal", tierName = "Cursed", ingredient = "Black Opal", tier = 6, miningSpeed = 15f, durability = 1500, damage = 7f, color = C(25, 20, 40), look = ArtLook.Opal, adaptive = true },
        new() { name = "Uranium", tierName = "Cursed", ingredient = "Uranium", tier = 6, miningSpeed = 30f, durability = 900, damage = 14f, color = C(120, 255, 60), look = ArtLook.Glowing, heldBurnPerSecond = 1f, glow = C(120, 255, 60, 150) },
    };

    [Tooltip("Rare material upgrades: the tool's recipe with lots of thread on the top layer. Blocks mined with them drop whole.")]
    public List<WovenTool> woven = new() { new() };

    public static string ToolName(ToolMaterial material, ToolKind kind) => $"{material.name.Trim()} {kind.name.Trim()}";

    /// <summary>
    /// Adds the generated items and recipes. exists tells whether an item or block name is already taken. Created
    /// icons are added to textures so the caller can destroy them.
    /// </summary>
    public void Generate(List<ItemDefinition> items, List<CraftingRecipe> recipes, System.Func<string, bool> exists, List<Texture2D> textures)
    {
        bool HasRecipe(string result) =>
            recipes.Exists(r => string.Equals(r.result?.Trim(), result, System.StringComparison.OrdinalIgnoreCase));

        void AddRecipe(string result, int count, string[] bottom, string[] top, params RecipeKey[] keys)
        {
            if (HasRecipe(result)) return;
            recipes.Add(new CraftingRecipe { result = result, count = count, bottom = bottom, top = top ?? new string[0], keys = new List<RecipeKey>(keys) });
        }

        Texture2D Icon(Color[] px, string name)
        {
            Texture2D tex = ProceduralArt.ToTexture(px, name);
            textures.Add(tex);
            return tex;
        }

        string handle = handleItem?.Trim();
        if (!string.IsNullOrEmpty(handle) && !exists(handle))
        {
            items.Add(new ItemDefinition { name = handle, icon = Icon(ProceduralArt.Stick(ProceduralArt.HandleColor), handle) });
            if (!string.IsNullOrWhiteSpace(handleMadeFrom) && exists(handleMadeFrom.Trim()))
                AddRecipe(handle, 4, new[] { "P", "P" }, null, new RecipeKey { symbol = "P", item = handleMadeFrom.Trim() });
        }

        foreach (ToolMaterial material in materials)
        {
            if (material == null || string.IsNullOrWhiteSpace(material.name)) continue;
            if (!exists(material.ingredient?.Trim()))
            {
                Debug.LogWarning($"Tool Manager: no item or block named '{material.ingredient}', so there are no {material.name} tools.", this);
                continue;
            }
            foreach (ToolKind kind in kinds)
            {
                if (kind == null || string.IsNullOrWhiteSpace(kind.name)) continue;
                string name = ToolName(material, kind);
                if (!exists(name))
                    items.Add(CreateTool(name, material, kind, Icon(ToolIcon(material, kind), name)));
                AddRecipe(name, 1, kind.pattern, null,
                    new RecipeKey { symbol = "M", item = material.ingredient.Trim() },
                    new RecipeKey { symbol = "S", item = handle });
            }
        }

        foreach (WovenTool w in woven)
        {
            if (w == null) continue;
            ToolMaterial material = materials.Find(m => m != null && string.Equals(m.name?.Trim(), w.material?.Trim(), System.StringComparison.OrdinalIgnoreCase));
            ToolKind kind = kinds.Find(k => k != null && string.Equals(k.name?.Trim(), w.kind?.Trim(), System.StringComparison.OrdinalIgnoreCase));
            if (material == null || kind == null || !exists(material.ingredient?.Trim())) continue;
            if (!exists(w.thread?.Trim()))
            {
                Debug.LogWarning($"Tool Manager: no item named '{w.thread}' to weave with.", this);
                continue;
            }

            string name = $"{w.prefix?.Trim()} {ToolName(material, kind)}".Trim();
            if (!exists(name))
            {
                ItemDefinition tool = CreateTool(name, material, kind, Icon(ProceduralArt.Weave(ToolIcon(material, kind), w.threadColor), name));
                tool.weaving = true;
                items.Add(tool);
            }

            // Thread fills every cell of the top layer over the pattern's bounds
            var top = new string[kind.pattern.Length];
            int width = 0;
            foreach (string row in kind.pattern) width = Mathf.Max(width, row?.Length ?? 0);
            for (int i = 0; i < top.Length; i++) top[i] = new string('w', width);
            AddRecipe(name, 1, kind.pattern, top,
                new RecipeKey { symbol = "M", item = material.ingredient.Trim() },
                new RecipeKey { symbol = "S", item = handle },
                new RecipeKey { symbol = "w", item = w.thread.Trim(), amount = w.threadPerCell });
        }
    }

    static ItemDefinition CreateTool(string name, ToolMaterial material, ToolKind kind, Texture2D icon) => new()
    {
        name = name, icon = icon, maxStack = 1, toolType = kind.type, toolTier = material.tier, tierName = material.tierName,
        miningSpeed = material.miningSpeed, durability = material.durability,
        attackDamage = kind.damage + material.damage, attackSpeed = kind.attackSpeed * material.attackSpeed,
        keepDurabilityChance = material.keepDurabilityChance, adaptive = material.adaptive,
        heldBurnPerSecond = material.heldBurnPerSecond, glow = material.glow
    };

    static Color[] ToolIcon(ToolMaterial material, ToolKind kind) =>
        ProceduralArt.Tool(kind.type, material.color, ProceduralArt.HandleColor, material.look, ProceduralArt.StableHash(material.name + kind.name));
}
