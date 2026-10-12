using UnityEngine;

/// <summary>Little bits of a block that fly out and fade when it breaks. Purely visual, no collisions.</summary>
public class BlockDebris : MonoBehaviour
{
    const float Lifetime = 0.6f;
    const float Gravity = 18f;

    Vector3 velocity;
    float age;
    Vector3 startScale;

    public static void Burst(VoxelWorld world, byte blockId, Vector3Int block, int count = 10)
    {
        if (blockId == 0 || world == null) return;
        Mesh mesh = BlockShapeMesh.GetBlockMesh(world.Blocks, blockId, VoxelShapes.Cube, world.Lighting);
        Vector3 center = block + Vector3.one * 0.5f;
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject("Debris") { layer = 2 };
            Vector3 offset = Random.insideUnitSphere * 0.35f;
            go.transform.position = center + offset;
            go.transform.rotation = Random.rotation;
            go.transform.localScale = Vector3.one * Random.Range(0.06f, 0.12f);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = world.Blocks.IsPlant(blockId) ? world.PlantMaterial : world.ChunkMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            var debris = go.AddComponent<BlockDebris>();
            debris.velocity = offset.normalized * Random.Range(1.5f, 3f) + Vector3.up * Random.Range(2f, 4f);
            debris.startScale = go.transform.localScale;
        }
    }

    void Update()
    {
        float dt = Time.deltaTime;
        age += dt;
        if (age >= Lifetime)
        {
            Destroy(gameObject);
            return;
        }
        velocity.y -= Gravity * dt;
        transform.position += velocity * dt;
        transform.localScale = startScale * (1f - age / Lifetime);
    }
}
