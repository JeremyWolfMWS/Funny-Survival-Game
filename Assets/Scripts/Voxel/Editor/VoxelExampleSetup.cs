using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Creates (or tops up) example blocks, biomes, items, recipes, managers and textures in Assets/VoxelData and hooks
/// them up to the scene's Voxel World. Existing assets are kept: only missing blocks, items and recipes are added.
/// "Add Survival Basics To My World" tops up the assets the scene's Voxel World already uses instead.
/// </summary>
public static class VoxelExampleSetup
{
    const string Folder = "Assets/VoxelData";
    const string TextureFolder = Folder + "/Textures";
    const string IconFolder = Folder + "/Icons";
    const int TexSize = 16;

    // Top/bottom fall back to the side texture when null
    static readonly (string name, string side, string top, string bottom, System.Action<BlockDefinition> setup)[] ExampleBlocks =
    {
        ("Grass", "grass_side", "grass_top", "dirt", b => { Soil(b, 0.6f, "Dirt"); b.biomeTint = true; }),
        ("Dirt", "dirt", null, null, b => Soil(b, 0.5f)),
        ("Stone", "stone", null, null, b => Rock(b, 1.5f, 1, "Cobblestone")),
        ("Cobblestone", "cobblestone", null, null, b => Rock(b, 2f, 1)),
        ("Sand", "sand", null, null, b => Soil(b, 0.5f)),
        ("Sandstone", "sandstone", null, null, b => Rock(b, 0.8f, 1)),
        ("Snow", "snow", null, null, b => Soil(b, 0.2f)),
        ("Red Dirt", "red_dirt_side", "red_dirt_top", "red_dirt_bottom", b => Soil(b, 0.5f)),
        ("Red Rock", "red_rock", null, null, b => Rock(b, 1.2f, 1)),
        ("Orange Rock", "orange_rock", null, null, b => Rock(b, 1.2f, 1)),
        ("Dark Red Rock", "dark_red_rock", null, null, b => Rock(b, 1.2f, 1)),
        ("Cream Sandstone", "cream_sandstone", null, null, b => Rock(b, 0.8f, 1)),
        ("Log", "log_side", "log_top", "log_top", b => Wood(b, 2f)),
        ("Leaves", "leaves", null, null, b =>
        {
            b.hardness = 0.2f; b.drop = "String"; b.dropCount = 2; b.dropChance = 0.6f;
            b.waterLoggable = true; b.slopesInCuttingTable = false; b.biomeTint = true;
        }),
        ("Planks", "planks", null, null, b => Wood(b, 2f)),
        ("Crafting Table", "crafting_table_side", "crafting_table_top", "planks", b =>
        {
            Wood(b, 2.5f); b.interaction = BlockInteraction.CraftingTable; b.slopesInCuttingTable = false; b.waterLoggable = false;
        }),
        ("Cutting Table", "cutting_table_side", "cutting_table_top", "planks", b =>
        {
            Wood(b, 2.5f); b.interaction = BlockInteraction.CuttingTable; b.slopesInCuttingTable = false; b.waterLoggable = false;
        }),
        ("Ice", "ice", null, null, b =>
        {
            b.hardness = 0.5f; b.preferredTool = ToolType.Pickaxe; b.slipperiness = 0.9f;
            b.mineableWithoutWeavingPickaxe = false;
        }),
        ("Magma", "magma", null, null, b => { Rock(b, 0.5f, 1); b.damagePerSecond = 2f; b.waterLoggable = false; }),
    };

    static void Soil(BlockDefinition b, float hardness, string drop = "")
    {
        b.hardness = hardness; b.preferredTool = ToolType.Shovel; b.drop = drop; b.waterLoggable = true;
    }

    static void Rock(BlockDefinition b, float hardness, int tier, string drop = "")
    {
        b.hardness = hardness; b.preferredTool = ToolType.Pickaxe; b.requiredTier = tier; b.drop = drop; b.waterLoggable = true;
    }

    static void Wood(BlockDefinition b, float hardness)
    {
        b.hardness = hardness; b.preferredTool = ToolType.Axe; b.waterLoggable = true;
    }

    // Blocks the survival loop needs: soil, stone for stone tools, trees, and the two tables
    static readonly string[] SurvivalBlocks =
    {
        "Dirt", "Stone", "Cobblestone", "Sand", "Log", "Leaves", "Planks", "Crafting Table", "Cutting Table"
    };

