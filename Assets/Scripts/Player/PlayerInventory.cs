using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The player's items. 1-9 / scroll wheel pick the hotbar slot, Q drops one of the held item (Ctrl+Q the stack).
/// </summary>
[RequireComponent(typeof(PlayerController))]
public class PlayerInventory : MonoBehaviour
{
    [System.Serializable]
    public class StarterItem
    {
        [Tooltip("Item or block name.")]
        public string item = "";
        [Min(1)] public int count = 1;
    }

    public static readonly List<PlayerInventory> All = new();

    [Tooltip("Given at the start and after respawning. Empty for a normal survival start.")]
    public List<StarterItem> starterItems = new();

    public Inventory Inventory { get; private set; }
    public ItemRegistry Items => world != null ? world.Items : null;
    public int SelectedSlot { get; private set; }
    public ItemStack Selected => Inventory.slots[SelectedSlot];
    public ItemInfo SelectedInfo => Items?.Get(Selected.id);
    /// <summary>Time.time of the last hotbar change, for showing the item name.</summary>
    public float SlotChangedTime { get; private set; } = -10f;

    PlayerController controller;
    VoxelWorld world;

    void Awake()
    {
        controller = GetComponent<PlayerController>();
    }

    void OnEnable() => All.Add(this);

    void OnDisable() => All.Remove(this);

    void Start()
    {
        world = controller.world;
        Inventory = new Inventory(world.Items);
        GiveStarterItems();
    }

    public void GiveStarterItems()
    {
        foreach (StarterItem s in starterItems)
        {
            int id = Items.GetId(s.item);
            if (id == 0)
            {
                Debug.LogWarning($"Starter item '{s.item}' doesn't exist.", this);
                continue;
            }
            for (int left = s.count; left > 0;)
            {
                ItemStack stack = Items.CreateStack(id, left);
                left -= stack.count;
                Give(stack);
            }
        }
    }

    void Update()
    {
        if (Inventory == null || !controller.HasFocus) return;
        Keyboard keyboard = Keyboard.current;
        Mouse mouse = Mouse.current;
        if (keyboard == null || mouse == null) return;

        int previous = SelectedSlot;
        for (int i = 0; i < Inventory.HotbarSize; i++)
            if (keyboard[Key.Digit1 + i].wasPressedThisFrame) SelectedSlot = i;

        float scroll = mouse.scroll.ReadValue().y;
        if (scroll > 0f) SelectedSlot = (SelectedSlot + Inventory.HotbarSize - 1) % Inventory.HotbarSize;
        else if (scroll < 0f) SelectedSlot = (SelectedSlot + 1) % Inventory.HotbarSize;
        if (SelectedSlot != previous) SlotChangedTime = Time.time;

        if (keyboard.qKey.wasPressedThisFrame && !Selected.IsEmpty)
        {
            int count = keyboard.leftCtrlKey.isPressed ? Selected.count : 1;
            ItemStack thrown = Selected;
            thrown.count = count;
            ConsumeSelected(count);
            Throw(thrown);
        }
    }

    public void SelectSlot(int slot)
    {
        SelectedSlot = Mathf.Clamp(slot, 0, Inventory.HotbarSize - 1);
        SlotChangedTime = Time.time;
    }

    public void SetSelected(ItemStack stack) => Inventory.slots[SelectedSlot] = stack.IsEmpty ? ItemStack.Empty : stack;

    public void ConsumeSelected(int count = 1)
    {
        ItemStack s = Selected;
        s.count -= count;
        SetSelected(s);
    }

    /// <summary>Uses up one durability of the held tool. Returns true if it broke.</summary>
    public bool DamageSelectedTool()
    {
        ItemStack s = Selected;
        ItemDefinition def = SelectedInfo?.definition;
        if (def == null || def.durability <= 0) return false;
        if (def.keepDurabilityChance > 0f && Random.value < def.keepDurabilityChance) return false;
        s.durability--;
        if (s.durability > 0)
        {
            SetSelected(s);
            return false;
        }
        SetSelected(ItemStack.Empty);
        return true;
    }

    /// <summary>Adds to the inventory, dropping whatever doesn't fit at the player's feet.</summary>
    public void Give(ItemStack stack)
    {
        ItemStack left = Inventory.Add(stack);
        if (!left.IsEmpty) ItemDrop.Spawn(world, left, transform.position + Vector3.up * 0.5f, Vector3.zero, 1f);
    }

    /// <summary>Throws a stack forward from the eyes.</summary>
    public void Throw(ItemStack stack)
    {
        Transform eye = controller.playerCamera != null ? controller.playerCamera.transform : transform;
        Vector3 velocity = eye.forward * 5f + Vector3.up * 1.5f;
        ItemDrop.Spawn(world, stack, eye.position + eye.forward * 0.4f - Vector3.up * 0.3f, velocity, 1.5f);
    }

    /// <summary>Scatters every item around the player, used on death.</summary>
    public void DropAll()
    {
        for (int i = 0; i < Inventory.Size; i++)
        {
            if (Inventory.slots[i].IsEmpty) continue;
            Vector2 dir = Random.insideUnitCircle * 3f;
            ItemDrop.Spawn(world, Inventory.slots[i], transform.position + Vector3.up,
                new Vector3(dir.x, 3f, dir.y), 2f);
            Inventory.slots[i] = ItemStack.Empty;
        }
    }
}
