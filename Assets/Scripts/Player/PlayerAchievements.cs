using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public enum AchievementTrigger { ObtainItem, MineBlock, PlaceBlock, ReachBelowY }

[System.Serializable]
public class Achievement
{
    public string title = "";
    [TextArea] public string description = "";
    public AchievementTrigger trigger;
    [Tooltip("Item or block name. Empty = anything. Starting with * matches the end of the name, e.g. \"* Slope\".")]
    public string target = "";
    [Tooltip("How many to hold at once / mine / place. For Reach Below Y, the Y level to get below.")]
    public int count = 1;
    [Tooltip("Item shown as the icon. Empty = the target.")]
    public string icon = "";
}

/// <summary>
/// Achievements unlock in any order and pop up a toast when earned. L opens the list. Achievements naming items that
/// don't exist in the world are left out. Progress lasts for the session.
/// </summary>
[RequireComponent(typeof(PlayerController), typeof(PlayerInventory))]
public class PlayerAchievements : MonoBehaviour
{
    public bool showToasts = true;
    public float toastTime = 4f;

    public List<Achievement> achievements = new()
    {
        new() { title = "Timber!", description = "Hold left click on a tree until a Log drops", target = "Log" },
        new() { title = "Benchmarking", description = "Craft a Crafting Table from 4 Planks", target = "Crafting Table" },
        new() { title = "Time to Mine!", description = "Make a Wooden Pickaxe at a Crafting Table", target = "Wooden Pickaxe" },
        new() { title = "Stone Age", description = "Mine Stone with a pickaxe to get Cobblestone", target = "Cobblestone" },
        new() { title = "Getting an Upgrade", description = "Craft a Stone Pickaxe", target = "Stone Pickaxe" },
        new() { title = "Lumberjack", description = "Chop 64 Logs", trigger = AchievementTrigger.MineBlock, target = "Log", count = 64 },
        new() { title = "Cutting Edge", description = "Crafting Table pattern on the bottom layer, Cobblestone on the top layer", target = "Cutting Table" },
        new() { title = "Slippery Slope", description = "Place a slope. It turns into a corner next to other slopes", trigger = AchievementTrigger.PlaceBlock, target = "* Slope", icon = "Cobblestone Slope" },
        new() { title = "Into the Depths", description = "Dig below Y 16", trigger = AchievementTrigger.ReachBelowY, count = 16, icon = "Stone" },
        new() { title = "Acquire Hardware", description = "Mine Iron Ore with a Stone Pickaxe", target = "Iron" },
        new() { title = "Isn't It Iron Pick", description = "Craft an Iron Pickaxe", target = "Iron Pickaxe" },
        new() { title = "Bucket Brigade", description = "Scoop up water with a Bucket", target = "Water Bucket" },
        new() { title = "Spiritual Matter", description = "Mine Topaz Ore below Y 20 with an Iron Pickaxe", target = "Topaz" },
        new() { title = "Hoarder of Threads", description = "Hold 36 String at once, enough to weave a pickaxe", target = "String", count = 36 },
        new() { title = "Master Weaver", description = "Weave a Topaz Pickaxe: 4 String in every top layer cell", target = "Weaving Topaz Pickaxe" },
    };

    PlayerController controller;
    PlayerInventory inventory;
    PlayerInteraction interaction;
    bool[] unlocked;
    int[] progress;
    List<int> visible;
    readonly Queue<int> toastQueue = new();
    int toast = -1;
    float toastStart;
    float nextCheck;
    bool open;
    int page;
    float scale;
    GUIStyle titleStyle, textStyle, smallStyle, centerStyle;

    public bool IsOpen => open;
    public int UnlockedCount { get; private set; }
    public int VisibleCount => visible?.Count ?? 0;

    void Awake()
    {
        controller = GetComponent<PlayerController>();
        inventory = GetComponent<PlayerInventory>();
        interaction = GetComponent<PlayerInteraction>();
    }