    static TreeType Oak(float chance) => new() { shape = TreeShape.Oak, chance = chance, minHeight = 3, maxHeight = 5, leafRadius = 5 };
    static TreeType BigOak(float chance) => new() { shape = TreeShape.Oak, chance = chance, minHeight = 4, maxHeight = 6, leafRadius = 7 };
    static TreeType Pine(float chance) => new() { shape = TreeShape.Pine, chance = chance, minHeight = 6, maxHeight = 9, leafRadius = 2 };

    [MenuItem("Tools/Voxel/Create Example World")]
    static void Create()
    {
        if (!AssetDatabase.IsValidFolder(Folder)) AssetDatabase.CreateFolder("Assets", "VoxelData");

        var db = CreateOrLoad<BlockDatabase>("BlockDatabase", d => d.blocks = new List<BlockDefinition>());
        AddExampleBlocks(db);
        Dictionary<string, Texture2D> textures = SaveTextures(TextureFolder, TexturePainters(), 1000);
        AssignExampleTextures(db, textures);
        if (db.waterTexture == null) db.waterTexture = textures["water"];
        EditorUtility.SetDirty(db);

        var plains = CreateOrLoad<BiomeConfig>("Biome_Plains", b =>
        {
            b.temperature = 0.5f; b.humidity = 0.6f;
            b.baseHeight = 30; b.heightScale = 10; b.noiseScale = 0.015f; b.octaves = 4;
            b.surfaceBlock = "Grass"; b.subSurfaceBlock = "Dirt"; b.subSurfaceDepth = 3; b.deepBlock = "Stone";
            b.slopeChance = 1f;
        });
        AddBiomeFeatures(plains, underwater: "Sand", waterSurface: "", Oak(0.003f), BigOak(0.0008f));

        var desert = CreateOrLoad<BiomeConfig>("Biome_Desert", b =>
        {
            b.temperature = 0.9f; b.humidity = 0.1f;
            b.baseHeight = 28; b.heightScale = 8; b.noiseScale = 0.02f; b.octaves = 3;
            b.surfaceBlock = "Sand"; b.subSurfaceBlock = "Sand"; b.subSurfaceDepth = 4; b.deepBlock = "Sandstone";
            b.slopeChance = 1f;
        });
        AddBiomeFeatures(desert, underwater: "Sand", waterSurface: "");

        // Rolling foothills, with mountain ranges rising up to 60 blocks above them
        var mountains = CreateOrLoad<BiomeConfig>("Biome_Mountains", b =>
        {
            b.temperature = 0.15f; b.humidity = 0.5f;
            b.baseHeight = 32; b.heightScale = 16; b.noiseScale = 0.012f; b.octaves = 4;
            b.mountainHeight = 60; b.mountainNoiseScale = 0.008f; b.mountainCoverage = 0.6f; b.mountainSharpness = 1.5f;
            b.surfaceBlock = "Grass"; b.subSurfaceBlock = "Dirt"; b.subSurfaceDepth = 2; b.deepBlock = "Stone";
            b.peakHeight = 70; b.peakBlock = "Snow";
            b.slopeChance = 0.6f;
        });
        AddBiomeFeatures(mountains, underwater: "Stone", waterSurface: "Ice", Pine(0.025f));

        // Red-rock country: low rolling valleys with stepped buttes showing banded rock on the cliffs
        var sedona = CreateOrLoad<BiomeConfig>("Biome_Sedona", b =>
        {
            b.temperature = 0.75f; b.humidity = 0.4f;
            b.baseHeight = 28; b.heightScale = 8; b.noiseScale = 0.02f; b.octaves = 3;
            b.plateauHeight = 36; b.plateauNoiseScale = 0.01f; b.plateauThreshold = 0.55f;
            b.plateauEdgeWidth = 0.12f; b.plateauSteps = 4;
            b.surfaceBlock = "Red Dirt"; b.subSurfaceBlock = "Red Rock"; b.subSurfaceDepth = 2; b.deepBlock = "Red Rock";
            b.strataBlocks = new[]
            {
                "Red Rock", "Orange Rock", "Red Rock", "Dark Red Rock", "Orange Rock", "Red Rock", "Cream Sandstone"
            };
            b.strataThickness = 3; b.strataWarpScale = 0.02f; b.strataWarpAmount = 3f;
            b.slopeChance = 0.8f;
        });
        AddBiomeFeatures(sedona, underwater: "", waterSurface: "", Oak(0.003f));

        var config = CreateOrLoad<WorldGenConfig>("WorldGenConfig", c =>
        {
            c.blockDatabase = db;
            c.biomes = new List<BiomeConfig> { plains, desert, mountains, sedona };
        });
        var ores = CreateOrLoad<OreDatabase>("Ore Manager", o => o.ores.Add(new OreDefinition
        {
            name = "Magma", blockName = "Magma", drop = OreDrop.OreBlock, requiredTier = 1,
            minY = 1, maxY = 12, frequency = 0.06f, density = 0.6f
        }));
        if (config.oreManager == null)
        {
            config.oreManager = ores;
            EditorUtility.SetDirty(config);
        }

        ItemDatabase items = CreateOrLoad<ItemDatabase>("ItemDatabase", d => d.items = new List<ItemDefinition>());
        AddExampleItems(items, IconFolder);
        RecipeDatabase recipes = CreateOrLoad<RecipeDatabase>("RecipeDatabase", d => d.recipes = new List<CraftingRecipe>());
        AddExampleRecipes(recipes);
        var tools = CreateOrLoad<ToolDatabase>("Tool Manager", t => { });
        Material water = CreateWaterMaterial();

        AssetDatabase.SaveAssets();

        var world = Object.FindAnyObjectByType<VoxelWorld>();
        if (world == null)
        {
            var go = new GameObject("Voxel World");
            world = go.AddComponent<VoxelWorld>();
            Undo.RegisterCreatedObjectUndo(go, "Create Voxel World");
        }
        Undo.RecordObject(world, "Create Example World");
        world.config = config;
        if (world.itemDatabase == null) world.itemDatabase = items;
        if (world.recipeDatabase == null) world.recipeDatabase = recipes;
        if (world.toolManager == null) world.toolManager = tools;
        if (world.waterMaterial == null) world.waterMaterial = water;
        EditorUtility.SetDirty(world);
        AssetDatabase.SaveAssets();
        Selection.activeObject = world.gameObject;
    }

