using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public class RecipeKey
{
    [Tooltip("Single character used in the patterns.")]
    public string symbol = "#";
    [Tooltip("Item or block name.")]
    public string item = "";
    [Tooltip("How many of the item each cell with this symbol needs.")]
    [Min(1)] public int amount = 1;
}

/// <summary>
/// A shaped recipe for the 2-layer crafting grid. The bottom layer holds the normal recipe. A recipe with a top
/// pattern is a variant: the same bottom pattern plus those items on the top layer gives a different result.
/// Recipes without a top pattern need the top layer to be empty.
/// </summary>
[System.Serializable]
public class CraftingRecipe
{
    [Tooltip("Rows of the bottom (normal) layer. A space means an empty cell.")]
    public string[] bottom = new string[0];
    [Tooltip("Optional rows of the top layer, lined up with the bottom rows. Empty = the top layer must be empty.")]
    public string[] top = new string[0];
    public List<RecipeKey> keys = new();
    public string result = "";
    [Min(1)] public int count = 1;
}

[CreateAssetMenu(fileName = "RecipeDatabase", menuName = "Voxel/Recipe Database")]
public class RecipeDatabase : ScriptableObject
{
    public List<CraftingRecipe> recipes = new();
}

/// <summary>Matches a 2-layer crafting grid (size x size cells per layer) against the recipes.</summary>
public static class Crafting
{
    /// <summary>
    /// Finds the recipe the grid makes. Grids are row-major, row 0 at the top. Returns null if nothing matches.
    /// </summary>
    public static CraftingRecipe Match(RecipeDatabase db, ItemRegistry items, int size, ItemStack[] bottom, ItemStack[] top)
    {
        if (db == null || !Bounds(bottom, size, out int minX, out int minY, out int w, out int h)) return null;

        foreach (CraftingRecipe recipe in db.recipes)
        {
            if (!PatternBounds(recipe.bottom, out int pMinX, out int pMinY, out int pw, out int ph)) continue;
            if (pw != w || ph != h) continue;

            bool match = true;
            for (int y = 0; y < size && match; y++)
            for (int x = 0; x < size && match; x++)
            {
                // Grid cell (x, y) lines up with pattern cell (px, py)
                int px = x - minX + pMinX, py = y - minY + pMinY;
                int i = x + y * size;
                match = Fits(Cell(recipe, recipe.bottom, px, py, items), bottom[i]) &&
                        Fits(Cell(recipe, TopRows(recipe), px, py, items), top[i]);
            }
            if (match) return recipe;
        }
        return null;
    }

    /// <summary>Removes the items a matched recipe uses from both layers.</summary>
    public static void Consume(CraftingRecipe recipe, ItemRegistry items, int size, ItemStack[] bottom, ItemStack[] top)
    {
        if (!Bounds(bottom, size, out int minX, out int minY, out _, out _) ||
            !PatternBounds(recipe.bottom, out int pMinX, out int pMinY, out _, out _)) return;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int px = x - minX + pMinX, py = y - minY + pMinY;
            int i = x + y * size;
            Take(ref bottom[i], Cell(recipe, recipe.bottom, px, py, items).amount);
            Take(ref top[i], Cell(recipe, TopRows(recipe), px, py, items).amount);
        }
    }

    public struct RecipeCell
    {
        public bool top;
        /// <summary>Position relative to the top-left of the bottom pattern.</summary>
        public int x, y;
        /// <summary>-1 if the recipe names an unknown item.</summary>
        public int id;
        public int amount;
    }

    /// <summary>Every filled cell of both layers, positioned relative to the bottom pattern. Empty for broken recipes.</summary>
    public static List<RecipeCell> Cells(CraftingRecipe recipe, ItemRegistry items)
    {
        var cells = new List<RecipeCell>();
        if (!PatternBounds(recipe.bottom, out int minX, out int minY, out _, out _)) return cells;

        void AddLayer(string[] rows, bool top)
        {
            for (int y = 0; rows != null && y < rows.Length; y++)
            for (int x = 0; rows[y] != null && x < rows[y].Length; x++)
            {
                var (id, amount) = Cell(recipe, rows, x, y, items);
                if (id != 0) cells.Add(new RecipeCell { top = top, x = x - minX, y = y - minY, id = id, amount = amount });
            }
        }
        AddLayer(recipe.bottom, false);
        AddLayer(TopRows(recipe), true);
        return cells;
    }

    static string[] TopRows(CraftingRecipe recipe) => recipe.top != null && recipe.top.Length > 0 ? recipe.top : null;

    static bool Fits((int id, int amount) need, ItemStack have) =>
        need.id == 0 ? have.IsEmpty : have.id == need.id && have.count >= need.amount;

    static void Take(ref ItemStack stack, int amount)
    {
        if (stack.IsEmpty || amount <= 0) return;
        stack.count -= amount;
        if (stack.count <= 0) stack = ItemStack.Empty;
    }

    static (int id, int amount) Cell(CraftingRecipe recipe, string[] rows, int x, int y, ItemRegistry items)
    {
        if (rows == null || y < 0 || y >= rows.Length || x < 0 || rows[y] == null || x >= rows[y].Length) return (0, 0);
        char c = rows[y][x];
        if (c == ' ') return (0, 0);
        foreach (RecipeKey key in recipe.keys)
            if (!string.IsNullOrEmpty(key.symbol) && key.symbol[0] == c)
            {
                int id = items.GetId(key.item);
                return (id != 0 ? id : -1, Mathf.Max(1, key.amount));
            }
        return (-1, 1); // Unknown symbols and item names never match
    }

    static bool Bounds(ItemStack[] grid, int size, out int minX, out int minY, out int w, out int h)
    {
        minX = minY = int.MaxValue;
        int maxX = -1, maxY = -1;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            if (grid[x + y * size].IsEmpty) continue;
            minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
            minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
        }
        w = maxX - minX + 1;
        h = maxY - minY + 1;
        return maxX >= 0;
    }

    static bool PatternBounds(string[] rows, out int minX, out int minY, out int w, out int h)
    {
        minX = minY = int.MaxValue;
        int maxX = -1, maxY = -1;
        for (int y = 0; rows != null && y < rows.Length; y++)
        for (int x = 0; rows[y] != null && x < rows[y].Length; x++)
        {
            if (rows[y][x] == ' ') continue;
            minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x);
            minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y);
        }
        w = maxX - minX + 1;
        h = maxY - minY + 1;
        return maxX >= 0;
    }
}
