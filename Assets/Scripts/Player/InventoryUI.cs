using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// E opens the inventory with a 2x2 crafting grid; crafting tables give 3x3, cutting tables turn blocks into slopes.
/// The crafting grid has two layers: the bottom layer is the normal recipe, items on the top layer make variants.
/// Left click picks up / places / swaps, right click takes half or places one, shift click moves items across,
/// 1-9 over a slot swaps it with that hotbar slot. The recipe book fills the grid for you (shift = as many as possible).
/// Also draws the hotbar, crosshair and the control hints at the start.
/// </summary>
[DefaultExecutionOrder(100)]
[RequireComponent(typeof(PlayerController), typeof(PlayerInventory))]
public class InventoryUI : MonoBehaviour
{
    public enum Mode { Closed, Inventory, CraftingTable, CuttingTable }

    public const float HotbarSlotSize = 40f;

    public static readonly Color PanelColor = new Color32(198, 198, 198, 250);
    public static readonly Color PanelLight = Color.white;
    public static readonly Color PanelDark = new Color32(85, 85, 85, 255);
    public static readonly Color SlotColor = new Color32(139, 139, 139, 255);
    public static readonly Color SlotDark = new Color32(55, 55, 55, 255);
    static readonly Color TopLayerColor = new Color32(128, 118, 160, 255);
    static readonly Color ResultColor = new Color32(130, 150, 110, 255);
    public static readonly Color TextColor = new Color32(63, 63, 63, 255);

    [Tooltip("Seconds the control hints show at the start.")]
    public float controlsHintTime = 20f;

    PlayerController controller;
    PlayerInventory player;
    Mode mode = Mode.Closed;
    int gridSize = 2;
    ItemStack[] bottom = new ItemStack[4];
    ItemStack[] top = new ItemStack[4];
    readonly ItemStack[] cutInput = new ItemStack[1];
    ItemStack held;
    int recipePage;

    GUIStyle countStyle, labelStyle, titleStyle, headerStyle, tooltipStyle, hintStyle;
    string hoverText;
    ItemStack[] hoverSlots;
    int hoverIndex;
    float scale;

    public bool IsOpen => mode != Mode.Closed;
    ItemRegistry Items => player.Items;

    void Awake()
    {
        controller = GetComponent<PlayerController>();
        player = GetComponent<PlayerInventory>();
    }