    static T CreateOrLoad<T>(string name, System.Action<T> setup) where T : ScriptableObject =>
        CreateOrLoad(Folder, name, setup, out _);

    static T CreateOrLoad<T>(string folder, string name, System.Action<T> setup, out bool created) where T : ScriptableObject
    {
        string path = $"{folder}/{name}.asset";
        var existing = AssetDatabase.LoadAssetAtPath<T>(path);
        created = existing == null;
        if (existing != null) return existing;

        var asset = ScriptableObject.CreateInstance<T>();
        setup(asset);
        AssetDatabase.CreateAsset(asset, path);
        return asset;
    }

    // Biomes made by older versions of this tool get the water settings once, and trees if they have none
    static void AddBiomeFeatures(BiomeConfig biome, string underwater, string waterSurface, params TreeType[] trees)
    {
        if (string.IsNullOrEmpty(biome.underwaterSurfaceBlock) && string.IsNullOrEmpty(biome.waterSurfaceBlock))
        {
            biome.underwaterSurfaceBlock = underwater;
            biome.waterSurfaceBlock = waterSurface;
        }
        if (biome.trees.Count == 0) biome.trees.AddRange(trees);
        EditorUtility.SetDirty(biome);
    }

    static void AddExampleBlocks(BlockDatabase db)
    {
        foreach (var spec in ExampleBlocks)
        {
            BlockDefinition block = db.blocks.Find(b => string.Equals(b.name?.Trim(), spec.name, System.StringComparison.OrdinalIgnoreCase));
            if (block == null)
            {
                block = new BlockDefinition { name = spec.name };
                db.blocks.Add(block);
            }
            else if (!IsUnconfigured(block)) continue;
            spec.setup(block);
        }
    }

    // Blocks from before block properties existed still have every property at its default
    static bool IsUnconfigured(BlockDefinition b) =>
        Mathf.Approximately(b.hardness, 1f) && b.preferredTool == ToolType.None && b.requiredTier == 0 &&
        string.IsNullOrEmpty(b.drop) && !b.waterLoggable && b.slipperiness == 0f && b.damagePerSecond == 0f &&
        b.interaction == BlockInteraction.None && b.mineableWithoutWeavingPickaxe;

