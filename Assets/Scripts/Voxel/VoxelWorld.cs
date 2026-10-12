using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Everything stored in one voxel: block, its shape (see VoxelShapes) and water (see Fluid).</summary>
public struct VoxelEdit
{
    public byte id;
    public byte shape;
    public byte fluid;
}

/// <summary>Streams voxel chunks around a viewer. Keep this object at the world origin.</summary>
public class VoxelWorld : MonoBehaviour
{
    public WorldGenConfig config;
    [Tooltip("Optional. Its main texture is replaced by the packed block texture atlas. Leave empty to auto-create a Lit material.")]
    public Material material;
    [Tooltip("Optional transparent material for water. Its main texture is replaced by the atlas. Leave empty to auto-create one.")]
    public Material waterMaterial;
    public int seed = 1337;

    [Header("Items")]
    public ItemDatabase itemDatabase;
    [Tooltip("Empty = Assets/Resources/Recipe Database.")]
    public RecipeDatabase recipeDatabase;
    [Tooltip("Makes the tools (Stone Pickaxe, Iron Axe...) with their icons and recipes. Ores come from the World " +
             "Gen Config's Ore Manager. Empty = Assets/Resources/Tool Manager.")]
    public ToolDatabase toolManager;

    public const string DefaultRecipes = "Recipe Database", DefaultTools = "Tool Manager";

    [Header("Streaming")]
    [Tooltip("Chunks load around this. Falls back to the main camera.")]
    public Transform viewer;
    [Tooltip("Radius in chunks. Chunks are generated on background threads, so higher mostly costs memory and draw calls.")]
    [Range(3, 32)] public int viewDistance = 12;
    [Tooltip("Background threads generating chunks. 0 = one per CPU core, minus one for the game.")]
    [Range(0, 16)] public int generatorThreads = 0;
    [Tooltip("Milliseconds per frame spent putting finished chunks into the scene. Lower = smoother, higher = faster loading.")]
    [Range(0.5f, 16f)] public float uploadBudgetMs = 3f;
    public bool generateColliders = true;

    [Header("Distance Fog")]
    [Tooltip("Fades the terrain out before the edge of the loaded area, so chunks never appear where you can see them. " +
             "The fog distance follows View Distance and the camera's field of view.")]
    public bool distanceFog = true;
    public Color fogColor = new Color32(160, 200, 245, 255);
    [Tooltip("Where the fog starts, as a share of the fog distance.")]
    [Range(0f, 0.95f)] public float fogStart = 0.6f;
    [Tooltip("Paints the sky the fog color, so far terrain blends into it instead of leaving a silhouette.")]
    public bool skyMatchesFog = true;

    [Header("Lighting")]
    [Tooltip("Minecraft Alpha style: no shadows or smooth shading. Each face direction gets a fixed shade (tops " +
             "brightest, bottoms darkest) and sunlight fades block by block into caves and under trees. Uses an unlit " +
             "shader instead of Material / Water Material. Off = those materials' own lighting.")]
    public bool classicLighting = true;
    [Tooltip("Brightness where sunlight never reaches, like deep caves. Minecraft Alpha was nearly black (0.05).")]
    [Range(0f, 1f)] public float caveBrightness = 0.12f;
    [Tooltip("Tint of blocks in full sunlight.")]
    public Color sunlightTint = new Color(1f, 0.97f, 0.9f);
    [Tooltip("Tint in the shade, under trees and in caves, fading into Sunlight Tint as the light gets brighter.")]
    public Color shadeTint = new Color(0.72f, 0.82f, 1f);

    [Header("Loading Screen")]
    [Tooltip("Covers the screen while the area around the player generates at the start, after regenerating and after " +
             "teleporting somewhere unloaded.")]
    public bool loadingScreen = true;

    [Header("Water")]
    [Tooltip("Spray from waves crashing into sea cliffs, in biomes with Wave Spray.")]
    public bool waveSpray = true;
    [Tooltip("Seconds between water flow steps.")]
    [Min(0.02f)] public float fluidTickInterval = 0.25f;
    [Tooltip("Most water cells updated per step, so big floods spread over several frames.")]
    [Min(64)] public int maxFluidUpdatesPerTick = 4096;

    public TerrainGenerator Generator { get; private set; }
    /// <summary>The Block Database plus the Ore Manager's blocks. A runtime copy, the asset isn't changed.</summary>
    public BlockDatabase Blocks { get; private set; }
    public ItemRegistry Items { get; private set; }
    /// <summary>The Recipe Database plus generated recipes (tools and their handles).</summary>
    public RecipeDatabase Recipes { get; private set; }
    public Material ChunkMaterial { get; private set; }
    public Material WaterMaterial { get; private set; }
    /// <summary>The chunk material with see-through pixels cut out and both sides drawn, for plants.</summary>
    public Material PlantMaterial { get; private set; }
    /// <summary>Classic lighting's brightness levels, or null when Classic Lighting is off.</summary>
    public LightTable Lighting { get; private set; }

