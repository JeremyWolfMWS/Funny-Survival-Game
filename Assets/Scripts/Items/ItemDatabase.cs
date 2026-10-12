using System.Collections.Generic;
using UnityEngine;

public enum ItemAction { None, EmptyBucket, WaterBucket }

/// <summary>A non-block item: tools, materials, buckets. Blocks and their slopes are items automatically.</summary>
[System.Serializable]
public class ItemDefinition
{
    public string name = "Item";
    public Texture2D icon;
    [Min(1)] public int maxStack = 64;

    [Header("Tool (Tool Type None = not a tool)")]
    public ToolType toolType;
    [Tooltip("1 = wood, 2 = stone, 3 = iron/topaz. Compared with a block's Required Tier.")]
    [Min(0)] public int toolTier;
    [Tooltip("Mining speed multiplier on blocks of the matching tool type. Hand = 1.")]
    [Min(1)] public float miningSpeed = 1f;
    [Tooltip("Blocks it can break before it's used up. 0 = unbreakable.")]
    [Min(0)] public int durability;
    [Tooltip("Woven material upgrade: blocks drop themselves, slopes stay slopes, and blocks that need a Weaving Pickaxe drop.")]
    public bool weaving;
    [Tooltip("Shown in the tooltip, e.g. Battle-ready.")]
    public string tierName = "";

    [Header("Combat")]
    [Tooltip("Damage per hit.")]
    [Min(0)] public float attackDamage = 1f;
    [Tooltip("Hits per second.")]
    [Min(0.1f)] public float attackSpeed = 4f;

    [Header("Special")]
    [Tooltip("Chance that a use doesn't cost durability.")]
    [Range(0, 1)] public float keepDurabilityChance;
    [Tooltip("Counts as whatever tool a block needs (mining it at full speed and tier), and strikes an enemy's weakness.")]
    public bool adaptive;
    [Tooltip("Damage per second to whoever holds it.")]
    [Min(0)] public float heldBurnPerSecond;
    [Tooltip("Pulsing glow behind the icon and around the screen while held. Alpha = strength.")]
    public Color glow = Color.clear;

    [Header("Use")]
    public ItemAction action;
}

[CreateAssetMenu(fileName = "ItemDatabase", menuName = "Voxel/Item Database")]
public class ItemDatabase : ScriptableObject
{
    public List<ItemDefinition> items = new();
}