    void OnEnable()
    {
        if (interaction == null) return;
        interaction.BlockBroken += OnBlockBroken;
        interaction.BlockPlaced += OnBlockPlaced;
    }

    void OnDisable()
    {
        if (interaction != null)
        {
            interaction.BlockBroken -= OnBlockBroken;
            interaction.BlockPlaced -= OnBlockPlaced;
        }
        if (open) Close();
    }

    ItemRegistry Items => inventory.Items;

    bool Ready()
    {
        if (inventory.Inventory == null || Items == null) return false;
        if (visible != null) return true;

        unlocked = new bool[achievements.Count];
        progress = new int[achievements.Count];
        visible = new List<int>();
        for (int i = 0; i < achievements.Count; i++)
        {
            string target = achievements[i].target;
            bool specific = !string.IsNullOrWhiteSpace(target) && !target.StartsWith("*");
            if (!specific || Items.GetId(target) != 0) visible.Add(i);
        }
        return true;
    }

    void Update()
    {
        if (!Ready()) return;

        Keyboard keyboard = Keyboard.current;
        if (keyboard != null)
        {
            if (open && (keyboard.lKey.wasPressedThisFrame || keyboard.escapeKey.wasPressedThisFrame)) Close();
            else if (!open && controller.HasFocus && keyboard.lKey.wasPressedThisFrame) Open();
        }

        if (Time.time < nextCheck) return;
        nextCheck = Time.time + 0.25f;
        foreach (int i in visible)
        {
            Achievement a = achievements[i];
            if (unlocked[i]) continue;
            if (a.trigger == AchievementTrigger.ObtainItem)
            {
                progress[i] = Mathf.Max(progress[i], CountHeld(a.target));
                if (progress[i] >= Mathf.Max(1, a.count)) Unlock(i);
            }
            else if (a.trigger == AchievementTrigger.ReachBelowY && transform.position.y < a.count) Unlock(i);
        }
    }

    int CountHeld(string target)
    {
        int n = 0;
        foreach (ItemStack s in inventory.Inventory.slots)
            if (!s.IsEmpty && Matches(target, Items.Get(s.id))) n += s.count;
        return n;
    }

    static bool Matches(string target, ItemInfo item)
    {
        if (item == null) return false;
        if (string.IsNullOrWhiteSpace(target)) return true;
        const System.StringComparison ignoreCase = System.StringComparison.OrdinalIgnoreCase;
        return target.StartsWith("*")
            ? item.name.EndsWith(target.Substring(1).Trim(), ignoreCase)
            : string.Equals(item.name, target.Trim(), ignoreCase);
    }

    void OnBlockBroken(ItemInfo item) => Count(AchievementTrigger.MineBlock, item);

    void OnBlockPlaced(ItemInfo item) => Count(AchievementTrigger.PlaceBlock, item);

    void Count(AchievementTrigger trigger, ItemInfo item)
    {
        if (!Ready()) return;
        foreach (int i in visible)
        {
            Achievement a = achievements[i];
            if (unlocked[i] || a.trigger != trigger || !Matches(a.target, item)) continue;
            progress[i]++;
            if (progress[i] >= Mathf.Max(1, a.count)) Unlock(i);
        }
    }

    public void Unlock(int index)
    {
        if (!Ready() || index < 0 || index >= achievements.Count || unlocked[index]) return;
        unlocked[index] = true;
        UnlockedCount++;
        if (showToasts) toastQueue.Enqueue(index);
    }

    public bool IsUnlocked(int index) => unlocked != null && index >= 0 && index < unlocked.Length && unlocked[index];

    void Open()
    {
        open = true;
        page = 0;
        controller.InputEnabled = false;
        PlayerController.SetCursorLocked(false);
    }

    void Close()
    {
        open = false;
        controller.InputEnabled = true;
        PlayerController.SetCursorLocked(true);
    }

    // ---------- Drawing ----------

    void OnGUI()
    {
        if (visible == null) return;
        GUI.depth = -2;
        scale = Screen.height / 720f;
        EnsureStyles();
        if (open) DrawList();
        DrawToast();
        GUI.color = Color.white;
    }