    /// <summary>True while the area around the viewer is still generating (the loading screen is up).</summary>
    public bool IsLoading { get; private set; }

    readonly Dictionary<Vector2Int, Chunk> chunks = new();
    readonly Dictionary<Vector2Int, Dictionary<Vector3Int, VoxelEdit>> edits = new();
    // Bumped on every edit, so chunks generated from an older snapshot of the edits know to catch up
    readonly Dictionary<Vector2Int, int> editVersions = new();
    readonly List<Vector2Int> buildQueue = new();
    readonly HashSet<Chunk> dirtyChunks = new();
    Vector2Int lastCenter;
    bool queueValid;
    Texture2D atlas;
    FluidSimulator fluids;
    float fluidTimer;
    ItemDatabase runtimeItems;
    readonly List<Texture2D> generatedTextures = new();
    WaveSpray spray;
    // The biomes around the viewer's fog, eased toward so walking into a foggy biome rolls the fog in
    float biomeFogDistance = 1f, targetFogDistance = 1f, fogSampleTimer;
    Color biomeFogColor = Color.clear, targetFogColor = Color.clear;

    // ---------- Background generation ----------
    // A chunk goes: queued -> generating (worker thread) -> uploaded, collider baking (worker thread) -> loaded.
    // Only loaded chunks are in `chunks`, so gameplay never touches a chunk without its collider.

    class ChunkJob
    {
        public Vector2Int coord;
        public int contentVersion, editVersion;
        public List<KeyValuePair<Vector3Int, VoxelEdit>> edits;
        public ChunkData data;
        public ChunkMesh mesh;
        public System.Exception error;
    }

    class BakeJob
    {
        public Chunk chunk;
        public int contentVersion, editVersion;
        public System.Exception error;
    }

    readonly Dictionary<Vector2Int, ChunkJob> generating = new();
    readonly Dictionary<Vector2Int, Chunk> baking = new();
    readonly ConcurrentQueue<ChunkJob> generated = new();
    readonly ConcurrentQueue<BakeJob> baked = new();
    readonly ConcurrentBag<ChunkMesh> meshPool = new();
    readonly List<Task> bakeTasks = new();
    readonly System.Diagnostics.Stopwatch uploadTimer = new();
    // Bumped on Regenerate so results from the old world are thrown away
    int contentVersion;

    const float LoadingFadeSeconds = 0.6f;
    const float LoadingTimeout = 30f;
    const float LoadingUploadBudgetMs = 25f;
    const int LoadingSettleFrames = 10;
    float loadingAlpha, loadingStarted;
    int loadingTotal, settledFrames;
    GUIStyle loadingStyle;

    void Awake() => Initialize();

    void Initialize()
    {
        contentVersion++;
        StartLoading();
        BuildContent();
        Generator = new TerrainGenerator(config, Blocks, seed);
        fluids = new FluidSimulator(this);
        if (waveSpray && spray == null)
        {
            var go = new GameObject("Wave Spray") { layer = gameObject.layer };
            go.transform.SetParent(transform, false);
            spray = go.AddComponent<WaveSpray>();
            spray.world = this;
        }
        if (atlas != null) Destroy(atlas);
        Blocks.DefaultTint = Generator.Tints.Count > 0 ? 1 : 0;
        atlas = Blocks.BuildAtlas(Generator.Tints);
        BlockShapeMesh.ClearCache();
        Items?.Dispose();
        Items = new ItemRegistry(Blocks, runtimeItems, atlas);
        Lighting = classicLighting
            ? new LightTable(caveBrightness, QualitySettings.activeColorSpace == ColorSpace.Linear, sunlightTint, shadeTint)
            : null;
        if (ChunkMaterial == null && classicLighting) ChunkMaterial = CreateClassicMaterial("Voxel Chunks (Classic)");
        if (ChunkMaterial == null)
            ChunkMaterial = material != null ? new Material(material) : CreateDefaultMaterial();
        if (WaterMaterial == null && classicLighting)
        {
            WaterMaterial = CreateClassicMaterial("Voxel Water (Classic)");
            if (WaterMaterial != null) MakeTransparent(WaterMaterial);
        }
        if (WaterMaterial == null)
            WaterMaterial = waterMaterial != null ? new Material(waterMaterial) : CreateWaterMaterial();
        if (PlantMaterial == null) PlantMaterial = CreatePlantMaterial(ChunkMaterial);
        // Without it see-through pixels (leaves' holes) draw as whatever color they hide, usually black
        SetAlphaClip(ChunkMaterial, Blocks.HasCutouts);
        foreach (Material m in new[] { ChunkMaterial, WaterMaterial, PlantMaterial })
        {
            m.mainTexture = atlas;
            if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", atlas);
        }
    }