    void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || player.Inventory == null) return;

        if (IsOpen)
        {
            if (keyboard.eKey.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame) Close();
        }
        else if (controller.HasFocus && keyboard.eKey.wasPressedThisFrame) Open(Mode.Inventory);
    }

    public void Open(Mode newMode)
    {
        if (player.Inventory == null || newMode == Mode.Closed) return;
        if (IsOpen) ReturnItems();
        mode = newMode;
        gridSize = mode == Mode.CraftingTable ? 3 : 2;
        bottom = new ItemStack[gridSize * gridSize];
        top = new ItemStack[gridSize * gridSize];
        recipePage = 0;
        controller.InputEnabled = false;
        PlayerController.SetCursorLocked(false);
    }

    public void Close()
    {
        ReturnItems();
        mode = Mode.Closed;
        controller.InputEnabled = true;
        PlayerController.SetCursorLocked(true);
    }

    // Anything left in the grids or on the cursor goes back into the inventory
    void ReturnItems()
    {
        ReturnGrid();
        player.Give(cutInput[0]);
        cutInput[0] = ItemStack.Empty;
        player.Give(held);
        held = ItemStack.Empty;
    }

    void ReturnGrid()
    {
        for (int i = 0; i < bottom.Length; i++) { player.Give(bottom[i]); bottom[i] = ItemStack.Empty; }
        for (int i = 0; i < top.Length; i++) { player.Give(top[i]); top[i] = ItemStack.Empty; }
    }

    void OnDisable()
    {
        if (IsOpen && player.Inventory != null) Close();
    }

    // ---------- Results ----------

    CraftingRecipe CurrentRecipe(out ItemStack result)
    {
        result = ItemStack.Empty;
        CraftingRecipe recipe = Crafting.Match(controller.world.Recipes, Items, gridSize, bottom, top);
        if (recipe == null) return null;
        int id = Items.GetId(recipe.result);
        if (id == 0) return null;
        result = new ItemStack(id, recipe.count, Items.Get(id).definition?.durability ?? 0);
        return recipe;
    }

    ItemStack CuttingResult()
    {
        ItemInfo info = Items.Get(cutInput[0].id);
        if (info == null || info.blockId == 0 || info.isSlope) return ItemStack.Empty;
        BlockDefinition block = controller.world.Blocks.Get(info.blockId);
        return block != null && block.slopesInCuttingTable ? new ItemStack(Items.SlopeItem(info.blockId), 2) : ItemStack.Empty;
    }

    ItemStack Result(out CraftingRecipe recipe)
    {
        recipe = null;
        if (mode == Mode.CuttingTable) return CuttingResult();
        recipe = CurrentRecipe(out ItemStack result);
        return result;
    }

    void ConsumeIngredients(CraftingRecipe recipe)
    {
        if (mode == Mode.CuttingTable)
        {
            cutInput[0].count--;
            if (cutInput[0].count <= 0) cutInput[0] = ItemStack.Empty;
        }
        else Crafting.Consume(recipe, Items, gridSize, bottom, top);
    }

    void ClickResult(bool shift)
    {
        if (shift)
        {
            // Craft as many as possible straight into the inventory
            for (int i = 0; i < 256; i++)
            {
                ItemStack result = Result(out CraftingRecipe recipe);
                if (result.IsEmpty) break;
                ConsumeIngredients(recipe);
                player.Give(result);
            }
            return;
        }

        ItemStack r = Result(out CraftingRecipe rec);
        if (r.IsEmpty) return;
        int max = Items.Get(r.id).maxStack;
        if (held.IsEmpty) held = r;
        else if (held.id == r.id && held.count + r.count <= max) held.count += r.count;
        else return;
        ConsumeIngredients(rec);
    }

    // ---------- Recipe book ----------

    List<CraftingRecipe> BookRecipes()
    {
        var list = new List<CraftingRecipe>();
        RecipeDatabase db = controller.world.Recipes;
        if (db == null) return list;
        foreach (CraftingRecipe r in db.recipes)
            if (Items.GetId(r.result) != 0 && FitsGrid(Crafting.Cells(r, Items))) list.Add(r);
        return list;
    }

    bool FitsGrid(List<Crafting.RecipeCell> cells)
    {
        if (cells.Count == 0) return false;
        foreach (var c in cells)
            if (c.id <= 0 || c.x < 0 || c.y < 0 || c.x >= gridSize || c.y >= gridSize) return false;
        return true;
    }

    static Dictionary<int, int> Totals(List<Crafting.RecipeCell> cells)
    {
        var totals = new Dictionary<int, int>();
        foreach (var c in cells) totals[c.id] = (totals.TryGetValue(c.id, out int n) ? n : 0) + c.amount;
        return totals;
    }

    // Counts what's in the inventory plus the grid, since filling a recipe returns the grid first
    int Available(int id)
    {
        int n = player.Inventory.CountOf(id);
        foreach (ItemStack s in bottom) if (s.id == id) n += s.count;
        foreach (ItemStack s in top) if (s.id == id) n += s.count;
        return n;
    }

    /// <summary>How many times the recipe could be laid out from what the player has.</summary>
    int Sets(List<Crafting.RecipeCell> cells)
    {
        int sets = int.MaxValue;
        foreach (var kv in Totals(cells)) sets = Mathf.Min(sets, Available(kv.Key) / kv.Value);
        foreach (var c in cells) sets = Mathf.Min(sets, Items.Get(c.id).maxStack / c.amount);
        return sets == int.MaxValue ? 0 : sets;
    }

    void FillRecipe(CraftingRecipe recipe, bool max)
    {
        var cells = Crafting.Cells(recipe, Items);
        int sets = Sets(cells);
        if (sets <= 0) return;
        if (!max) sets = 1;

        ReturnGrid();
        foreach (var c in cells)
        {
            int taken = player.Inventory.Remove(c.id, c.amount * sets);
            if (taken <= 0) continue;
            ItemStack[] grid = c.top ? top : bottom;
            grid[c.x + c.y * gridSize] = new ItemStack(c.id, taken);
        }
    }

    string DescribeRecipe(CraftingRecipe recipe, List<Crafting.RecipeCell> cells)
    {
        string Layer(bool topLayer)
        {
            var totals = new Dictionary<int, int>();
            foreach (var c in cells)
                if (c.top == topLayer) totals[c.id] = (totals.TryGetValue(c.id, out int n) ? n : 0) + c.amount;
            var parts = new List<string>();
            foreach (var kv in totals) parts.Add($"{kv.Value} {Items.Get(kv.Key).name}");
            return string.Join(", ", parts);
        }

        string text = recipe.count > 1 ? $"{recipe.result} x{recipe.count}" : recipe.result;
        text += $"\nBottom: {Layer(false)}";
        string topText = Layer(true);
        if (topText.Length > 0) text += $"\nTop: {topText}";
        return text;
    }

    // ---------- Slot clicks ----------

    void ClickSlot(ItemStack[] slots, int i, int button, bool shift, System.Action<ItemStack[], int> quickMove)
    {
        ref ItemStack slot = ref slots[i];
        if (button == 0)
        {
            if (shift && !slot.IsEmpty && quickMove != null) quickMove(slots, i);
            else if (held.IsEmpty) { held = slot; slot = ItemStack.Empty; }
            else if (slot.IsEmpty) { slot = held; held = ItemStack.Empty; }
            else if (slot.id == held.id && Items.Get(slot.id).maxStack > 1)
            {
                int moved = Mathf.Min(Items.Get(slot.id).maxStack - slot.count, held.count);
                slot.count += moved;
                held.count -= moved;
                if (held.count <= 0) held = ItemStack.Empty;
            }
            else (slot, held) = (held, slot);
        }
        else if (button == 1)
        {
            if (held.IsEmpty && !slot.IsEmpty)
            {
                int half = (slot.count + 1) / 2;
                held = slot;
                held.count = half;
                slot.count -= half;
                if (slot.count <= 0) slot = ItemStack.Empty;
            }
            else if (!held.IsEmpty && (slot.IsEmpty || (slot.id == held.id && slot.count < Items.Get(slot.id).maxStack)))
            {
                if (slot.IsEmpty) slot = new ItemStack(held.id, 0, held.durability);
                slot.count++;
                held.count--;
                if (held.count <= 0) held = ItemStack.Empty;
            }
        }
    }

    // Hotbar <-> main inventory
    void QuickMoveInventory(ItemStack[] slots, int i)
    {
        bool fromHotbar = i < Inventory.HotbarSize;
        slots[i] = MoveInto(slots[i], fromHotbar ? Inventory.HotbarSize : 0, fromHotbar ? Inventory.Size : Inventory.HotbarSize);
    }

    // Grid -> inventory
    void QuickMoveToInventory(ItemStack[] slots, int i) => slots[i] = player.Inventory.Add(slots[i]);

    ItemStack MoveInto(ItemStack stack, int from, int to)
    {
        ItemStack[] slots = player.Inventory.slots;
        int max = Items.Get(stack.id)?.maxStack ?? 64;
        for (int i = from; i < to && stack.count > 0; i++)
            if (slots[i].id == stack.id && slots[i].count < max)
            {
                int moved = Mathf.Min(max - slots[i].count, stack.count);
                slots[i].count += moved;
                stack.count -= moved;
            }
        for (int i = from; i < to && stack.count > 0; i++)
            if (slots[i].IsEmpty)
            {
                slots[i] = stack;
                stack = ItemStack.Empty;
            }
        return stack.count > 0 ? stack : ItemStack.Empty;
    }

    // ---------- Drawing ----------

    void OnGUI()
    {
        if (player.Inventory == null) return;
        GUI.depth = -1;
        scale = Screen.height / 720f;
        EnsureStyles();
        hoverText = null;
        hoverSlots = null;

        if (IsOpen) DrawWindow();
        else DrawHud();

        GUI.color = Color.white;
    }

    void EnsureStyles()
    {
        countStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.LowerRight, fontStyle = FontStyle.Bold, padding = new RectOffset() };
        labelStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, padding = new RectOffset() };
        titleStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        tooltipStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperLeft, padding = new RectOffset(), richText = true };
        headerStyle ??= new GUIStyle(titleStyle) { alignment = TextAnchor.MiddleLeft };
        hintStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true };
        headerStyle.fontSize = Mathf.RoundToInt(15f * scale);
        hintStyle.fontSize = Mathf.RoundToInt(11f * scale);
        countStyle.fontSize = Mathf.RoundToInt(13f * scale);
        labelStyle.fontSize = Mathf.RoundToInt(12f * scale);
        titleStyle.fontSize = Mathf.RoundToInt(15f * scale);
        tooltipStyle.fontSize = Mathf.RoundToInt(12f * scale);
    }

    void DrawHud()
    {
        // Crosshair
        float len = 9f * scale, thick = 2f * scale;
        var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        Fill(new Rect(center.x - len, center.y - thick * 0.5f, len * 2f, thick), new Color(1f, 1f, 1f, 0.85f));
        Fill(new Rect(center.x - thick * 0.5f, center.y - len, thick, len * 2f), new Color(1f, 1f, 1f, 0.85f));

        float s = HotbarSlotSize * scale;
        float x0 = (Screen.width - Inventory.HotbarSize * s) * 0.5f, y0 = Screen.height - s - 8f * scale;
        var bar = new Rect(x0 - 2f * scale, y0 - 2f * scale, Inventory.HotbarSize * s + 4f * scale, s + 4f * scale);
        Fill(bar, new Color(0f, 0f, 0f, 0.45f));
        for (int i = 0; i < Inventory.HotbarSize; i++)
        {
            var r = new Rect(x0 + i * s, y0, s, s);
            Frame(r, new Color(0.55f, 0.55f, 0.55f, 0.9f), scale);
            DrawItem(r, player.Inventory.slots[i]);
        }

        // Selected slot gets a thick bright frame that pokes out of the bar
        float grow = 3f * scale;
        var sel = new Rect(x0 + player.SelectedSlot * s - grow, y0 - grow, s + grow * 2f, s + grow * 2f);
        Frame(sel, Color.white, 3f * scale);
        Frame(new Rect(sel.x - scale, sel.y - scale, sel.width + 2f * scale, sel.height + 2f * scale), new Color(0f, 0f, 0f, 0.6f), scale);

        // Name of the held item for a moment after switching
        float since = Time.time - player.SlotChangedTime;
        ItemInfo info = player.SelectedInfo;
        if (since < 2f && info != null)
        {
            var nameRect = new Rect(0f, y0 - 52f * scale, Screen.width, 24f * scale);
            float alpha = Mathf.Clamp01(2f - since);
            GUI.color = new Color(0f, 0f, 0f, alpha * 0.8f);
            GUI.Label(new Rect(nameRect.x + scale, nameRect.y + scale, nameRect.width, nameRect.height), info.name, titleStyle);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(nameRect, info.name, titleStyle);
        }

        if (Time.timeSinceLevelLoad < controlsHintTime)
        {
            const string controls = "WASD move, Space jump/swim, Shift sneak, Ctrl sprint\n" +
                                    "Left click mine, Right click place/use, Middle click pick block\n" +
                                    "E inventory, L achievements, Q drop, 1-9 / wheel hotbar, Esc free mouse";
            float x = 10f * scale, y = 10f * scale, width = 300f * scale, pad = 7f * scale;
            float h = hintStyle.CalcHeight(new GUIContent(controls), width - pad * 2f);
            float alpha = Mathf.Clamp01(controlsHintTime - Time.timeSinceLevelLoad);
            Fill(new Rect(x, y, width, h + pad * 2f), new Color(0f, 0f, 0f, 0.5f * alpha));
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(new Rect(x + pad, y + pad, width - pad * 2f, h), controls, hintStyle);
        }
    }

    void DrawWindow()
    {
        Event e = Event.current;
        bool click = e.type == EventType.MouseDown;
        float s = HotbarSlotSize * scale, pad = 10f * scale, gap = 12f * scale, titleH = 22f * scale, labelH = 16f * scale;

        float width = Inventory.HotbarSize * s + pad * 2f;
        float craftH = titleH + labelH + 3f * s;
        float height = pad + craftH + gap + labelH + 3f * s + gap * 0.4f + s + pad;
        bool showBook = mode != Mode.CuttingTable;
        float bookWidth = showBook ? 4f * s + pad * 2f : 0f;
        float bookGap = showBook ? 6f * scale : 0f;
        float totalWidth = width + bookWidth + bookGap;
        float left = (Screen.width - totalWidth) * 0.5f, topY = (Screen.height - height) * 0.5f;
        var panel = new Rect(left + bookWidth + bookGap, topY, width, height);
        var book = new Rect(left, topY, bookWidth, height);

        Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.55f));
        Panel(panel);
        if (showBook) Panel(book);

        string title = mode switch
        {
            Mode.CraftingTable => "Crafting Table",
            Mode.CuttingTable => "Cutting Table",
            _ => "Inventory"
        };
        Label(new Rect(panel.x + pad, panel.y + pad * 0.5f, panel.width, titleH), title, true);

        float x = panel.x + pad, y = panel.y + pad * 0.5f + titleH;
        if (mode == Mode.CuttingTable) DrawCutting(x, y, s, labelH, panel.width - pad * 2f);
        else DrawCrafting(x, y, s, labelH, gap);

        // Main inventory, then the hotbar row
        y = panel.y + pad + craftH + gap;
        Label(new Rect(x, y, panel.width, labelH), "Inventory");
        y += labelH;
        ItemStack[] slots = player.Inventory.slots;
        for (int row = 0; row < 3; row++)
        for (int col = 0; col < Inventory.HotbarSize; col++)
            SlotButton(new Rect(x + col * s, y + row * s, s, s), slots, Inventory.HotbarSize + row * Inventory.HotbarSize + col, QuickMoveInventory);
        y += 3f * s + gap * 0.4f;
        for (int col = 0; col < Inventory.HotbarSize; col++)
            SlotButton(new Rect(x + col * s, y, s, s), slots, col, QuickMoveInventory);

        if (showBook) DrawRecipeBook(book, s, pad, titleH);

        // 1-9 over a slot swaps it with that hotbar slot
        if (e.type == EventType.KeyDown && hoverSlots != null && e.keyCode >= KeyCode.Alpha1 && e.keyCode <= KeyCode.Alpha9)
        {
            int hotbar = e.keyCode - KeyCode.Alpha1;
            if (!(hoverSlots == slots && hoverIndex == hotbar))
                (hoverSlots[hoverIndex], slots[hotbar]) = (slots[hotbar], hoverSlots[hoverIndex]);
            e.Use();
        }

        // Clicking outside the windows throws the held stack
        if (click && !panel.Contains(e.mousePosition) && !book.Contains(e.mousePosition) && !held.IsEmpty)
        {
            ItemStack thrown = held;
            if (e.button == 1) { thrown.count = 1; held.count--; }
            else held.count = 0;
            if (held.count <= 0) held = ItemStack.Empty;
            player.Throw(thrown);
            e.Use();
        }

        Vector2 m = e.mousePosition;
        if (!held.IsEmpty) DrawItem(new Rect(m.x - s * 0.5f, m.y - s * 0.5f, s, s), held);
        else if (hoverText != null) DrawTooltip(m, hoverText);
    }

    void DrawCrafting(float x, float y, float s, float labelH, float gap)
    {
        float grid = gridSize * s;
        Label(new Rect(x, y, grid + gap, labelH), "Bottom layer");
        Label(new Rect(x + grid + gap, y, grid * 2f, labelH), "Top layer (variants)");
        y += labelH;
        // Center the grids vertically in the 3-row area
        float gy = y + (3 - gridSize) * s * 0.5f;

        for (int row = 0; row < gridSize; row++)
        for (int col = 0; col < gridSize; col++)
        {
            int i = col + row * gridSize;
            SlotButton(new Rect(x + col * s, gy + row * s, s, s), bottom, i, QuickMoveToInventory);
            SlotButton(new Rect(x + grid + gap + col * s, gy + row * s, s, s), top, i, QuickMoveToInventory, TopLayerColor);
        }

        float ax = x + 2f * grid + gap * 2f;
        Arrow(new Rect(ax, y + s, s, s));
        ResultButton(new Rect(ax + s + gap * 0.5f, y + s - 4f * scale, s + 8f * scale, s + 8f * scale));
    }

    void DrawCutting(float x, float y, float s, float labelH, float width)
    {
        Label(new Rect(x, y, width, labelH), "1 block = 2 slopes");
        y += labelH + s;
        float cx = x + (width - 3.5f * s) * 0.5f;
        SlotButton(new Rect(cx, y, s, s), cutInput, 0, QuickMoveToInventory);
        Arrow(new Rect(cx + s * 1.1f, y, s, s));
        ResultButton(new Rect(cx + 2.3f * s, y - 4f * scale, s + 8f * scale, s + 8f * scale));
    }

    void DrawRecipeBook(Rect book, float s, float pad, float titleH)
    {
        Event e = Event.current;
        Label(new Rect(book.x + pad, book.y + pad * 0.5f, book.width, titleH), "Recipes", true);

        List<CraftingRecipe> recipes = BookRecipes();
        // Things you can make right now come first
        var entries = new List<(CraftingRecipe recipe, List<Crafting.RecipeCell> cells, int sets)>();
        foreach (CraftingRecipe r in recipes)
        {
            var cells = Crafting.Cells(r, Items);
            entries.Add((r, cells, Sets(cells)));
        }
        entries.Sort((a, b) => (b.sets > 0).CompareTo(a.sets > 0));

        const int columns = 4;
        float navH = 22f * scale;
        int rows = Mathf.Max(1, Mathf.FloorToInt((book.height - pad * 2f - titleH - navH) / s));
        int perPage = rows * columns;
        int pages = Mathf.Max(1, Mathf.CeilToInt(entries.Count / (float)perPage));
        recipePage = Mathf.Clamp(recipePage, 0, pages - 1);

        if (entries.Count == 0)
        {
            Label(new Rect(book.x + pad, book.y + pad + titleH, book.width - pad * 2f, labelStyle.fontSize * 3f),
                controller.world.Recipes == null || controller.world.Recipes.recipes.Count == 0
                    ? "No recipes yet.\nRun Tools/Voxel/\nAdd Survival Basics" : "Nothing fits\nthis grid");
        }

        float gx = book.x + pad, gy = book.y + pad * 0.5f + titleH;
        for (int i = 0; i < perPage; i++)
        {
            int index = recipePage * perPage + i;
            if (index >= entries.Count) break;
            var (recipe, cells, sets) = entries[index];
            var r = new Rect(gx + (i % columns) * s, gy + (i / columns) * s, s, s);
            int resultId = Items.GetId(recipe.result);

            Slot(r, sets > 0 ? SlotColor : new Color32(110, 90, 90, 255), r.Contains(e.mousePosition));
            DrawItem(r, new ItemStack(resultId, recipe.count), sets > 0 ? 1f : 0.35f);
            if (!r.Contains(e.mousePosition)) continue;

            hoverText = DescribeRecipe(recipe, cells) + (sets > 0 ? "\n<color=#9f9>Click to fill the grid, shift for all</color>" : "\n<color=#f99>Missing ingredients</color>");
            if (e.type == EventType.MouseDown && e.button == 0 && sets > 0)
            {
                FillRecipe(recipe, e.shift);
                e.Use();
            }
        }

        if (pages > 1)
        {
            float ny = book.yMax - pad - navH;
            if (NavButton(new Rect(book.x + pad, ny, navH * 1.5f, navH), "<")) recipePage = (recipePage + pages - 1) % pages;
            if (NavButton(new Rect(book.xMax - pad - navH * 1.5f, ny, navH * 1.5f, navH), ">")) recipePage = (recipePage + 1) % pages;
            GUI.color = TextColor;
            GUI.Label(new Rect(book.x, ny, book.width, navH), $"{recipePage + 1}/{pages}", titleStyle);
        }
    }

    bool NavButton(Rect r, string text)
    {
        Event e = Event.current;
        bool hover = r.Contains(e.mousePosition);
        Fill(r, hover ? new Color32(170, 170, 200, 255) : SlotColor);
        Bevel(r, PanelLight, SlotDark, scale);
        GUI.color = TextColor;
        GUI.Label(r, text, titleStyle);
        if (!hover || e.type != EventType.MouseDown || e.button != 0) return false;
        e.Use();
        return true;
    }

    void SlotButton(Rect r, ItemStack[] slots, int i, System.Action<ItemStack[], int> quickMove, Color? tint = null)
    {
        Event e = Event.current;
        bool hover = r.Contains(e.mousePosition);
        Slot(r, tint ?? SlotColor, hover);
        DrawItem(r, slots[i]);
        if (!hover) return;

        hoverSlots = slots;
        hoverIndex = i;
        if (!slots[i].IsEmpty) hoverText = Describe(slots[i]);
        if (e.type != EventType.MouseDown) return;
        ClickSlot(slots, i, e.button, e.shift, quickMove);
        e.Use();
    }

    void ResultButton(Rect r)
    {
        ItemStack result = Result(out _);
        Event e = Event.current;
        bool hover = r.Contains(e.mousePosition);
        Slot(r, ResultColor, hover);
        DrawItem(r, result);
        if (!hover) return;
        if (!result.IsEmpty) hoverText = Describe(result);
        if (e.type != EventType.MouseDown || e.button != 0) return;
        ClickResult(e.shift);
        e.Use();
    }

    string Describe(ItemStack stack)
    {
        ItemInfo info = Items.Get(stack.id);
        if (info == null) return null;
        string text = info.name;
        ItemDefinition def = info.definition;
        if (def != null)
        {
            if (!string.IsNullOrWhiteSpace(def.tierName)) text += $"\n<color=#{TierColor(def.tierName)}>{def.tierName}</color>";
            if (def.toolType != ToolType.None)
            {
                text += def.toolType == ToolType.Sword
                    ? $"\n<color=#aaa>Sword, tier {def.toolTier}</color>"
                    : $"\n<color=#aaa>{def.toolType}, tier {def.toolTier}, speed x{def.miningSpeed:0.#}</color>";
                text += $"\n<color=#aaa>{def.attackDamage:0.#} damage, {def.attackSpeed:0.##} hits/s</color>";
            }
            if (def.weaving) text += "\n<color=#fd6>Woven: blocks drop whole</color>";
            if (def.keepDurabilityChance > 0f) text += $"\n<color=#9cf>Blessed: {def.keepDurabilityChance:P0} chance to keep durability</color>";
            if (def.adaptive) text += "\n<color=#c8f>Adaptive: becomes whatever tool the job needs, strikes any weakness</color>";
            if (def.heldBurnPerSecond > 0f)
                text += $"\n<color=#7f3>RADIOACTIVE</color>\n<color=#f84>Searing hot: burns you for {def.heldBurnPerSecond:0.#} a second while held</color>";
            if (def.durability > 0) text += $"\n<color=#aaa>Durability {stack.durability}/{def.durability}</color>";
        }
        else if (info.blockId != 0)
        {
            BlockDefinition block = controller.world.Blocks.Get(info.blockId);
            if (block != null && block.interaction != BlockInteraction.None)
                text += "\n<color=#aaa>Right click to use</color>";
        }
        return text;
    }

    static string TierColor(string tier) => tier.Trim().ToLowerInvariant() switch
    {
        "battle-ready" => "e0e6f0",
        "spiritual" => "8fd8ff",
        "glass cannon" => "ffe36b",
        "battle-hardened" => "c0c8d8",
        "cursed" => "d040ff",
        _ => "aaaaaa"
    };

    // ---------- Drawing helpers ----------

    void DrawTooltip(Vector2 mouse, string text)
    {
        var content = new GUIContent(text);
        float w = 0f;
        foreach (string line in text.Split('\n'))
            w = Mathf.Max(w, tooltipStyle.CalcSize(new GUIContent(StripTags(line))).x);
        float h = tooltipStyle.CalcHeight(content, w + 2f);
        float p = 6f * scale;
        var r = new Rect(mouse.x + 14f * scale, mouse.y - h - p * 2f, w + p * 2f, h + p * 2f);
        if (r.xMax > Screen.width) r.x = mouse.x - r.width - 6f * scale;
        if (r.y < 0f) r.y = mouse.y + 18f * scale;

        Fill(r, new Color32(16, 1, 16, 240));
        Frame(new Rect(r.x + scale, r.y + scale, r.width - 2f * scale, r.height - 2f * scale), new Color32(60, 0, 140, 255), scale);
        GUI.color = Color.white;
        GUI.Label(new Rect(r.x + p, r.y + p, w + 2f, h), content, tooltipStyle);
    }

    static string StripTags(string s) => System.Text.RegularExpressions.Regex.Replace(s, "<.*?>", "");

    void Label(Rect r, string text, bool title = false)
    {
        GUI.color = TextColor;
        GUI.Label(r, text, title ? headerStyle : labelStyle);
    }

    void Arrow(Rect r)
    {
        // A chunky right-pointing arrow
        float u = r.width / 8f;
        Color c = new Color32(140, 140, 140, 255);
        Fill(new Rect(r.x + u, r.y + 3.5f * u, 4f * u, u), c);
        for (int i = 0; i < 3; i++)
            Fill(new Rect(r.x + (5f + i) * u, r.y + (1.5f + i) * u, u, (5f - 2f * i) * u), c);
    }

    void Panel(Rect r) => DrawPanel(r, scale);

    void Slot(Rect r, Color color, bool hover) => DrawSlot(r, color, hover, scale);

    public static void DrawPanel(Rect r, float scale)
    {
        Fill(r, PanelColor);
        Bevel(r, PanelLight, PanelDark, 2f * scale);
        Frame(new Rect(r.x - scale, r.y - scale, r.width + 2f * scale, r.height + 2f * scale), Color.black, scale);
    }

    public static void DrawSlot(Rect r, Color color, bool hover, float scale)
    {
        Fill(r, color);
        // Inset look: dark top-left edge, light bottom-right edge
        Bevel(r, SlotDark, PanelLight, Mathf.Max(1f, scale));
        if (hover) Fill(new Rect(r.x + 2f * scale, r.y + 2f * scale, r.width - 4f * scale, r.height - 4f * scale), new Color(1f, 1f, 1f, 0.3f));
    }

    void DrawItem(Rect r, ItemStack stack, float alpha = 1f)
    {
        if (stack.IsEmpty) return;
        ItemInfo info = Items.Get(stack.id);
        if (info == null) return;

        float inset = r.width * 0.1f;
        var iconRect = new Rect(r.x + inset, r.y + inset, r.width - inset * 2f, r.height - inset * 2f);
        Color glow = info.definition?.glow ?? Color.clear;
        if (glow.a > 0f)
        {
            float pulse = 0.6f + 0.4f * Mathf.Sin(Time.unscaledTime * 4f);
            Fill(new Rect(r.x + scale, r.y + scale, r.width - 2f * scale, r.height - 2f * scale),
                new Color(glow.r, glow.g, glow.b, glow.a * pulse * 0.5f * alpha));
        }
        GUI.color = new Color(1f, 1f, 1f, alpha);
        if (info.icon != null) GUI.DrawTexture(iconRect, info.icon, ScaleMode.ScaleToFit);
        else Fill(iconRect, new Color(0.9f, 0.3f, 0.9f, alpha));

        ItemDefinition def = info.definition;
        if (def != null && def.durability > 0 && stack.durability < def.durability)
        {
            float fraction = Mathf.Clamp01(stack.durability / (float)def.durability);
            var bar = new Rect(iconRect.x + scale, iconRect.yMax - 2f * scale, iconRect.width - 2f * scale, 2f * scale);
            Fill(bar, Color.black);
            Fill(new Rect(bar.x, bar.y, bar.width * fraction, bar.height), Color.Lerp(Color.red, Color.green, fraction));
        }

        if (stack.count > 1)
        {
            var textRect = new Rect(r.x, r.y, r.width - 3f * scale, r.height - 1f * scale);
            string count = stack.count.ToString();
            GUI.color = new Color(0.25f, 0.25f, 0.25f, alpha);
            GUI.Label(new Rect(textRect.x + 1.5f * scale, textRect.y + 1.5f * scale, textRect.width, textRect.height), count, countStyle);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(textRect, count, countStyle);
        }
    }

    public static void Fill(Rect r, Color c)
    {
        GUI.color = c;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
    }

    public static void Frame(Rect r, Color c, float t)
    {
        Fill(new Rect(r.x, r.y, r.width, t), c);
        Fill(new Rect(r.x, r.yMax - t, r.width, t), c);
        Fill(new Rect(r.x, r.y, t, r.height), c);
        Fill(new Rect(r.xMax - t, r.y, t, r.height), c);
    }

    public static void Bevel(Rect r, Color topLeft, Color bottomRight, float t)
    {
        Fill(new Rect(r.x, r.y, r.width, t), topLeft);
        Fill(new Rect(r.x, r.y, t, r.height), topLeft);
        Fill(new Rect(r.x, r.yMax - t, r.width, t), bottomRight);
        Fill(new Rect(r.xMax - t, r.y, t, r.height), bottomRight);
    }
}
