using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Hold left click to mine (speed depends on the tool) or attack the enemy you're aiming at, right click to place
/// blocks and slopes, use buckets and open crafting / cutting tables (sneak to place against them instead), middle
/// click picks a block from the inventory. Slopes rise away from you and turn into corners next to other slopes.
/// </summary>
[RequireComponent(typeof(PlayerController), typeof(PlayerInventory))]
public class PlayerInteraction : MonoBehaviour
{
    public float reach = 5f;
    [Tooltip("How far away enemies can be hit.")]
    public float attackReach = 4f;
    [Tooltip("Hits while falling do this much more damage, like Minecraft's critical hits.")]
    public float criticalMultiplier = 1.5f;
    public float placeRepeat = 0.2f;
    [Tooltip("Pause between breaking one block and starting the next while holding the button.")]
    public float breakCooldown = 0.2f;
    [Tooltip("Optional material for the block outline. Leave empty for a black unlit one.")]
    public Material highlightMaterial;

    PlayerController controller;
    PlayerInventory inventory;
    InventoryUI ui;
    VoxelWorld world;

    bool hasTarget;
    Vector3Int targetBlock, placeBlock;
    GameObject highlight;
    float nextPlaceTime, nextBreakTime, nextAttackTime;
    IDamageable aimedEnemy;
    readonly RaycastHit[] attackHits = new RaycastHit[8];
    Vector3Int miningBlock;
    float miningProgress;
    GameObject crack;
    MeshFilter crackFilter;
    Material crackMaterial;
    // The Block Database's breaking sheet cut into frames, or placeholder cracks without one
    Texture2D[] defaultCracks;

    public bool HasTarget => hasTarget;
    public Vector3Int TargetBlock => targetBlock;
    /// <summary>0-1 progress of the block being mined.</summary>
    public float MiningProgress => miningProgress;

    /// <summary>The block or slope item matching what the player just broke.</summary>
    public event System.Action<ItemInfo> BlockBroken;
    /// <summary>The block or slope item the player just placed.</summary>
    public event System.Action<ItemInfo> BlockPlaced;

    void Awake()
    {
        controller = GetComponent<PlayerController>();
        inventory = GetComponent<PlayerInventory>();
        ui = GetComponent<InventoryUI>();
        highlight = CreateHighlight();
    }

    void Start() => world = controller.world;

    void OnDestroy()
    {
        if (highlight != null) Destroy(highlight);
        if (crack != null) Destroy(crack);
        if (crackMaterial != null) Destroy(crackMaterial);
        if (defaultCracks != null)
            foreach (Texture2D t in defaultCracks) Destroy(t);
    }

    void LateUpdate() => UpdateCrack();

    void Update()
    {
        if (world == null || inventory.Inventory == null) return;
        Mouse mouse = Mouse.current;

        UpdateTarget();
        UpdateEnemyTarget();
        if (mouse == null || !controller.HasFocus || controller.GrabbedCursorThisFrame)
        {
            miningProgress = 0f;
            return;
        }

        if (aimedEnemy != null)
        {
            miningProgress = 0f;
            if (mouse.leftButton.isPressed && Time.time >= nextAttackTime) Attack();
        }
        else UpdateMining(mouse);
        if (mouse.rightButton.wasPressedThisFrame) nextPlaceTime = 0f;
        if (mouse.rightButton.isPressed && Time.time >= nextPlaceTime)
        {
            nextPlaceTime = Time.time + placeRepeat;
            Use();
        }
        if (mouse.middleButton.wasPressedThisFrame && hasTarget) PickBlock();
    }

    // ---------- Targeting ----------

    void UpdateTarget()
    {
        hasTarget = false;
        Camera cam = controller.playerCamera;
        // Walks the voxel grid instead of hitting colliders, so the player's own body, item drops and chunks whose
        // collider is still being built never get in the way
        if (cam != null && world.RaycastBlock(cam.transform.position, cam.transform.forward, reach, out Vector3Int hit, out Vector3Int before))
        {
            hasTarget = true;
            targetBlock = hit;
            // Placing while aiming at tall grass or a flower replaces it, like in Minecraft
            placeBlock = world.Blocks.IsPlant(world.GetBlock(hit)) ? hit : before;
        }

        highlight.SetActive(hasTarget);
        if (hasTarget) highlight.transform.position = targetBlock;
    }

