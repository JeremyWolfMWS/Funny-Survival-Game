/// <summary>Player inventory: slots 0-8 are the hotbar, 9-35 the main inventory.</summary>
public class Inventory
{
    public const int HotbarSize = 9;
    public const int Size = 36;

    public readonly ItemStack[] slots = new ItemStack[Size];
    readonly ItemRegistry items;

    public Inventory(ItemRegistry items) => this.items = items;

    public ItemRegistry Items => items;

    /// <summary>Adds as much of the stack as fits (topping up matching stacks first, hotbar first). Returns what's left.</summary>
    public ItemStack Add(ItemStack stack)
    {
        if (stack.IsEmpty) return ItemStack.Empty;
        int max = items.Get(stack.id)?.maxStack ?? 64;

        for (int i = 0; i < Size && stack.count > 0; i++)
            if (slots[i].id == stack.id && slots[i].count < max && max > 1)
            {
                int moved = System.Math.Min(max - slots[i].count, stack.count);
                slots[i].count += moved;
                stack.count -= moved;
            }

        for (int i = 0; i < Size && stack.count > 0; i++)
            if (slots[i].IsEmpty)
            {
                int moved = System.Math.Min(max, stack.count);
                slots[i] = new ItemStack(stack.id, moved, stack.durability);
                stack.count -= moved;
            }

        return stack.count > 0 ? stack : ItemStack.Empty;
    }

    public int CountOf(int id)
    {
        int total = 0;
        foreach (ItemStack s in slots)
            if (s.id == id) total += s.count;
        return total;
    }

    /// <summary>Takes up to count of an item, last slots first so the hotbar is spared. Returns how many were taken.</summary>
    public int Remove(int id, int count)
    {
        int taken = 0;
        for (int i = Size - 1; i >= 0 && taken < count; i--)
        {
            if (slots[i].id != id) continue;
            int n = System.Math.Min(slots[i].count, count - taken);
            slots[i].count -= n;
            taken += n;
            if (slots[i].count <= 0) slots[i] = ItemStack.Empty;
        }
        return taken;
    }

    public void Clear()
    {
        for (int i = 0; i < Size; i++) slots[i] = ItemStack.Empty;
    }
}