    void EnsureStyles()
    {
        titleStyle ??= new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, padding = new RectOffset(), clipping = TextClipping.Clip };
        textStyle ??= new GUIStyle(GUI.skin.label) { wordWrap = true, padding = new RectOffset() };
        smallStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleRight, padding = new RectOffset() };
        centerStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        titleStyle.fontSize = Mathf.RoundToInt(13f * scale);
        textStyle.fontSize = Mathf.RoundToInt(11f * scale);
        smallStyle.fontSize = Mathf.RoundToInt(11f * scale);
        centerStyle.fontSize = Mathf.RoundToInt(15f * scale);
    }

    void DrawList()
    {
        const int columns = 2, rows = 6;
        float pad = 10f * scale, titleH = 26f * scale, cardW = 250f * scale, cardH = 52f * scale, gap = 6f * scale, navH = 22f * scale;
        int perPage = columns * rows;
        int pages = Mathf.Max(1, Mathf.CeilToInt(visible.Count / (float)perPage));
        page = Mathf.Clamp(page, 0, pages - 1);

        float width = pad * 2f + columns * cardW + (columns - 1) * gap;
        float height = pad + titleH + rows * cardH + (rows - 1) * gap + pad + (pages > 1 ? navH + gap : 0f);
        var panel = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);

        InventoryUI.Fill(new Rect(0f, 0f, Screen.width, Screen.height), new Color(0f, 0f, 0f, 0.55f));
        InventoryUI.DrawPanel(panel, scale);

        GUI.color = InventoryUI.TextColor;
        centerStyle.alignment = TextAnchor.MiddleLeft;
        GUI.Label(new Rect(panel.x + pad, panel.y + pad * 0.5f, width, titleH), "Achievements", centerStyle);
        centerStyle.alignment = TextAnchor.MiddleCenter;
        GUI.Label(new Rect(panel.x, panel.y + pad * 0.5f, width - pad, titleH), $"{UnlockedCount}/{visible.Count}", smallStyle);

        for (int n = 0; n < perPage; n++)
        {
            int k = page * perPage + n;
            if (k >= visible.Count) break;
            var r = new Rect(panel.x + pad + (n % columns) * (cardW + gap), panel.y + pad * 0.5f + titleH + (n / columns) * (cardH + gap), cardW, cardH);
            DrawCard(r, visible[k]);
        }

        if (pages > 1)
        {
            float ny = panel.yMax - pad - navH;
            if (NavButton(new Rect(panel.x + pad, ny, navH * 1.5f, navH), "<")) page = (page + pages - 1) % pages;
            if (NavButton(new Rect(panel.xMax - pad - navH * 1.5f, ny, navH * 1.5f, navH), ">")) page = (page + 1) % pages;
            GUI.color = InventoryUI.TextColor;
            GUI.Label(new Rect(panel.x, ny, panel.width, navH), $"{page + 1}/{pages}", centerStyle);
        }
    }

    void DrawCard(Rect r, int i)
    {
        Achievement a = achievements[i];
        bool done = unlocked[i];
        InventoryUI.DrawSlot(r, done ? new Color32(120, 150, 100, 255) : InventoryUI.SlotColor, false, scale);

        float p = 6f * scale, icon = r.height - p * 2f;
        DrawIcon(new Rect(r.x + p, r.y + p, icon, icon), a, done ? 1f : 0.3f);

        float tx = r.x + p * 2f + icon, tw = r.xMax - tx - p;
        float lineH = titleStyle.fontSize * 1.3f;
        GUI.color = done ? new Color(1f, 1f, 0.6f) : new Color(0.85f, 0.85f, 0.85f);
        GUI.Label(new Rect(tx, r.y + p * 0.6f, tw, lineH), a.title, titleStyle);
        GUI.color = done ? Color.white : new Color(0.8f, 0.8f, 0.8f);
        GUI.Label(new Rect(tx, r.y + p * 0.6f + lineH, tw, r.height - lineH - p), a.description, textStyle);

        // Counting achievements show how far along they are
        int goal = Mathf.Max(1, a.count);
        if (!done && goal > 1 && a.trigger != AchievementTrigger.ReachBelowY)
        {
            float fraction = Mathf.Clamp01(progress[i] / (float)goal);
            var bar = new Rect(tx, r.yMax - p * 0.5f - 3f * scale, tw - 44f * scale, 3f * scale);
            InventoryUI.Fill(bar, new Color(0f, 0f, 0f, 0.5f));
            InventoryUI.Fill(new Rect(bar.x, bar.y, bar.width * fraction, bar.height), new Color(0.5f, 1f, 0.4f));
            GUI.color = Color.white;
            GUI.Label(new Rect(tx, bar.y - 6f * scale, tw, 14f * scale), $"{Mathf.Min(progress[i], goal)}/{goal}", smallStyle);
        }
    }

    void DrawIcon(Rect r, Achievement a, float alpha)
    {
        string name = string.IsNullOrWhiteSpace(a.icon) ? a.target : a.icon;
        ItemInfo info = Items.Get(string.IsNullOrWhiteSpace(name) || name.StartsWith("*") ? 0 : Items.GetId(name));
        GUI.color = new Color(1f, 1f, 1f, alpha);
        if (info?.icon != null) GUI.DrawTexture(r, info.icon, ScaleMode.ScaleToFit);
        else
        {
            var gem = new Rect(r.x + r.width * 0.25f, r.y + r.height * 0.25f, r.width * 0.5f, r.height * 0.5f);
            InventoryUI.Fill(gem, new Color(1f, 0.8f, 0.25f, alpha));
            InventoryUI.Frame(gem, new Color(0.5f, 0.35f, 0f, alpha), Mathf.Max(1f, scale));
        }
    }

    void DrawToast()
    {
        if (toast < 0 && toastQueue.Count > 0)
        {
            toast = toastQueue.Dequeue();
            toastStart = Time.time;
        }
        if (toast < 0) return;

        float t = Time.time - toastStart;
        if (t > toastTime)
        {
            toast = -1;
            return;
        }

        // Slides down from the top right corner, then back up
        const float slide = 0.3f;
        float shown = Mathf.Clamp01(Mathf.Min(t, toastTime - t) / slide);
        float w = 230f * scale, h = 46f * scale, p = 6f * scale;
        var r = new Rect(Screen.width - w - 10f * scale, Mathf.Lerp(-h, 10f * scale, shown), w, h);

        InventoryUI.Fill(r, new Color32(33, 33, 33, 235));
        InventoryUI.Frame(r, new Color32(85, 85, 85, 255), 2f * scale);
        float icon = h - p * 2f;
        DrawIcon(new Rect(r.x + p, r.y + p, icon, icon), achievements[toast], 1f);

        float tx = r.x + p * 2f + icon, tw = r.xMax - tx - p, lineH = titleStyle.fontSize * 1.3f;
        GUI.color = new Color(1f, 1f, 0.35f);
        GUI.Label(new Rect(tx, r.y + p, tw, lineH), "Achievement Get!", titleStyle);
        GUI.color = Color.white;
        GUI.Label(new Rect(tx, r.y + p + lineH, tw, lineH), achievements[toast].title, titleStyle);
    }

    bool NavButton(Rect r, string text)
    {
        Event e = Event.current;
        bool hover = r.Contains(e.mousePosition);
        InventoryUI.Fill(r, hover ? new Color32(170, 170, 200, 255) : InventoryUI.SlotColor);
        InventoryUI.Bevel(r, InventoryUI.PanelLight, InventoryUI.SlotDark, scale);
        GUI.color = InventoryUI.TextColor;
        GUI.Label(r, text, centerStyle);
        if (!hover || e.type != EventType.MouseDown || e.button != 0) return false;
        e.Use();
        return true;
    }
}