    /// <summary>The living enemy under the crosshair within attack reach, unless a block is in front of it.</summary>
    void UpdateEnemyTarget()
    {
        aimedEnemy = null;
        Camera cam = controller.playerCamera;
        if (cam == null) return;
        Vector3 origin = cam.transform.position, dir = cam.transform.forward;
        float blockDistance = hasTarget ? Vector3.Distance(origin, targetBlock + Vector3.one * 0.5f) - 0.5f : float.MaxValue;

        int n = Physics.RaycastNonAlloc(origin, dir, attackHits, attackReach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float best = float.MaxValue;
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = attackHits[i];
            if (h.distance >= best || h.distance > blockDistance || h.collider.attachedRigidbody == controller.Body) continue;
            var target = h.collider.GetComponentInParent<IDamageable>();
            if (target == null || !target.IsAlive) continue;
            aimedEnemy = target;
            best = h.distance;
        }
    }

    void Attack()
    {
        ItemDefinition weapon = inventory.SelectedInfo?.definition;
        float damage = weapon?.attackDamage ?? 1f;
        float rate = weapon?.attackSpeed ?? 4f;
        bool crit = !controller.IsGrounded && !controller.IsInWater && controller.Body.linearVelocity.y < -0.5f;
        if (crit) damage *= criticalMultiplier;

        aimedEnemy.TakeHit(damage, controller.Forward * (crit ? 5f : 3.5f) + Vector3.up * 2.5f, weapon);
        if (weapon != null && weapon.toolType != ToolType.None) inventory.DamageSelectedTool();
        nextAttackTime = Time.time + 1f / Mathf.Max(0.1f, rate);
    }

    // ---------- Mining ----------

    void UpdateMining(Mouse mouse)
    {
        if (!mouse.leftButton.isPressed || !hasTarget || targetBlock.y <= 0)
        {
            miningProgress = 0f;
            if (!mouse.leftButton.isPressed) nextBreakTime = 0f;
            return;
        }
        if (Time.time < nextBreakTime) return;

        if (targetBlock != miningBlock)
        {
            miningBlock = targetBlock;
            miningProgress = 0f;
        }

        BlockDefinition block = world.GetBlockDefinition(targetBlock);
        if (block == null) return;
        float time = BreakTime(block, inventory.SelectedInfo?.definition);
        // Mining underwater or without footing is much slower
        if (controller.IsHeadUnderwater) time *= 5f;
        if (!controller.IsGrounded && !controller.IsInWater) time *= 5f;
        miningProgress = time <= 0f ? 1f : miningProgress + Time.deltaTime / time;
        if (miningProgress < 1f) return;

        Break(targetBlock);
        miningProgress = 0f;
        nextBreakTime = Time.time + breakCooldown;
    }

    static bool IsEffective(BlockDefinition block, ItemDefinition tool) =>
        tool != null && block.preferredTool != ToolType.None && (tool.toolType == block.preferredTool || tool.adaptive);

    /// <summary>Whether the block drops anything when broken with this tool (null = hand).</summary>
    public static bool CanHarvest(BlockDefinition block, ItemDefinition tool) =>
        block.requiredTier <= 0 || (IsEffective(block, tool) && tool.toolTier >= block.requiredTier);

    /// <summary>Seconds to break the block with this tool (null = hand).</summary>
    public static float BreakTime(BlockDefinition block, ItemDefinition tool)
    {
        if (block.hardness <= 0f) return 0f;
        float speed = IsEffective(block, tool) ? tool.miningSpeed : 1f;
        return block.hardness * (CanHarvest(block, tool) ? 1.5f : 5f) / speed;
    }

