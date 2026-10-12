using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// A chunk in the scene: its generated data (see ChunkData) and the meshes built from it. Chunks arrive from worker
/// threads already generated and meshed; edits rebuild the mesh right away on the main thread.
/// </summary>
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class Chunk : MonoBehaviour
{
    public const int Size = 16;
    public const int Height = 128;

    public Vector2Int Coord => data.coord;
    public ChunkData Data => data;

    ChunkData data;
    VoxelWorld world;
    Mesh mesh, waterMesh, plantMesh;
    MeshCollider meshCollider;

    static readonly ChunkMesh mainThreadMesh = new();

    /// <summary>
    /// Shows a chunk built off the main thread. The collider is left for AssignCollider, so it can be baked on a
    /// worker thread first.
    /// </summary>
    public void Initialize(VoxelWorld world, ChunkData data, ChunkMesh built)
    {
        this.world = world;
        this.data = data;
        if (world.generateColliders) meshCollider = gameObject.AddComponent<MeshCollider>();
        Upload(built, assignCollider: false);
    }

    public bool HasSolidMesh => mesh != null && mesh.vertexCount > 0;
    public EntityId MeshId => mesh.GetEntityId();
    public MeshColliderCookingOptions CookingOptions => meshCollider != null ? meshCollider.cookingOptions : default;

    /// <summary>Hooks the mesh up to the collider. Instant if the mesh was baked with Physics.BakeMesh beforehand.</summary>
    public void AssignCollider()
    {
        if (meshCollider == null) return;
        meshCollider.sharedMesh = null;
        meshCollider.sharedMesh = HasSolidMesh ? mesh : null;
    }

    public bool Contains(Vector3Int worldPos, out int index) => data.Contains(worldPos, out index);

    public byte GetBlockWorld(Vector3Int worldPos) => Contains(worldPos, out int i) ? data.voxels[i] : (byte)0;

    public byte GetShapeWorld(Vector3Int worldPos) => Contains(worldPos, out int i) ? data.shapes[i] : VoxelShapes.Cube;

    public byte GetFluidWorld(Vector3Int worldPos) => Contains(worldPos, out int i) ? data.fluids[i] : Fluid.None;

    public VoxelEdit GetVoxelWorld(Vector3Int worldPos) => data.Get(worldPos);

    public void SetVoxelWorld(Vector3Int worldPos, VoxelEdit voxel) => data.Set(worldPos, voxel);

    /// <summary>Rebuilds the meshes and collider right away, e.g. after edits.</summary>
    public void BuildMesh()
    {
        mainThreadMesh.Build(data, world.Blocks, world.Lighting);
        Upload(mainThreadMesh, assignCollider: true);
    }

    void Upload(ChunkMesh m, bool assignCollider)
    {
        if (mesh == null)
        {
            mesh = new Mesh { name = $"Chunk {Coord}", indexFormat = IndexFormat.UInt32 };
            mesh.MarkDynamic();
            GetComponent<MeshFilter>().sharedMesh = mesh;
            GetComponent<MeshRenderer>().sharedMaterial = world.ChunkMaterial;
        }

        mesh.Clear();
        mesh.SetVertices(m.verts);
        mesh.SetNormals(m.normals);
        mesh.SetUVs(0, m.uvs);
        mesh.SetColors(m.colors);
        mesh.SetTriangles(m.tris, 0);
        mesh.RecalculateBounds();

        if (assignCollider) AssignCollider();
        UploadChild(ref waterMesh, "Water", world.WaterMaterial, m.waterVerts, m.waterNormals, m.waterUvs, m.waterColors, m.waterTris);
        UploadChild(ref plantMesh, "Plants", world.PlantMaterial, m.plantVerts, m.plantNormals, m.plantUvs, m.plantColors, m.plantTris);
    }

    // Water and plants live on child objects so they can use their own material and have no collider
    void UploadChild(ref Mesh childMesh, string label, Material material,
        List<Vector3> verts, List<Vector3> normals, List<Vector2> uvs, List<Color> colors, List<int> tris)
    {
        if (childMesh == null)
        {
            if (verts.Count == 0) return;
            var go = new GameObject(label) { layer = gameObject.layer };
            go.transform.SetParent(transform, false);
            childMesh = new Mesh { name = $"{label} {Coord}", indexFormat = IndexFormat.UInt32 };
            childMesh.MarkDynamic();
            go.AddComponent<MeshFilter>().sharedMesh = childMesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = material;
            r.shadowCastingMode = ShadowCastingMode.Off;
        }

        childMesh.Clear();
        childMesh.SetVertices(verts);
        childMesh.SetNormals(normals);
        childMesh.SetUVs(0, uvs);
        childMesh.SetColors(colors);
        childMesh.SetTriangles(tris, 0);
        childMesh.RecalculateBounds();
    }

    void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
        if (waterMesh != null) Destroy(waterMesh);
        if (plantMesh != null) Destroy(plantMesh);
    }
}