    /// <summary>Fills in example textures for blocks that match by name and don't have any texture yet.</summary>
    static void AssignExampleTextures(BlockDatabase db, Dictionary<string, Texture2D> textures)
    {
        Texture2D Get(string key) => key != null && textures.TryGetValue(key, out var t) ? t : null;

        foreach (var block in db.blocks)
        {
            if (block.sideTexture != null || block.topTexture != null || block.bottomTexture != null) continue;
            foreach (var spec in ExampleBlocks)
            {
                if (spec.name != block.name) continue;
                block.sideTexture = Get(spec.side);
                block.topTexture = Get(spec.top);
                block.bottomTexture = Get(spec.bottom);
                break;
            }
        }
    }

    // ---------- Items and recipes ----------

    // Tools, sticks and ore drops come from the Tool and Ore Managers, so only the rest is listed here
    static readonly (string name, ItemAction action)[] ExampleItems =
    {
        ("String", ItemAction.None), ("Bucket", ItemAction.EmptyBucket), ("Water Bucket", ItemAction.WaterBucket)
    };

    /// <summary>Adds the example items db doesn't have yet, with icons saved into iconFolder. Returns how many were added.</summary>
    static int AddExampleItems(ItemDatabase db, string iconFolder)
    {
        Color stringColor = C(240, 240, 240), iron = C(225, 225, 230);
        var painters = new List<(string name, System.Func<System.Random, Color[]> paint)>
        {
            ("String", r => ProceduralArt.StringIcon(stringColor)),
            ("Bucket", r => ProceduralArt.Bucket(iron, false)),
            ("Water Bucket", r => ProceduralArt.Bucket(iron, true)),
        };
        painters.RemoveAll(p => db.items.Exists(i => string.Equals(i.name?.Trim(), p.name, System.StringComparison.OrdinalIgnoreCase)));
        if (painters.Count == 0) return 0;
        Dictionary<string, Texture2D> icons = SaveTextures(iconFolder, painters, 2000);

        int added = 0;
        foreach (var (name, action) in ExampleItems)
        {
            if (!icons.TryGetValue(name, out Texture2D icon)) continue;
            db.items.Add(new ItemDefinition { name = name, icon = icon, maxStack = action == ItemAction.None ? 64 : 1, action = action });
            added++;
        }
        EditorUtility.SetDirty(db);
        return added;
    }

    /// <summary>Adds the example recipes db doesn't have a recipe for yet. Returns how many were added.</summary>
    static int AddExampleRecipes(RecipeDatabase db, bool includeSandstone = true)
    {
        int added = 0;
        void Add(string result, int count, string[] bottom, string[] top, params (char symbol, string item, int amount)[] keys)
        {
            if (db.recipes.Exists(r => string.Equals(r.result?.Trim(), result, System.StringComparison.OrdinalIgnoreCase))) return;
            var recipe = new CraftingRecipe { result = result, count = count, bottom = bottom, top = top ?? new string[0] };
            foreach (var k in keys) recipe.keys.Add(new RecipeKey { symbol = k.symbol.ToString(), item = k.item, amount = k.amount });
            db.recipes.Add(recipe);
            added++;
        }

        Add("Planks", 4, new[] { "L" }, null, ('L', "Log", 1));
        Add("Crafting Table", 1, new[] { "PP", "PP" }, null, ('P', "Planks", 1));
        // Variant: the crafting table pattern with stone laid on the top layer
        Add("Cutting Table", 1, new[] { "PP", "PP" }, new[] { "CC", "CC" }, ('P', "Planks", 1), ('C', "Cobblestone", 1));
        if (includeSandstone) Add("Sandstone", 1, new[] { "SS", "SS" }, null, ('S', "Sand", 1));
        Add("Bucket", 1, new[] { "I I", " I " }, null, ('I', "Iron", 1));
        EditorUtility.SetDirty(db);
        return added;
    }

    // ---------- Survival basics for an existing world ----------

    [MenuItem("Tools/Voxel/Add Survival Basics To My World")]
    static void AddSurvivalBasics()
    {
        var world = Object.FindAnyObjectByType<VoxelWorld>();
        if (world == null || world.config == null || world.config.blockDatabase == null)
        {
            EditorUtility.DisplayDialog("Add Survival Basics",
                "Needs a Voxel World in the open scene with a World Gen Config that has a Block Database.", "OK");
            return;
        }

        WorldGenConfig config = world.config;
        BlockDatabase db = config.blockDatabase;
        string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(config))?.Replace('\\', '/');
        if (string.IsNullOrEmpty(folder)) folder = "Assets";
        var log = new List<string>();

