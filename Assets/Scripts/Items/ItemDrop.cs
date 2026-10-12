using System.Collections.Generic;
using UnityEngine;

/// <summary>A dropped item stack lying in the world. Walk near it to pick it up.</summary>
public class ItemDrop : MonoBehaviour
{
    const float BlockSize = 0.25f;
    const float SpriteSize = 0.4f;
    const float PickupRadius = 1.4f;
    const float Lifetime = 300f;
    const int IgnoreRaycastLayer = 2;

    public ItemStack stack;

    static readonly Dictionary<Texture2D, Material> spriteMaterials = new();
    static Mesh quadMesh;

    float pickupDelay;
    float age;
    Transform visual;

    /// <summary>Spawns a drop. Pickup delay keeps the player from instantly grabbing what they just threw.</summary>
    public static ItemDrop Spawn(VoxelWorld world, ItemStack stack, Vector3 position, Vector3 velocity, float pickupDelay = 0.5f)
    {
        if (stack.IsEmpty || world == null) return null;
        ItemInfo info = world.Items.Get(stack.id);
        if (info == null) return null;

        var go = new GameObject($"Drop {info.name}") { layer = IgnoreRaycastLayer };
        go.transform.position = position;
        var drop = go.AddComponent<ItemDrop>();
        drop.stack = stack;
        drop.pickupDelay = pickupDelay;

        var box = go.AddComponent<BoxCollider>();
        box.size = Vector3.one * BlockSize;
        var body = go.AddComponent<Rigidbody>();
        body.mass = 0.1f;
        body.linearDamping = 0.5f;
        body.angularDamping = 5f;
        body.constraints = RigidbodyConstraints.FreezeRotation;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.linearVelocity = velocity;

        var visual = new GameObject("Visual") { layer = IgnoreRaycastLayer };
        visual.transform.SetParent(go.transform, false);
        var filter = visual.AddComponent<MeshFilter>();
        var renderer = visual.AddComponent<MeshRenderer>();
        if (info.blockId != 0 && !world.Blocks.IsPlant(info.blockId))
        {
            // Blocks drop as tiny blocks, slopes as tiny slopes
            filter.sharedMesh = BlockShapeMesh.GetBlockMesh(world.Blocks, info.blockId, info.DisplayShape, world.Lighting);
            renderer.sharedMaterial = world.ChunkMaterial;
            visual.transform.localScale = Vector3.one * BlockSize;
        }
        else
        {
            filter.sharedMesh = GetQuad();
            renderer.sharedMaterial = SpriteMaterial(info.icon);
            visual.transform.localScale = Vector3.one * SpriteSize;
        }
        drop.visual = visual.transform;

        foreach (var player in PlayerInventory.All)
            if (player != null)
                foreach (Collider c in player.GetComponents<Collider>()) Physics.IgnoreCollision(box, c);

        return drop;
    }

    static Mesh GetQuad()
    {
        if (quadMesh != null) return quadMesh;
        quadMesh = new Mesh { name = "Item Sprite" };
        quadMesh.SetVertices(new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f), new Vector3(0.5f, -0.5f, 0f) });
        quadMesh.SetUVs(0, new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) });
        quadMesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
        quadMesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
        return quadMesh;
    }

    static Material SpriteMaterial(Texture2D icon)
    {
        Texture2D key = icon != null ? icon : Texture2D.whiteTexture;
        if (spriteMaterials.TryGetValue(key, out Material mat) && mat != null) return mat;

        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        mat = new Material(shader) { name = $"Drop {key.name}", mainTexture = key };
        if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 0f);
        // Cut out the transparent background and show both sides of the flat sprite
        mat.SetFloat("_AlphaClip", 1f);
        mat.SetFloat("_Cutoff", 0.5f);
        mat.EnableKeyword("_ALPHATEST_ON");
        mat.SetFloat("_Cull", 0f);
        spriteMaterials[key] = mat;
        return mat;
    }

    void Update()
    {
        age += Time.deltaTime;
        if (age > Lifetime || transform.position.y < -64f)
        {
            Destroy(gameObject);
            return;
        }

        if (visual != null)
        {
            visual.localRotation = Quaternion.Euler(0f, age * 90f, 0f);
            visual.localPosition = new Vector3(0f, Mathf.Sin(age * 2.5f) * 0.05f + 0.08f, 0f);
        }

        if (age < pickupDelay) return;
        foreach (var player in PlayerInventory.All)
        {
            if (player == null || !player.isActiveAndEnabled || player.Inventory == null) continue;
            Vector3 center = player.transform.position + Vector3.up * 0.9f;
            if ((center - transform.position).sqrMagnitude > PickupRadius * PickupRadius) continue;

            stack = player.Inventory.Add(stack);
            if (stack.IsEmpty)
            {
                Destroy(gameObject);
                return;
            }
        }
    }
}
