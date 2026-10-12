using UnityEditor;
using UnityEngine;

public static class PlayerSetup
{
    /// <summary>Creates a player, or adds any missing player components to the existing one.</summary>
    [MenuItem("Tools/Voxel/Create Player")]
    static void Create()
    {
        var existing = Object.FindAnyObjectByType<PlayerController>();
        if (existing != null)
        {
            int added = AddMissingComponents(existing.gameObject);
            Selection.activeObject = existing.gameObject;
            Debug.Log(added > 0 ? $"Added {added} missing component(s) to the existing player." : "The scene already has a player.", existing);
            return;
        }

        var player = new GameObject("Player");
        Undo.RegisterCreatedObjectUndo(player, "Create Player");
        player.transform.position = Vector3.zero;

        Camera cam = Camera.main;
        if (cam == null)
        {
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            Undo.RegisterCreatedObjectUndo(camGo, "Create Player");
            cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
        }
        Undo.SetTransformParent(cam.transform, player.transform, "Create Player");
        Undo.RecordObject(cam.transform, "Create Player");
        Undo.RecordObject(cam, "Create Player");
        cam.nearClipPlane = 0.05f;

        // Adds the Rigidbody and CapsuleCollider it requires, and sets them up in Reset
        var controller = player.AddComponent<PlayerController>();
        cam.transform.localPosition = new Vector3(0f, controller.eyeHeight, 0f);
        cam.transform.localRotation = Quaternion.identity;
        controller.playerCamera = cam;
        controller.world = Object.FindAnyObjectByType<VoxelWorld>();
        if (controller.world != null)
        {
            Undo.RecordObject(controller.world, "Create Player");
            controller.world.viewer = player.transform;
            EditorUtility.SetDirty(controller.world);
        }
        AddMissingComponents(player);

        Selection.activeObject = player;
    }

    static int AddMissingComponents(GameObject player)
    {
        int added = 0;
        void Ensure<T>() where T : Component
        {
            if (player.GetComponent<T>() != null) return;
            Undo.AddComponent<T>(player);
            added++;
        }

        // Players from the CharacterController version move with a Rigidbody now
        if (player.TryGetComponent(out CharacterController old))
        {
            Undo.DestroyObjectImmediate(old);
            added++;
        }
        Ensure<Rigidbody>();
        Ensure<CapsuleCollider>();
        Ensure<PlayerInventory>();
        Ensure<PlayerHealth>();
        Ensure<InventoryUI>();
        Ensure<PlayerInteraction>();
        Ensure<PlayerAchievements>();
        return added;
    }
}