    void Break(Vector3Int pos)
    {
        VoxelEdit voxel = world.GetVoxel(pos);
        BlockDefinition block = world.Blocks.Get(voxel.id);
        if (block == null) return;

        ItemDefinition tool = inventory.SelectedInfo?.definition;
        world.SetBlock(pos, 0);
        BlockDebris.Burst(world, voxel.id, pos);
        SpawnDrops(pos, voxel, block, tool);
        if (tool != null && tool.toolType != ToolType.None) inventory.DamageSelectedTool();

        // A slope losing its neighbor might straighten out next to other slopes
        RefreshNeighborSlopes(pos);

        ItemRegistry items = world.Items;
        int brokenItem = VoxelShapes.IsCube(voxel.shape) ? items.BlockItem(voxel.id) : items.SlopeItem(voxel.id);
        ItemInfo broken = items.Get(brokenItem);
        if (broken != null) BlockBroken?.Invoke(broken);
    }

    void SpawnDrops(Vector3Int pos, VoxelEdit voxel, BlockDefinition block, ItemDefinition tool)
    {
        ItemRegistry items = world.Items;
        if (!CanHarvest(block, tool)) return;
        bool slope = !VoxelShapes.IsCube(voxel.shape);
        int id;
        int count = 1;

        if (tool != null && tool.weaving)
        {
            // Woven tools lift blocks out whole: the block itself, and slopes stay slopes
            id = slope ? items.SlopeItem(voxel.id) : items.BlockItem(voxel.id);
        }
        else
        {
            if (!block.mineableWithoutWeavingPickaxe) return;
            if (Random.value > block.dropChance) return;
            // A cutting table turns 1 block into 2 slopes, so slopes only give their block back half the time
            if (slope && Random.value < 0.5f) return;
            id = string.IsNullOrWhiteSpace(block.drop) ? items.BlockItem(voxel.id) : items.GetId(block.drop);
            count = block.dropCount;
            if (id == 0 && !string.IsNullOrWhiteSpace(block.drop))
                Debug.LogWarning($"Block '{block.name}' drops unknown item '{block.drop}'.");
        }

        Vector3 center = pos + Vector3.one * 0.5f;
        for (int left = count; id != 0 && left > 0;)
        {
            ItemStack stack = items.CreateStack(id, left);
            left -= stack.count;
            var velocity = new Vector3(Random.Range(-1f, 1f), 2.5f, Random.Range(-1f, 1f));
            ItemDrop.Spawn(world, stack, center, velocity, 0.3f);
        }
    }

    // ---------- Using items ----------

    void Use()
    {
        ItemInfo held = inventory.SelectedInfo;
        BlockDefinition target = hasTarget ? world.GetBlockDefinition(targetBlock) : null;

        if (target != null && target.interaction != BlockInteraction.None && !controller.IsSneaking && ui != null)
        {
            ui.Open(target.interaction == BlockInteraction.CraftingTable ? InventoryUI.Mode.CraftingTable : InventoryUI.Mode.CuttingTable);
            return;
        }
        if (held == null) return;

        ItemAction action = held.definition?.action ?? ItemAction.None;
        if (action == ItemAction.EmptyBucket) FillBucket();
        else if (action == ItemAction.WaterBucket) EmptyBucket();
        else if (held.blockId != 0 && hasTarget) PlaceBlock(held);
    }

    void PlaceBlock(ItemInfo held)
    {
        byte existing = world.GetBlock(placeBlock);
        if ((existing != 0 && !world.Blocks.IsPlant(existing)) || placeBlock.y <= 0 || placeBlock.y >= Chunk.Height) return;

        bool plant = world.Blocks.IsPlant(held.blockId);
        if (plant && !world.CanHoldPlant(placeBlock + Vector3Int.down)) return;
        var blockBounds = new Bounds(placeBlock + Vector3.one * 0.5f, Vector3.one * 0.99f);
        if (!plant && blockBounds.Intersects(controller.BodyBounds)) return;

        byte shape = VoxelShapes.Cube;
        if (held.isSlope && !plant)
        {
            Vector3 forward = controller.Forward;
            var dir = Mathf.Abs(forward.x) > Mathf.Abs(forward.z)
                ? new Vector2Int(forward.x > 0f ? 1 : -1, 0)
                : new Vector2Int(0, forward.z > 0f ? 1 : -1);
            shape = SlopeShapeAt(placeBlock, VoxelShapes.DirectionIndex(dir));
        }

        world.SetBlock(placeBlock, held.blockId, shape);
        inventory.ConsumeSelected();
        RefreshNeighborSlopes(placeBlock);
        BlockPlaced?.Invoke(held);
    }