        // Blocks, with only the textures they need
        Undo.RecordObject(db, "Add Survival Basics");
        var missing = new List<string>();
        foreach (string name in SurvivalBlocks)
            if (!db.TryGetId(name, out _)) missing.Add(name);

        var neededTextures = new HashSet<string>();
        foreach (var spec in ExampleBlocks)
            if (missing.Contains(spec.name))
                foreach (string key in new[] { spec.side, spec.top, spec.bottom })
                    if (key != null) neededTextures.Add(key);
        if (db.waterTexture == null) neededTextures.Add("water");

        var painters = TexturePainters();
        painters.RemoveAll(p => !neededTextures.Contains(p.name));
        Dictionary<string, Texture2D> textures = painters.Count > 0 ? SaveTextures($"{folder}/Textures", painters, 1000) : new();
        Texture2D Tex(string key) => key != null && textures.TryGetValue(key, out var t) ? t : null;

        foreach (var spec in ExampleBlocks)
        {
            if (!missing.Contains(spec.name)) continue;
            var block = new BlockDefinition { name = spec.name, sideTexture = Tex(spec.side), topTexture = Tex(spec.top), bottomTexture = Tex(spec.bottom) };
            spec.setup(block);
            db.blocks.Add(block);
        }
        if (missing.Count > 0) log.Add($"Blocks added to {db.name}: {string.Join(", ", missing)}");
        if (db.waterTexture == null && Tex("water") != null)
        {
            db.waterTexture = Tex("water");
            log.Add("Water texture added");
        }
        EditorUtility.SetDirty(db);

