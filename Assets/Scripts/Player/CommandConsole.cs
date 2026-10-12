using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// A chat-style command line for testing. T or / opens it, Enter runs the command, Esc closes it, Up / Down step
/// through earlier commands. Type /help for the list. Other scripts can add their own with Register.
/// </summary>
[RequireComponent(typeof(PlayerController))]
public class CommandConsole : MonoBehaviour
{
    [Tooltip("Seconds messages stay on screen after the console closes.")]
    public float messageTime = 8f;

    public bool IsOpen { get; private set; }

    class Command
    {
        public string usage, description;
        public System.Action<string[]> run;
    }

    readonly Dictionary<string, Command> commands = new();
    readonly List<(string text, float time)> log = new();
    readonly List<string> history = new();
    PlayerController controller;
    PlayerHealth health;
    PlayerInventory inventory;
    string input = "";
    int historyIndex;
    int openFrame;
    GUIStyle style;

    VoxelWorld World => controller.world;

    void Awake()
    {
        controller = GetComponent<PlayerController>();
        health = GetComponent<PlayerHealth>();
        inventory = GetComponent<PlayerInventory>();

        Register("help", "/help", "Lists the commands.", _ => Help());
        Register("summon", "/summon bear [count] [enraged]", "Spawns bears in front of you. Enraged ones start in phase 2.", Summon);
        Register("kill", "/kill [bears|me]", "Kills every bear, or you.", Kill);
        Register("heal", "/heal", "Refills your health and air.", _ =>
        {
            health.Heal(health.maxHealth);
            Print("Healed.");
        });
        Register("god", "/god", "Toggles taking no damage.", _ =>
        {
            health.Invulnerable = !health.Invulnerable;
            Print(health.Invulnerable ? "God mode on." : "God mode off.");
        });
        Register("give", "/give <item name> [count]", "Puts items in your inventory. Partial names work.", Give);
        Register("tp", "/tp <x> <y> <z>", "Teleports you. ~ means where you are now, e.g. /tp ~ ~10 ~.", Teleport);
        Register("goto", "/goto <biome>", "Teleports you to the nearest place with that biome.", GoTo);
        Register("where", "/where", "Shows your position and biome.", _ => Where());
        Register("clear", "/clear", "Clears the messages.", _ => log.Clear());
    }

    /// <summary>Adds a command. name is what's typed after the slash, run gets the words after it.</summary>
    public void Register(string name, string usage, string description, System.Action<string[]> run) =>
        commands[name.ToLowerInvariant()] = new Command { usage = usage, description = description, run = run };

    public void Print(string message) => log.Add((message, Time.time));