    /// <summary>
    /// Shape for a slope rising toward Directions[dir], stair style: a perpendicular slope on the high side makes an
    /// outer corner, one on the low side an inner corner.
    /// </summary>
    byte SlopeShapeAt(Vector3Int pos, int dir)
    {
        Vector2Int d = VoxelShapes.Directions[dir];
        var high = new Vector3Int(d.x, 0, d.y);

        int back = StraightSlopeAt(pos + high);
        if (back >= 0 && Perpendicular(dir, back))
            return VoxelShapes.OuterCorner(VoxelShapes.DiagonalIndex(d + VoxelShapes.Directions[back]));

        int front = StraightSlopeAt(pos - high);
        if (front >= 0 && Perpendicular(dir, front))
            return VoxelShapes.InnerCorner(VoxelShapes.DiagonalIndex(d + VoxelShapes.Directions[front]));

        return VoxelShapes.Slope(dir);
    }

    int StraightSlopeAt(Vector3Int pos) =>
        world.GetBlock(pos) == 0 ? -1 : VoxelShapes.StraightSlopeDirection(world.GetShape(pos));

    static bool Perpendicular(int a, int b)
    {
        Vector2Int da = VoxelShapes.Directions[a], db = VoxelShapes.Directions[b];
        return da.x * db.x + da.y * db.y == 0;
    }

    /// <summary>Straight slopes next to pos re-check whether they should become corners.</summary>
    void RefreshNeighborSlopes(Vector3Int pos)
    {
        foreach (Vector2Int d in VoxelShapes.Directions)
        {
            var n = pos + new Vector3Int(d.x, 0, d.y);
            int dir = StraightSlopeAt(n);
            if (dir < 0) continue;
            byte shape = SlopeShapeAt(n, dir);
            if (shape != world.GetShape(n)) world.SetBlock(n, world.GetBlock(n), shape);
        }
    }

    void FillBucket()
    {
        Camera cam = controller.playerCamera;
        if (cam == null) return;
        // Stop at the first water or solid block along the view
        if (!world.Raycast(cam.transform.position, cam.transform.forward, reach,
                v => v.fluid != Fluid.None || v.id != 0, out Vector3Int hit, out _)) return;
        if (!Fluid.IsSource(world.GetFluid(hit))) return;

        world.SetFluid(hit, Fluid.None);
        SwapHeldBucket("Water Bucket");
    }

    void EmptyBucket()
    {
        Camera cam = controller.playerCamera;
        if (cam == null) return;
        if (!world.Raycast(cam.transform.position, cam.transform.forward, reach, v => v.id != 0,
                out Vector3Int hit, out Vector3Int before)) return;

        // Fill a water loggable block directly, otherwise the space in front of it
        BlockDefinition block = world.GetBlockDefinition(hit);
        bool placed = block != null && block.waterLoggable && !Fluid.IsSource(world.GetFluid(hit))
            ? world.SetFluid(hit, Fluid.Source)
            : world.SetFluid(before, Fluid.Source);
        if (placed) SwapHeldBucket("Bucket");
    }

    void SwapHeldBucket(string itemName)
    {
        int id = world.Items.GetId(itemName);
        if (id == 0)
        {
            Debug.LogWarning($"No item named '{itemName}' in the Item Database.");
            return;
        }
        inventory.SetSelected(world.Items.CreateStack(id));
    }

    void PickBlock()
    {
        int id = world.Items.BlockItem(world.GetBlock(targetBlock));
        if (id == 0) return;
        ItemStack[] slots = inventory.Inventory.slots;
        int slot = System.Array.FindIndex(slots, s => s.id == id);
        if (slot < 0) return;
        if (slot < Inventory.HotbarSize)
        {
            inventory.SelectSlot(slot);
            return;
        }
        // Swap it into the current hotbar slot
        (slots[slot], slots[inventory.SelectedSlot]) = (slots[inventory.SelectedSlot], slots[slot]);
        inventory.SelectSlot(inventory.SelectedSlot);
    }