        // Biomes: soil and stone under the surface so there is something to mine, and trees
        var biomes = new List<BiomeConfig>(config.biomes);
        if (config.forceBiome != null && !biomes.Contains(config.forceBiome)) biomes.Add(config.forceBiome);
        foreach (BiomeConfig biome in biomes)
        {
            if (biome == null) continue;
            Undo.RecordObject(biome, "Add Survival Basics");
            var changes = new List<string>();
            bool Same(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), System.StringComparison.OrdinalIgnoreCase);
            if (Same(biome.surfaceBlock, "Grass") && (Same(biome.subSurfaceBlock, "Grass") || string.IsNullOrWhiteSpace(biome.subSurfaceBlock)))
            {
                biome.subSurfaceBlock = "Dirt";
                changes.Add("Sub Surface Block = Dirt");
            }
            if (Same(biome.deepBlock, biome.surfaceBlock) || string.IsNullOrWhiteSpace(biome.deepBlock))
            {
                biome.deepBlock = "Stone";
                changes.Add("Deep Block = Stone");
            }
            if (biome.trees.Count == 0)
            {
                biome.trees.Add(Oak(0.003f));
                changes.Add("oak trees");
            }
            if (changes.Count == 0) continue;
            EditorUtility.SetDirty(biome);
            log.Add($"Biome {biome.name}: {string.Join(", ", changes)}");
        }

        // Items, recipes and managers next to the World Gen Config
        Undo.RecordObject(world, "Add Survival Basics");
        Undo.RecordObject(config, "Add Survival Basics");

        if (world.itemDatabase == null)
        {
            world.itemDatabase = CreateOrLoad<ItemDatabase>(folder, "ItemDatabase", d => { }, out bool created);
            Report(world.itemDatabase, created, log);
        }
        int itemCount = AddExampleItems(world.itemDatabase, $"{folder}/Icons");
        if (itemCount > 0) log.Add($"{itemCount} items added to {world.itemDatabase.name}");

        if (world.recipeDatabase == null)
        {
            world.recipeDatabase = CreateOrLoad<RecipeDatabase>(folder, "RecipeDatabase", d => { }, out bool created);
            Report(world.recipeDatabase, created, log);
        }
        int recipeCount = AddExampleRecipes(world.recipeDatabase, db.TryGetId("Sandstone", out _));
        if (recipeCount > 0) log.Add($"{recipeCount} recipes added to {world.recipeDatabase.name}");

        if (world.toolManager == null)
        {
            world.toolManager = CreateOrLoad<ToolDatabase>(folder, "Tool Manager", t => { }, out bool created);
            Report(world.toolManager, created, log);
        }
        if (config.oreManager == null)
        {
            config.oreManager = CreateOrLoad<OreDatabase>(folder, "Ore Manager", o => { }, out bool created);
            Report(config.oreManager, created, log);
        }

        EditorUtility.SetDirty(config);
        EditorUtility.SetDirty(world);
        AssetDatabase.SaveAssets();
        Selection.activeObject = world.gameObject;

        Debug.Log(log.Count == 0
            ? "Add Survival Basics: your world already has everything."
            : "Add Survival Basics:\n- " + string.Join("\n- ", log), world);
    }

    static void Report(Object asset, bool created, List<string> log) =>
        log.Add($"{(created ? "Created" : "Assigned")} {AssetDatabase.GetAssetPath(asset)}");

    static Material CreateWaterMaterial()
    {
        string path = $"{Folder}/Water.mat";
        var existing = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (existing != null) return existing;
        Material mat = VoxelWorld.CreateWaterMaterial();
        mat.name = "Water";
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }

    // ---------- Procedural 16x16 example textures ----------

    static List<(string name, System.Func<System.Random, Color[]> paint)> TexturePainters()
    {
        Color grass = C(95, 159, 53), dirt = C(121, 85, 58);
        Color redDirtTop = C(156, 74, 42), redDirt = C(140, 62, 36);
        Color stone = C(125, 125, 125), plank = C(170, 130, 80);

        Color[] Stone(System.Random r) => Speckle(r, Noise(r, stone, 0.08f), C(98, 98, 98), 0.2f);

        var painters = new List<(string name, System.Func<System.Random, Color[]> paint)>
        {
            ("grass_top", r => Noise(r, grass, 0.12f)),
            ("grass_side", r => Overhang(r, Noise(r, dirt, 0.12f), grass)),
            ("dirt", r => Speckle(r, Noise(r, dirt, 0.12f), C(92, 64, 44), 0.12f)),
            ("stone", Stone),
            ("sand", r => Noise(r, C(219, 207, 142), 0.05f)),
            ("sandstone", r => Bands(r, C(196, 170, 110), 0.05f)),
            ("snow", r => Noise(r, C(240, 245, 250), 0.03f)),
            ("red_dirt_top", r => Noise(r, redDirtTop, 0.1f)),
            ("red_dirt_side", r => Overhang(r, Noise(r, redDirt, 0.1f), redDirtTop)),
            ("red_dirt_bottom", r => Noise(r, redDirt, 0.1f)),
            ("red_rock", r => Speckle(r, Noise(r, C(178, 70, 38), 0.08f), C(150, 58, 32), 0.15f)),
            ("orange_rock", r => Bands(r, C(205, 110, 55), 0.06f)),
            ("dark_red_rock", r => Speckle(r, Noise(r, C(120, 45, 30), 0.08f), C(95, 35, 24), 0.15f)),
            ("cream_sandstone", r => Bands(r, C(226, 200, 160), 0.04f)),
            ("cobblestone", r => Cells(r, C(130, 130, 130), C(80, 80, 80), 7)),
            ("log_side", r => VerticalStripes(r, C(105, 78, 46))),
            ("log_top", r => Rings(r, C(176, 140, 88), C(140, 105, 60), C(105, 78, 46))),
            ("leaves", r => Speckle(r, Noise(r, C(62, 128, 42), 0.15f), C(38, 84, 26), 0.3f)),
            ("planks", r => Boards(r, plank)),
            ("crafting_table_top", r => Grid(Boards(r, plank), C(95, 70, 40))),
            ("crafting_table_side", r => Frame(Boards(r, plank), C(95, 70, 40))),
            ("cutting_table_top", r => SawBlade(Boards(r, plank))),
            ("cutting_table_side", r => StoneTop(r, Boards(r, plank), stone)),
            ("ice", r => Streaks(r, Noise(r, C(160, 200, 250), 0.04f), C(215, 235, 255))),
            ("magma", r => Cells(r, C(95, 30, 15), C(255, 125, 25), 6)),
            ("water", r => Alpha(Noise(r, C(45, 90, 205), 0.08f), 0.72f)),
        };
        return painters;
    }

    static Dictionary<string, Texture2D> SaveTextures(string folder, List<(string name, System.Func<System.Random, Color[]> paint)> painters, int seed)
    {
        string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));

        var result = new Dictionary<string, Texture2D>();
        for (int i = 0; i < painters.Count; i++)
        {
            var (name, paint) = painters[i];
            string path = $"{folder}/{name}.png";
            if (!File.Exists(path))
            {
                var tex = new Texture2D(TexSize, TexSize, TextureFormat.RGBA32, false);
                tex.SetPixels(paint(new System.Random(seed + StableHash(name))));
                File.WriteAllBytes(path, tex.EncodeToPNG());
                Object.DestroyImmediate(tex);

                AssetDatabase.ImportAsset(path);
                var importer = (TextureImporter)AssetImporter.GetAtPath(path);
                importer.textureType = TextureImporterType.Default;
                importer.alphaIsTransparency = true;
                importer.filterMode = FilterMode.Point;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.SaveAndReimport();
            }
            result[name] = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }
        return result;
    }

    static Color[] Noise(System.Random r, Color baseColor, float variation)
    {
        var px = new Color[TexSize * TexSize];
        for (int i = 0; i < px.Length; i++) px[i] = Shade(baseColor, 1f + Signed(r) * variation);
        return px;
    }

    static Color[] Speckle(System.Random r, Color[] px, Color speck, float chance)
    {
        for (int i = 0; i < px.Length; i++)
            if (r.NextDouble() < chance) px[i] = Shade(speck, 1f + Signed(r) * 0.05f);
        return px;
    }

    // Paints a jagged strip of the top color along the upper edge, like a grass block side
    static Color[] Overhang(System.Random r, Color[] px, Color top)
    {
        for (int x = 0; x < TexSize; x++)
        {
            int depth = 2 + r.Next(0, 3);
            for (int y = TexSize - depth; y < TexSize; y++)
                px[x + y * TexSize] = Shade(top, 1f + Signed(r) * 0.12f);
        }
        return px;
    }

    // Horizontal layers of slightly different brightness, for sedimentary rock
    static Color[] Bands(System.Random r, Color baseColor, float variation)
    {
        const int bandHeight = 4;
        var bandShade = new float[TexSize / bandHeight];
        for (int b = 0; b < bandShade.Length; b++) bandShade[b] = 1f + Signed(r) * 0.1f;

        var px = new Color[TexSize * TexSize];
        for (int y = 0; y < TexSize; y++)
        for (int x = 0; x < TexSize; x++)
            px[x + y * TexSize] = Shade(baseColor, bandShade[y / bandHeight] + Signed(r) * variation);
        return px;
    }

    // Wooden boards with dark seams
    static Color[] Boards(System.Random r, Color wood)
    {
        Color[] px = Bands(r, wood, 0.05f);
        for (int y = 0; y < TexSize; y += 4)
        for (int x = 0; x < TexSize; x++)
            px[x + y * TexSize] = Shade(wood, 0.65f);
        // Staggered board ends
        for (int b = 0; b < TexSize / 4; b++)
        {
            int x = (b % 2 == 0 ? 4 : 11);
            for (int y = b * 4 + 1; y < b * 4 + 4; y++) px[x + y * TexSize] = Shade(wood, 0.7f);
        }
        return px;
    }

    // Irregular stones (cells) separated by mortar lines
    static Color[] Cells(System.Random r, Color fill, Color edge, int count)
    {
        var points = new Vector2[count * 9];
        var shades = new float[count];
        for (int i = 0; i < count; i++)
        {
            var p = new Vector2((float)r.NextDouble() * TexSize, (float)r.NextDouble() * TexSize);
            shades[i] = 1f + Signed(r) * 0.12f;
            // Copies in the neighboring tiles so the texture tiles seamlessly
            for (int t = 0; t < 9; t++) points[i * 9 + t] = p + new Vector2((t % 3 - 1) * TexSize, (t / 3 - 1) * TexSize);
        }

        var px = new Color[TexSize * TexSize];
        for (int y = 0; y < TexSize; y++)
        for (int x = 0; x < TexSize; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f);
            float best = float.MaxValue, second = float.MaxValue;
            int bestIndex = 0;
            for (int i = 0; i < points.Length; i++)
            {
                float d = (points[i] - p).sqrMagnitude;
                if (d < best) { second = best; best = d; bestIndex = i / 9; }
                else if (d < second) second = d;
            }
            bool border = Mathf.Sqrt(second) - Mathf.Sqrt(best) < 1.1f;
            px[x + y * TexSize] = border ? Shade(edge, 1f + Signed(r) * 0.08f) : Shade(fill, shades[bestIndex] + Signed(r) * 0.05f);
        }
        return px;
    }

    static Color[] VerticalStripes(System.Random r, Color bark)
    {
        var column = new float[TexSize];
        for (int x = 0; x < TexSize; x++) column[x] = 1f + Signed(r) * 0.18f;
        var px = new Color[TexSize * TexSize];
        for (int y = 0; y < TexSize; y++)
        for (int x = 0; x < TexSize; x++)
            px[x + y * TexSize] = Shade(bark, column[x] + Signed(r) * 0.05f);
        return px;
    }

    static Color[] Rings(System.Random r, Color light, Color dark, Color bark)
    {
        var px = new Color[TexSize * TexSize];
        for (int y = 0; y < TexSize; y++)
        for (int x = 0; x < TexSize; x++)
        {
            float d = Mathf.Max(Mathf.Abs(x - 7.5f), Mathf.Abs(y - 7.5f));
            Color c = d > 6.5f ? bark : Mathf.FloorToInt(d) % 2 == 0 ? light : dark;
            px[x + y * TexSize] = Shade(c, 1f + Signed(r) * 0.05f);
        }
        return px;
    }

    static Color[] Grid(Color[] px, Color line)
    {
        for (int i = 0; i < TexSize; i++)
        foreach (int g in new[] { 0, 5, 10, 15 })
        {
            px[i + g * TexSize] = line;
            px[g + i * TexSize] = line;
        }
        return px;
    }

    static Color[] Frame(Color[] px, Color line)
    {
        for (int i = 0; i < TexSize; i++)
        {
            px[i + (TexSize - 1) * TexSize] = line;
            px[i + (TexSize - 2) * TexSize] = line;
            px[i] = line;
        }
        // A little hammer and saw hanging on the side
        for (int y = 4; y < 11; y++) px[4 + y * TexSize] = C(110, 80, 50);
        for (int x = 3; x < 6; x++) px[x + 11 * TexSize] = C(150, 150, 150);
        for (int y = 5; y < 11; y++) for (int x = 9; x < 12; x++) px[x + y * TexSize] = C(190, 190, 195);
        return px;
    }

    static Color[] SawBlade(Color[] px)
    {
        for (int y = 0; y < TexSize; y++)
        for (int x = 0; x < TexSize; x++)
        {
            float dx = x - 7.5f, dy = y - 7.5f;
            float d = Mathf.Sqrt(dx * dx + dy * dy);
            float angle = Mathf.Atan2(dy, dx);
            // Teeth stick out a bit every 45 degrees
            float radius = 5.2f + (Mathf.Repeat(angle / (Mathf.PI / 4f), 1f) < 0.4f ? 1.3f : 0f);
            if (d < 1.2f) px[x + y * TexSize] = C(80, 80, 85);
            else if (d < radius) px[x + y * TexSize] = Shade(C(200, 200, 205), 1f - d * 0.02f);
        }
        return px;
    }

    static Color[] StoneTop(System.Random r, Color[] px, Color stone)
    {
        for (int y = TexSize - 5; y < TexSize; y++)
        for (int x = 0; x < TexSize; x++)
            px[x + y * TexSize] = Shade(stone, 1f + Signed(r) * 0.08f);
        return px;
    }

    static Color[] Streaks(System.Random r, Color[] px, Color streak)
    {
        for (int s = 0; s < 3; s++)
        {
            int start = r.Next(0, TexSize);
            for (int i = 0; i < 6; i++)
            {
                int x = (start + i) % TexSize, y = (r.Next(0, 2) + i * 2 + s * 5) % TexSize;
                px[x + y * TexSize] = streak;
            }
        }
        return px;
    }

    static Color[] Alpha(Color[] px, float alpha)
    {
        for (int i = 0; i < px.Length; i++) px[i].a = alpha;
        return px;
    }

    static int StableHash(string s)
    {
        int h = 17;
        foreach (char c in s) h = h * 31 + c;
        return h & 0xFFFF;
    }

    static float Signed(System.Random r) => (float)r.NextDouble() * 2f - 1f;

    static Color Shade(Color c, float k) => new(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), c.a);

    static Color C(int r, int g, int b) => new Color32((byte)r, (byte)g, (byte)b, 255);
}