    void Update()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || IsOpen || !controller.HasFocus) return;
        if (keyboard.tKey.wasPressedThisFrame) Open("");
        else if (keyboard.slashKey.wasPressedThisFrame || keyboard.numpadDivideKey.wasPressedThisFrame) Open("/");
    }

    void Open(string text)
    {
        IsOpen = true;
        input = text;
        historyIndex = history.Count;
        openFrame = Time.frameCount;
        controller.InputEnabled = false;
    }

    void Close()
    {
        IsOpen = false;
        input = "";
        controller.InputEnabled = true;
    }

    void Run(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return;
        history.Add(line);
        Print("<color=#aaa>" + line + "</color>");

        string[] words = line.TrimStart('/').Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return;
        if (!commands.TryGetValue(words[0].ToLowerInvariant(), out Command command))
        {
            Print($"<color=#f77>Unknown command '{words[0]}'. Try /help.</color>");
            return;
        }
        var args = new string[words.Length - 1];
        System.Array.Copy(words, 1, args, 0, args.Length);
        try
        {
            command.run(args);
        }
        catch (System.Exception e)
        {
            Print($"<color=#f77>{e.Message}</color>");
            Debug.LogException(e);
        }
    }

    // ---------- Commands ----------

    void Help()
    {
        var names = new List<string>(commands.Keys);
        names.Sort();
        foreach (string n in names) Print($"<b>{commands[n].usage}</b>  {commands[n].description}");
    }

    void Summon(string[] args)
    {
        if (args.Length == 0 || !args[0].Equals("bear", System.StringComparison.OrdinalIgnoreCase))
        {
            Print("Usage: /summon bear [count] [enraged]");
            return;
        }
        int count = 1;
        bool enraged = false;
        for (int i = 1; i < args.Length; i++)
        {
            if (int.TryParse(args[i], out int n)) count = Mathf.Clamp(n, 1, 10);
            else if (args[i].StartsWith("enrage", System.StringComparison.OrdinalIgnoreCase) || args[i] == "2") enraged = true;
        }

        Vector3 forward = controller.Forward;
        for (int i = 0; i < count; i++)
        {
            Vector3 spot = transform.position + forward * 7f + controller.Right * ((i - (count - 1) * 0.5f) * 3.5f);
            int x = Mathf.FloorToInt(spot.x), z = Mathf.FloorToInt(spot.z);
            var feet = new Vector3(x + 0.5f, World != null ? World.GetSurfaceHeight(x, z) + 1.1f : spot.y, z + 0.5f);
            float yaw = Mathf.Atan2(-forward.x, -forward.z) * Mathf.Rad2Deg;
            BearEnemy.Spawn(feet, yaw, enraged);
        }
        Print($"Summoned {count} {(enraged ? "enraged " : "")}bear{(count > 1 ? "s" : "")}.");
    }

    void Kill(string[] args)
    {
        string what = args.Length > 0 ? args[0].ToLowerInvariant() : "bears";
        if (what == "me" || what == "self")
        {
            health.Kill();
            return;
        }
        int n = 0;
        foreach (BearEnemy bear in FindObjectsByType<BearEnemy>())
        {
            if (!bear.IsAlive) continue;
            bear.Kill();
            n++;
        }
        Print($"Killed {n} bear{(n == 1 ? "" : "s")}.");
    }

    void Give(string[] args)
    {
        if (World == null || World.Items == null || args.Length == 0)
        {
            Print("Usage: /give <item name> [count]");
            return;
        }
        int count = 1;
        int nameWords = args.Length;
        if (args.Length > 1 && int.TryParse(args[^1], out int n))
        {
            count = Mathf.Clamp(n, 1, 9999);
            nameWords--;
        }
        string name = string.Join(" ", args, 0, nameWords);

        ItemRegistry items = World.Items;
        int id = items.GetId(name);
        if (id == 0)
        {
            // Partial names: the shortest item name containing what was typed
            int bestLength = int.MaxValue;
            for (int i = 1; i < items.Count; i++)
            {
                string itemName = items.Get(i)?.name;
                if (itemName == null || itemName.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (itemName.Length < bestLength)
                {
                    bestLength = itemName.Length;
                    id = i;
                }
            }
        }
        if (id == 0)
        {
            Print($"<color=#f77>No item called '{name}'.</color>");
            return;
        }
        for (int left = count; left > 0;)
        {
            ItemStack stack = items.CreateStack(id, left);
            if (stack.IsEmpty) break;
            left -= stack.count;
            inventory.Give(stack);
        }
        Print($"Gave {count} {items.Get(id).name}.");
    }

    void Teleport(string[] args)
    {
        if (args.Length != 3)
        {
            Print("Usage: /tp <x> <y> <z>");
            return;
        }
        Vector3 here = transform.position;
        float Coord(string s, float current)
        {
            if (s.StartsWith("~")) return current + (s.Length > 1 ? float.Parse(s.Substring(1), System.Globalization.CultureInfo.InvariantCulture) : 0f);
            return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
        }
        var target = new Vector3(Coord(args[0], here.x), Coord(args[1], here.y), Coord(args[2], here.z));
        controller.Teleport(target);
        Print($"Teleported to {target.x:0.#}, {target.y:0.#}, {target.z:0.#}.");
    }

    void GoTo(string[] args)
    {
        TerrainGenerator gen = World != null ? World.Generator : null;
        if (gen == null || args.Length == 0)
        {
            Print("Usage: /goto <biome>");
            return;
        }
        string query = string.Join(" ", args);
        BiomeConfig biome = null;
        foreach (BiomeConfig b in gen.AllBiomes)
            if (b != null && string.Equals(b.name, query, System.StringComparison.OrdinalIgnoreCase)) biome = b;
        if (biome == null)
            foreach (BiomeConfig b in gen.AllBiomes)
                if (b != null && b.name.IndexOf(query, System.StringComparison.OrdinalIgnoreCase) >= 0) { biome = b; break; }
        if (biome == null)
        {
            var names = new List<string>();
            foreach (BiomeConfig b in gen.AllBiomes) if (b != null) names.Add(b.name);
            Print($"<color=#f77>No biome called '{query}'.</color> Biomes: {string.Join(", ", names)}");
            return;
        }

        // Spiral outward; coasts are thin strips, so they get a finer, shorter search
        bool thin = biome.placement == BiomePlacement.Coast;
        int step = thin ? 12 : 48, maxRadius = thin ? 2500 : 8000;
        int ox = Mathf.FloorToInt(transform.position.x), oz = Mathf.FloorToInt(transform.position.z);
        for (int r = 0; r <= maxRadius; r += step)
        {
            int samples = Mathf.Max(1, Mathf.CeilToInt(2f * Mathf.PI * r / step));
            for (int i = 0; i < samples; i++)
            {
                float a = i * Mathf.PI * 2f / samples;
                int x = ox + Mathf.RoundToInt(Mathf.Cos(a) * r), z = oz + Mathf.RoundToInt(Mathf.Sin(a) * r);
                if (gen.GetBiome(x, z) != biome) continue;
                controller.Teleport(new Vector3(x + 0.5f, World.GetSurfaceHeight(x, z) + 2f, z + 0.5f));
                Print($"Found {biome.name} {r} blocks away, at {x}, {z}.");
                return;
            }
        }
        Print($"<color=#f77>No {biome.name} within {maxRadius} blocks.</color>");
    }

    void Where()
    {
        Vector3 p = transform.position;
        string biome = World != null && World.Generator != null ? World.GetBiome(p).name : "?";
        Print($"{p.x:0.#}, {p.y:0.#}, {p.z:0.#} in {biome}.");
    }

    // ---------- Drawing ----------

    void OnGUI()
    {
        float scale = Screen.height / 720f;
        style ??= new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
        style.fontSize = Mathf.RoundToInt(13f * scale);
        float lineHeight = style.fontSize * 1.45f;
        float width = Mathf.Min(Screen.width * 0.6f, 640f * scale);
        float x = 8f * scale;
        float inputY = Screen.height - 120f * scale;

        if (IsOpen) HandleKeys();

        // Messages above the input line, newest at the bottom; old ones fade once the console is closed
        float y = inputY - 4f * scale;
        for (int i = log.Count - 1; i >= 0 && y > Screen.height * 0.25f; i--)
        {
            float age = Time.time - log[i].time;
            float alpha = IsOpen ? 1f : Mathf.Clamp01(messageTime - age);
            if (alpha <= 0f) continue;
            float h = style.CalcHeight(new GUIContent(log[i].text), width - 8f * scale);
            y -= h;
            GUI.color = new Color(0f, 0f, 0f, 0.45f * alpha);
            GUI.DrawTexture(new Rect(x, y, width, h), Texture2D.whiteTexture);
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.Label(new Rect(x + 4f * scale, y, width - 8f * scale, h), log[i].text, style);
        }
        GUI.color = Color.white;
        if (!IsOpen) return;

        var field = new Rect(x, inputY, width, lineHeight + 4f * scale);
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(field, Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.SetNextControlName("Command Input");
        var fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = style.fontSize };
        input = GUI.TextField(field, input, 200, fieldStyle);
        GUI.FocusControl("Command Input");
    }

    void HandleKeys()
    {
        Event e = Event.current;
        if (e.type != EventType.KeyDown) return;
        // The key that opened the console shouldn't also be typed into it
        if (Time.frameCount - openFrame <= 1 && (e.character == 't' || e.character == 'T' || e.character == '/') && input.Length <= 1)
        {
            if (e.character == '/' && input == "/") e.Use();
            else if (e.character != '/' && input.Length == 0) e.Use();
            return;
        }
        switch (e.keyCode)
        {
            case KeyCode.Return:
            case KeyCode.KeypadEnter:
                string line = input;
                Close();
                Run(line);
                e.Use();
                break;
            case KeyCode.Escape:
                Close();
                e.Use();
                break;
            case KeyCode.UpArrow when history.Count > 0:
                historyIndex = Mathf.Max(0, historyIndex - 1);
                input = history[historyIndex];
                e.Use();
                break;
            case KeyCode.DownArrow when history.Count > 0:
                historyIndex = Mathf.Min(history.Count, historyIndex + 1);
                input = historyIndex < history.Count ? history[historyIndex] : "";
                e.Use();
                break;
        }
    }
}