    // ---------- Visuals ----------

    GameObject CreateHighlight()
    {
        var go = new GameObject("Block Highlight");
        const float e = 0.002f;
        var mesh = new Mesh { name = "Block Outline" };
        mesh.SetVertices(new[]
        {
            new Vector3(-e, -e, -e), new Vector3(1 + e, -e, -e), new Vector3(1 + e, -e, 1 + e), new Vector3(-e, -e, 1 + e),
            new Vector3(-e, 1 + e, -e), new Vector3(1 + e, 1 + e, -e), new Vector3(1 + e, 1 + e, 1 + e), new Vector3(-e, 1 + e, 1 + e),
        });
        mesh.SetIndices(new[] { 0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7 }, MeshTopology.Lines, 0);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;

        Material mat = highlightMaterial;
        if (mat == null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");
            mat = new Material(shader) { name = "Block Highlight" };
            if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", Color.black);
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", Color.black);
        }
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = mat;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        go.SetActive(false);
        return go;
    }

    // Cracks drawn over the block being mined, one frame per stage of progress
    void UpdateCrack()
    {
        bool show = world != null && miningProgress > 0f && hasTarget && miningBlock == targetBlock;
        if (!show)
        {
            if (crack != null) crack.SetActive(false);
            return;
        }

        if (crack == null)
        {
            crack = new GameObject("Mining Cracks");
            crackFilter = crack.AddComponent<MeshFilter>();
            var renderer = crack.AddComponent<MeshRenderer>();
            crackMaterial = BlockShapeMesh.CreateOverlayMaterial("Mining Cracks");
            renderer.sharedMaterial = crackMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        Texture2D[] frames = world.Blocks.breakingFrames;
        if (frames == null || frames.Length == 0)
        {
            if (defaultCracks == null)
            {
                defaultCracks = world.Blocks.SliceBreakingSheet();
                if (defaultCracks.Length == 0) defaultCracks = CreatePlaceholderCracks(10);
            }
            frames = defaultCracks;
        }
        int stage = Mathf.Clamp(Mathf.FloorToInt(miningProgress * frames.Length), 0, frames.Length - 1);

        crack.SetActive(true);
        crack.transform.position = miningBlock;
        crackFilter.sharedMesh = BlockShapeMesh.GetOverlayMesh(world.GetShape(miningBlock));
        crackMaterial.mainTexture = frames[stage] != null ? frames[stage] : Texture2D.blackTexture;
    }

    // Stand-in crack stages for when there's no breaking sheet at all: lines spreading from the middle
    static Texture2D[] CreatePlaceholderCracks(int stages)
    {
        const int size = 16;
        var rng = new System.Random(7);
        var order = new System.Collections.Generic.List<int>();
        var seen = new bool[size * size];
        for (int branch = 0; branch < 7; branch++)
        {
            float x = 7.5f + (float)(rng.NextDouble() - 0.5) * 3f, y = 7.5f + (float)(rng.NextDouble() - 0.5) * 3f;
            float angle = (float)(branch / 7.0 * Mathf.PI * 2 + rng.NextDouble() * 0.6);
            for (int step = 0; step < 12; step++)
            {
                int px = Mathf.FloorToInt(x), py = Mathf.FloorToInt(y);
                if (px < 0 || px >= size || py < 0 || py >= size) break;
                if (!seen[px + py * size]) { seen[px + py * size] = true; order.Add(px + py * size); }
                angle += (float)(rng.NextDouble() - 0.5) * 1.2f;
                x += Mathf.Cos(angle);
                y += Mathf.Sin(angle);
            }
        }

        var frames = new Texture2D[stages];
        for (int s = 0; s < stages; s++)
        {
            var pixels = new Color32[size * size];
            int shown = Mathf.CeilToInt(order.Count * (s + 1) / (float)stages);
            for (int i = 0; i < shown; i++) pixels[order[i]] = new Color32(20, 20, 20, 200);
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = $"Placeholder Crack {s}", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp
            };
            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            frames[s] = tex;
        }
        return frames;
    }
}