    // Unlit and tinted by vertex color, which is where classic lighting puts its shading. In builds the shader must be
    // included (e.g. by a saved material using it), or this falls back to the lit materials.
    static Material CreateClassicMaterial(string name)
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null) return null;
        var mat = new Material(shader) { name = name };
        mat.SetFloat("_Cull", (float)CullMode.Back);
        return mat;
    }

    void OnDestroy()
    {
        WaitForBakes();
        if (atlas != null) Destroy(atlas);
        if (ChunkMaterial != null) Destroy(ChunkMaterial);
        if (WaterMaterial != null) Destroy(WaterMaterial);
        if (PlantMaterial != null) Destroy(PlantMaterial);
        Items?.Dispose();
        DestroyContent();
    }

    /// <summary>Combines the databases with what the Ore and Tool Managers generate into runtime copies.</summary>
    void BuildContent()
    {
        DestroyContent();

        Blocks = Instantiate(config.blockDatabase);
        Blocks.name = config.blockDatabase.name;
        if (config.oreManager != null) config.oreManager.AddBlocks(Blocks, generatedTextures);

        runtimeItems = ScriptableObject.CreateInstance<ItemDatabase>();
        if (itemDatabase != null) runtimeItems.items.AddRange(itemDatabase.items);
        Recipes = ScriptableObject.CreateInstance<RecipeDatabase>();
        RecipeDatabase recipes = recipeDatabase != null ? recipeDatabase : Resources.Load<RecipeDatabase>(DefaultRecipes);
        if (recipes != null) Recipes.recipes.AddRange(recipes.recipes);
        ToolDatabase tools = toolManager != null ? toolManager : Resources.Load<ToolDatabase>(DefaultTools);

        bool Exists(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            string trimmed = name.Trim();
            return Blocks.TryGetId(trimmed, out _) || runtimeItems.items.Exists(i =>
                i != null && string.Equals(i.name?.Trim(), trimmed, System.StringComparison.OrdinalIgnoreCase));
        }

        // Ore drops first, tools are made from them
        if (config.oreManager != null) config.oreManager.AddItems(runtimeItems.items, Exists, generatedTextures);
        if (config.oreManager != null) config.oreManager.AddAlloys(runtimeItems.items, Recipes.recipes, Exists, generatedTextures);
        if (tools != null) tools.Generate(runtimeItems.items, Recipes.recipes, Exists, generatedTextures);
    }

    void DestroyContent()
    {
        foreach (Texture2D t in generatedTextures) if (t != null) Destroy(t);
        generatedTextures.Clear();
        if (Blocks != null) Destroy(Blocks);
        if (runtimeItems != null) Destroy(runtimeItems);
        if (Recipes != null) Destroy(Recipes);
    }

    static Material CreateDefaultMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var mat = new Material(shader) { name = "Voxel Chunks (Runtime)" };
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0f);
        if (mat.HasProperty("_Glossiness")) mat.SetFloat("_Glossiness", 0f);
        return mat;
    }

    /// <summary>
    /// A transparent, double-sided URP Lit material. In builds, assign a saved transparent material instead so the
    /// shader variant isn't stripped.
    /// </summary>
    public static Material CreateWaterMaterial()
    {
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        var mat = new Material(shader) { name = "Voxel Water (Runtime)" };
        MakeTransparent(mat);
        return mat;
    }

    // In builds the alpha clip shader variant must not be stripped; a saved material using it keeps it in
    static Material CreatePlantMaterial(Material chunkMaterial)
    {
        var mat = new Material(chunkMaterial) { name = "Voxel Plants (Runtime)" };
        SetAlphaClip(mat, true);
        mat.SetFloat("_Cull", (float)CullMode.Off);
        return mat;
    }

    static void SetAlphaClip(Material mat, bool on)
    {
        mat.SetFloat("_AlphaClip", on ? 1f : 0f);
        mat.SetFloat("_Cutoff", 0.5f);
        if (on) mat.EnableKeyword("_ALPHATEST_ON");
        else mat.DisableKeyword("_ALPHATEST_ON");
        mat.SetOverrideTag("RenderType", on ? "TransparentCutout" : "");
        mat.renderQueue = on ? (int)RenderQueue.AlphaTest : -1;
    }

    public static void MakeTransparent(Material mat)
    {
        mat.SetFloat("_Surface", 1f);
        mat.SetFloat("_Blend", 0f);
        mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite", 0f);
        mat.SetFloat("_Cull", (float)CullMode.Off);
        mat.SetFloat("_Smoothness", 0.6f);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.renderQueue = (int)RenderQueue.Transparent;
    }

    /// <summary>Rebuilds the whole world. Use after tweaking biome assets or the seed in play mode.</summary>
    [ContextMenu("Regenerate")]
    public void Regenerate()
    {
        WaitForBakes();
        foreach (var chunk in chunks.Values)
            if (chunk != null) Destroy(chunk.gameObject);
        foreach (var chunk in baking.Values)
            if (chunk != null) Destroy(chunk.gameObject);
        chunks.Clear();
        baking.Clear();
        generating.Clear();
        dirtyChunks.Clear();
        Initialize();
        queueValid = false;
    }

    void Update()
    {
        Vector2Int center = WorldToChunk(ViewerPosition());
        if (!queueValid || center != lastCenter)
        {
            lastCenter = center;
            RefreshQueue(center);
            queueValid = true;
        }

        ScheduleChunks();
        UploadChunks(IsLoading ? LoadingUploadBudgetMs : uploadBudgetMs);
        UpdateLoading(center);

        fluidTimer += Time.deltaTime;
        if (fluidTimer >= fluidTickInterval)
        {
            fluidTimer = 0f;
            fluids.Tick(maxFluidUpdatesPerTick);
        }
    }

    void LateUpdate()
    {
        foreach (var chunk in dirtyChunks)
            if (chunk != null) chunk.BuildMesh();
        dirtyChunks.Clear();
        UpdateFog();
    }

    Vector3 ViewerPosition()
    {
        if (viewer != null) return viewer.position;
        Camera cam = Camera.main;
        return cam != null ? cam.transform.position : transform.position;
    }

    void RefreshQueue(Vector2Int center)
    {
        int r = viewDistance;

        var toRemove = new List<Vector2Int>();
        foreach (var kv in chunks)
            if (!InRange(kv.Key, center)) toRemove.Add(kv.Key);
        foreach (var c in toRemove)
        {
            dirtyChunks.Remove(chunks[c]);
            Destroy(chunks[c].gameObject);
            chunks.Remove(c);
        }

        buildQueue.Clear();
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (dx * dx + dz * dz > r * r) continue;
            var c = new Vector2Int(center.x + dx, center.y + dz);
            if (!IsKnown(c)) buildQueue.Add(c);
        }
        // Farthest first, so popping from the end builds nearest chunks first
        buildQueue.Sort((a, b) => (b - center).sqrMagnitude.CompareTo((a - center).sqrMagnitude));
    }

    // Chunks are kept one ring past the view distance so walking back and forth over a border doesn't reload them
    bool InRange(Vector2Int c, Vector2Int center) => (c - center).sqrMagnitude <= (viewDistance + 1) * (viewDistance + 1);

    bool IsKnown(Vector2Int c) => chunks.ContainsKey(c) || generating.ContainsKey(c) || baking.ContainsKey(c);

    int MaxJobs => generatorThreads > 0 ? generatorThreads : Mathf.Max(1, System.Environment.ProcessorCount - 1);

    void ScheduleChunks()
    {
        TerrainGenerator gen = Generator;
        BlockDatabase blocks = Blocks;
        LightTable lighting = Lighting;
        while (generating.Count < MaxJobs && buildQueue.Count > 0)
        {
            Vector2Int c = buildQueue[^1];
            buildQueue.RemoveAt(buildQueue.Count - 1);
            if (IsKnown(c)) continue;

            var job = new ChunkJob
            {
                coord = c, contentVersion = contentVersion, editVersion = EditVersion(c),
                edits = edits.TryGetValue(c, out var e) ? new List<KeyValuePair<Vector3Int, VoxelEdit>>(e) : null,
                mesh = meshPool.TryTake(out ChunkMesh m) ? m : new ChunkMesh()
            };
            generating[c] = job;
            Task.Run(() =>
            {
                try
                {
                    job.data = new ChunkData(job.coord);
                    job.data.Generate(gen, blocks, job.edits);
                    job.mesh.Build(job.data, blocks, lighting);
                }
                catch (System.Exception ex) { job.error = ex; }
                generated.Enqueue(job);
            });
        }
    }

    /// <summary>Puts finished chunks into the scene, within a time budget so frames stay smooth.</summary>
    void UploadChunks(float budgetMs)
    {
        uploadTimer.Restart();

        while (baked.TryDequeue(out BakeJob bake))
        {
            if (bake.contentVersion != contentVersion || bake.chunk == null) continue;
            baking.Remove(bake.chunk.Coord);
            if (bake.error != null) Debug.LogException(bake.error);
            if (!InRange(bake.chunk.Coord, lastCenter))
            {
                Destroy(bake.chunk.gameObject);
                continue;
            }
            bake.chunk.AssignCollider();
            AddLoadedChunk(bake.chunk, bake.editVersion);
        }
        bakeTasks.RemoveAll(t => t.IsCompleted);

        while (uploadTimer.Elapsed.TotalMilliseconds < budgetMs && generated.TryDequeue(out ChunkJob job))
        {
            if (job.contentVersion != contentVersion) continue;
            generating.Remove(job.coord);
            if (job.error != null || !InRange(job.coord, lastCenter))
            {
                if (job.error != null) Debug.LogException(job.error);
                meshPool.Add(job.mesh);
                continue;
            }

            Chunk chunk = CreateChunkObject(job.coord);
            chunk.Initialize(this, job.data, job.mesh);
            meshPool.Add(job.mesh);

            if (generateColliders && chunk.HasSolidMesh) BakeCollider(chunk, job.editVersion);
            else AddLoadedChunk(chunk, job.editVersion);
        }
    }

    Chunk CreateChunkObject(Vector2Int c)
    {
        var go = new GameObject($"Chunk {c.x},{c.y}") { layer = gameObject.layer };
        go.transform.SetParent(transform, false);
        go.transform.position = new Vector3(c.x * Chunk.Size, 0f, c.y * Chunk.Size);
        return go.AddComponent<Chunk>();
    }

    // Cooking a collider is the slowest part of showing a chunk, so it happens on a worker thread too
    void BakeCollider(Chunk chunk, int editVersion)
    {
        baking[chunk.Coord] = chunk;
        var bake = new BakeJob { chunk = chunk, contentVersion = contentVersion, editVersion = editVersion };
        EntityId meshId = chunk.MeshId;
        MeshColliderCookingOptions options = chunk.CookingOptions;
        bakeTasks.Add(Task.Run(() =>
        {
            try { Physics.BakeMesh(meshId, false, options); }
            catch (System.Exception ex) { bake.error = ex; }
            baked.Enqueue(bake);
        }));
    }

    // Meshes can't be destroyed while a worker is baking them
    void WaitForBakes()
    {
        if (bakeTasks.Count > 0) Task.WaitAll(bakeTasks.ToArray(), 5000);
        bakeTasks.Clear();
    }

    void AddLoadedChunk(Chunk chunk, int editVersion)
    {
        Vector2Int c = chunk.Coord;
        chunks[c] = chunk;
        // Edits made while it was generating
        if (EditVersion(c) != editVersion && edits.TryGetValue(c, out var e))
        {
            foreach (var kv in e) chunk.SetVoxelWorld(kv.Key, kv.Value);
            chunk.BuildMesh();
        }
    }

    int EditVersion(Vector2Int c) => editVersions.TryGetValue(c, out int v) ? v : 0;

    // ---------- Fog and loading screen ----------

    void UpdateFog()
    {
        if (!distanceFog) return;
        UpdateBiomeFog();
        Color color = Color.Lerp(fogColor, new Color(biomeFogColor.r, biomeFogColor.g, biomeFogColor.b), biomeFogColor.a);

        // Distance from the viewer that is always loaded: the view radius minus the viewer's spot in its chunk
        float loaded = (viewDistance - 1.5f) * Chunk.Size;
        float end = loaded;
        Camera cam = Camera.main;
        if (cam != null)
        {
            // URP fog goes by depth along the view direction, so things near the screen corners are less fogged than
            // things straight ahead at the same distance. Pull the fog in so even the corners are covered in time.
            float tanV = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad), tanH = tanV * cam.aspect;
            end = loaded / Mathf.Sqrt(1f + tanV * tanV + tanH * tanH);
            if (cam.farClipPlane < loaded + Chunk.Size) cam.farClipPlane = loaded + Chunk.Size;
            if (skyMatchesFog)
            {
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = color;
            }
        }
        end *= biomeFogDistance;

        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = color;
        // Thick fog starts close by instead of at the usual share of the distance
        RenderSettings.fogStartDistance = end * fogStart * Mathf.Lerp(0.15f, 1f, biomeFogDistance);
        RenderSettings.fogEndDistance = end;
    }

    /// <summary>Averages the Fog settings of the biomes at and around the viewer, then eases toward them.</summary>
    void UpdateBiomeFog()
    {
        if (Generator == null) return;
        fogSampleTimer -= Time.deltaTime;
        if (fogSampleTimer <= 0f)
        {
            fogSampleTimer = 0.25f;
            Vector3 p = ViewerPosition();
            float distance = 0f;
            Color tint = Color.clear;
            const int samples = 5;
            for (int i = 0; i < samples; i++)
            {
                Vector2 o = i == 0 ? Vector2.zero : new Vector2(i == 1 ? 1 : i == 2 ? -1 : 0, i == 3 ? 1 : i == 4 ? -1 : 0) * 24f;
                BiomeConfig b = Generator.GetBiome(Mathf.FloorToInt(p.x + o.x), Mathf.FloorToInt(p.z + o.y));
                if (b == null) continue;
                distance += b.fogDistance;
                // Premultiplied, so biomes without a fog color only thin out the others' color
                tint += new Color(b.fogColor.r * b.fogColor.a, b.fogColor.g * b.fogColor.a, b.fogColor.b * b.fogColor.a, b.fogColor.a);
            }
            targetFogDistance = distance / samples;
            // Fading out keeps the color, so it doesn't darken on the way
            targetFogColor = tint.a > 0f
                ? new Color(tint.r / tint.a, tint.g / tint.a, tint.b / tint.a, tint.a / samples)
                : new Color(biomeFogColor.r, biomeFogColor.g, biomeFogColor.b, 0f);
        }
        float k = IsLoading ? 1f : 1f - Mathf.Exp(-Time.deltaTime * 1.5f);
        biomeFogDistance = Mathf.Lerp(biomeFogDistance, targetFogDistance, k);
        if (biomeFogColor.a < 0.01f) biomeFogColor = new Color(targetFogColor.r, targetFogColor.g, targetFogColor.b, biomeFogColor.a);
        biomeFogColor = Color.Lerp(biomeFogColor, targetFogColor, k);
    }

    void StartLoading()
    {
        if (!loadingScreen) return;
        IsLoading = true;
        loadingAlpha = 1f;
        loadingStarted = Time.unscaledTime;
        loadingTotal = 0;
        settledFrames = 0;
    }

    void UpdateLoading(Vector2Int center)
    {
        int remaining = buildQueue.Count + generating.Count + baking.Count;
        if (IsLoading)
        {
            loadingTotal = Mathf.Max(loadingTotal, remaining);
            // The world keeps rendering behind the overlay, so once every chunk is in, wait for the shaders and
            // textures that first render needed to finish compiling and uploading before showing anything
            bool settling = remaining == 0 && !MaterialsCompiling();
            settledFrames = settling ? settledFrames + 1 : 0;
            if (settledFrames >= LoadingSettleFrames || Time.unscaledTime - loadingStarted > LoadingTimeout) IsLoading = false;
        }
        // Teleported somewhere that isn't generated yet
        else if (loadingScreen && (generating.ContainsKey(center) || buildQueue.Contains(center)))
            StartLoading();

        loadingAlpha = Mathf.MoveTowards(loadingAlpha, IsLoading ? 1f : 0f, Time.unscaledDeltaTime / LoadingFadeSeconds);
    }

    // In the editor shaders compile in the background and draw as cyan placeholders until they're done
    static bool MaterialsCompiling()
    {
#if UNITY_EDITOR
        return UnityEditor.ShaderUtil.anythingCompiling;
#else
        return false;
#endif
    }

    void OnGUI()
    {
        if (loadingAlpha <= 0f) return;
        GUI.depth = -1000;

        Color previous = GUI.color;
        GUI.color = new Color(fogColor.r, fogColor.g, fogColor.b, loadingAlpha);
        GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);

        float scale = Mathf.Max(1f, Screen.height / 720f);
        loadingStyle ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        loadingStyle.fontSize = Mathf.RoundToInt(22 * scale);
        loadingStyle.normal.textColor = Color.white;

        int remaining = buildQueue.Count + generating.Count + baking.Count;
        string title = remaining > 0 || !IsLoading ? "Generating world" : "Preparing materials";
        var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        GUI.color = new Color(0f, 0f, 0f, 0.35f * loadingAlpha);
        GUI.Label(new Rect(center.x - 300f * scale + 2f, center.y - 40f * scale + 2f, 600f * scale, 40f * scale), title, loadingStyle);
        GUI.color = new Color(1f, 1f, 1f, loadingAlpha);
        GUI.Label(new Rect(center.x - 300f * scale, center.y - 40f * scale, 600f * scale, 40f * scale), title, loadingStyle);

        float progress = loadingTotal > 0 ? 1f - remaining / (float)loadingTotal : 0f;
        if (!IsLoading) progress = 1f;
        var bar = new Rect(center.x - 160f * scale, center.y + 10f * scale, 320f * scale, 8f * scale);
        GUI.color = new Color(0f, 0f, 0f, 0.3f * loadingAlpha);
        GUI.DrawTexture(bar, Texture2D.whiteTexture);
        GUI.color = new Color(1f, 1f, 1f, loadingAlpha);
        GUI.DrawTexture(new Rect(bar.x, bar.y, bar.width * progress, bar.height), Texture2D.whiteTexture);
        GUI.color = previous;
    }

    // ---------- Public API for gameplay ----------

    public static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;

    public static Vector2Int WorldToChunk(Vector3 pos) =>
        new(Mathf.FloorToInt(pos.x / Chunk.Size), Mathf.FloorToInt(pos.z / Chunk.Size));

    public static Vector2Int BlockToChunk(Vector3Int block) =>
        new(FloorDiv(block.x, Chunk.Size), FloorDiv(block.z, Chunk.Size));

    public bool IsChunkLoaded(Vector3 worldPos) => chunks.ContainsKey(WorldToChunk(worldPos));

    public bool IsBlockLoaded(Vector3Int pos) =>
        pos.y >= 0 && pos.y < Chunk.Height && chunks.ContainsKey(BlockToChunk(pos));

    /// <summary>
    /// Block, shape and water at pos. Outside loaded chunks only edits, plain terrain and sea water are known.
    /// </summary>
    public VoxelEdit GetVoxel(Vector3Int pos)
    {
        if (pos.y < 0 || pos.y >= Chunk.Height) return default;

        Vector2Int c = BlockToChunk(pos);
        if (chunks.TryGetValue(c, out var chunk)) return chunk.GetVoxelWorld(pos);
        if (edits.TryGetValue(c, out var e) && e.TryGetValue(pos, out VoxelEdit edited)) return edited;

        VoxelColumn col = Generator.GetColumn(pos.x, pos.z);
        bool sea = pos.y > col.height && pos.y <= Generator.SeaLevel;
        return new VoxelEdit { id = col.GetBlock(pos.y), fluid = sea ? Fluid.Source : Fluid.None };
    }

    public byte GetBlock(Vector3Int pos) => GetVoxel(pos).id;

    /// <summary>Shape (see VoxelShapes) of the block at pos. Generated slopes are only known in loaded chunks.</summary>
    public byte GetShape(Vector3Int pos) => GetVoxel(pos).shape;

    /// <summary>Water at pos (see Fluid), 0 for none.</summary>
    public byte GetFluid(Vector3Int pos) => GetVoxel(pos).fluid;

    /// <summary>Block definition at pos, or null for air.</summary>
    public BlockDefinition GetBlockDefinition(Vector3Int pos) => Blocks.Get(GetBlock(pos));

    /// <summary>
    /// Places (id > 0) or removes (id = 0) a block, optionally with a shape such as VoxelShapes.Slope(...).
    /// Water loggable blocks keep a water source they are placed into, and removing a block leaves its water behind.
    /// Edits persist when chunks unload/reload.
    /// </summary>
    public void SetBlock(Vector3Int pos, byte id, byte shape = VoxelShapes.Cube)
    {
        byte fluid = GetFluid(pos);
        if (id != 0 && !(Fluid.IsSource(fluid) && (Blocks.Get(id)?.waterLoggable ?? false))) fluid = Fluid.None;
        SetVoxel(pos, new VoxelEdit { id = id, shape = shape, fluid = fluid });
    }

    /// <summary>Places a block by name from the Block Database. An empty name removes the block.</summary>
    public void SetBlock(Vector3Int pos, string blockName, byte shape = VoxelShapes.Cube) =>
        SetBlock(pos, Blocks.GetId(blockName), shape);

    /// <summary>
    /// Sets the water at pos without touching the block. Only air and water loggable blocks (sources only) hold water.
    /// Returns false if the water can't go there.
    /// </summary>
    public bool SetFluid(Vector3Int pos, byte fluid)
    {
        if (pos.y < 0 || pos.y >= Chunk.Height) return false;
        VoxelEdit v = GetVoxel(pos);
        if (fluid != Fluid.None && v.id != 0 && !(Fluid.IsSource(fluid) && (Blocks.Get(v.id)?.waterLoggable ?? false)))
            return false;
        v.fluid = fluid;
        SetVoxel(pos, v);
        return true;
    }

    /// <summary>Writes a whole voxel and wakes up the water around it.</summary>
    public void SetVoxel(Vector3Int pos, VoxelEdit voxel)
    {
        if (pos.y < 0 || pos.y >= Chunk.Height) return;
        if (voxel.id == 0) voxel.shape = VoxelShapes.Cube;

        Vector2Int c = BlockToChunk(pos);
        int lx = pos.x - c.x * Chunk.Size;
        int lz = pos.z - c.y * Chunk.Size;

        ApplyEdit(c, pos, voxel);
        // Neighbors keep a copy of border blocks for face culling
        if (lx == 0) ApplyEdit(c + Vector2Int.left, pos, voxel);
        if (lx == Chunk.Size - 1) ApplyEdit(c + Vector2Int.right, pos, voxel);
        if (lz == 0) ApplyEdit(c + Vector2Int.down, pos, voxel);
        if (lz == Chunk.Size - 1) ApplyEdit(c + Vector2Int.up, pos, voxel);

        fluids.ScheduleAround(pos);

        // Plants need a block under them
        Vector3Int above = pos + Vector3Int.up;
        if ((voxel.id == 0 || Blocks.IsPlant(voxel.id)) && Blocks.IsPlant(GetBlock(above)))
            SetVoxel(above, new VoxelEdit { fluid = GetFluid(above) });
    }

    /// <summary>Whether a plant can stand on the block at pos.</summary>
    public bool CanHoldPlant(Vector3Int pos)
    {
        byte id = GetBlock(pos);
        return id != 0 && !Blocks.IsPlant(id);
    }

    /// <summary>Name of the block at pos, or null for air.</summary>
    public string GetBlockName(Vector3Int pos)
    {
        byte id = GetBlock(pos);
        return id == 0 ? null : Blocks.GetName(id);
    }

    void ApplyEdit(Vector2Int c, Vector3Int pos, VoxelEdit edit)
    {
        if (!edits.TryGetValue(c, out var e)) edits[c] = e = new Dictionary<Vector3Int, VoxelEdit>();
        e[pos] = edit;
        editVersions[c] = EditVersion(c) + 1;
        if (chunks.TryGetValue(c, out var chunk))
        {
            chunk.SetVoxelWorld(pos, edit);
            dirtyChunks.Add(chunk);
        }
    }

    /// <summary>
    /// Steps through voxels along a ray until stop returns true. hit is that voxel, previous the one before it.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, System.Func<VoxelEdit, bool> stop,
        out Vector3Int hit, out Vector3Int previous) =>
        Raycast(origin, direction, maxDistance, (v, cell, enter, exit) => stop(v), out hit, out previous);

    /// <summary>
    /// The first solid block along a ray. Slopes only count where the ray passes through their solid part, so you can
    /// aim past one at the block behind. previous is the cell the ray came from, where a placed block goes. Doesn't
    /// use colliders, so it works the moment a chunk is generated.
    /// </summary>
    public bool RaycastBlock(Vector3 origin, Vector3 direction, float maxDistance, out Vector3Int hit, out Vector3Int previous)
    {
        direction.Normalize();
        return Raycast(origin, direction, maxDistance, (v, cell, enter, exit) =>
        {
            if (v.id == 0) return false;
            if (VoxelShapes.IsCube(v.shape)) return true;
            // Walk the part of the ray inside the cell and see if it dips under the sloped top
            Vector3 a = origin + direction * enter - cell, b = origin + direction * Mathf.Min(exit, maxDistance) - cell;
            const int Samples = 12;
            for (int i = 0; i <= Samples; i++)
            {
                Vector3 p = Vector3.Lerp(a, b, i / (float)Samples);
                if (p.y <= VoxelShapes.SurfaceHeight(v.shape, Mathf.Clamp01(p.x), Mathf.Clamp01(p.z)) + 0.001f) return true;
            }
            return false;
        }, out hit, out previous);
    }

    /// <summary>Steps through voxels along a ray; stop gets each voxel, its cell and the distances where the ray enters and leaves it.</summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, System.Func<VoxelEdit, Vector3Int, float, float, bool> stop,
        out Vector3Int hit, out Vector3Int previous)
    {
        direction.Normalize();
        hit = Vector3Int.FloorToInt(origin);
        previous = hit;
        Vector3Int step = new(direction.x >= 0 ? 1 : -1, direction.y >= 0 ? 1 : -1, direction.z >= 0 ? 1 : -1);
        Vector3 delta = default, next = default;
        for (int a = 0; a < 3; a++)
        {
            if (Mathf.Abs(direction[a]) < 1e-6f)
            {
                delta[a] = next[a] = float.MaxValue;
                continue;
            }
            delta[a] = Mathf.Abs(1f / direction[a]);
            next[a] = (step[a] > 0 ? hit[a] + 1 - origin[a] : origin[a] - hit[a]) * delta[a];
        }

        float travelled = 0f;
        while (travelled <= maxDistance)
        {
            float exit = Mathf.Min(next.x, Mathf.Min(next.y, next.z));
            if (stop(GetVoxel(hit), hit, travelled, exit)) return true;
            previous = hit;
            if (next.x < next.y && next.x < next.z) { hit.x += step.x; travelled = next.x; next.x += delta.x; }
            else if (next.y < next.z) { hit.y += step.y; travelled = next.y; next.y += delta.y; }
            else { hit.z += step.z; travelled = next.z; next.z += delta.z; }
        }
        return false;
    }

    /// <summary>Y of the topmost solid block in a column.</summary>
    public int GetSurfaceHeight(int x, int z)
    {
        for (int y = Chunk.Height - 1; y >= 0; y--)
            if (GetBlock(new Vector3Int(x, y, z)) != 0) return y;
        return 0;
    }

    /// <summary>A position standing on top of the terrain at (x, z).</summary>
    public Vector3 GetSpawnPosition(int x, int z) => new(x + 0.5f, GetSurfaceHeight(x, z) + 1f, z + 0.5f);

    public BiomeConfig GetBiome(Vector3 worldPos) =>
        Generator.GetBiome(Mathf.FloorToInt(worldPos.x), Mathf.FloorToInt(worldPos.z));
}
