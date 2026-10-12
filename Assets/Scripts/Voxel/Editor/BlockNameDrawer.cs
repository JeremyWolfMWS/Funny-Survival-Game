using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(BlockNameAttribute))]
public class BlockNameDrawer : PropertyDrawer
{
    const string None = "(None)";

    static readonly List<string> cachedNames = new();
    static double cacheTime = -1;

    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        List<string> names = GetBlockNames();
        if (property.propertyType != SerializedPropertyType.String || names.Count == 0)
        {
            EditorGUI.PropertyField(position, property, label);
            return;
        }

        string current = property.stringValue;
        var options = new List<string> { None };
        options.AddRange(names);

        int index = string.IsNullOrWhiteSpace(current)
            ? 0
            : options.FindIndex(1, n => string.Equals(n, current.Trim(), System.StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            options.Add($"{current} (missing)");
            index = options.Count - 1;
        }

        var contents = new GUIContent[options.Count];
        for (int i = 0; i < contents.Length; i++) contents[i] = new GUIContent(options[i]);

        label = EditorGUI.BeginProperty(position, label, property);
        EditorGUI.BeginChangeCheck();
        int picked = EditorGUI.Popup(position, label, index, contents);
        if (EditorGUI.EndChangeCheck() && picked <= names.Count)
            property.stringValue = picked == 0 ? "" : options[picked];
        EditorGUI.EndProperty();
    }

    // Names from every Block Database in the project, plus the blocks Ore Managers make
    static List<string> GetBlockNames()
    {
        if (EditorApplication.timeSinceStartup - cacheTime < 1.0) return cachedNames;
        cacheTime = EditorApplication.timeSinceStartup;
        cachedNames.Clear();

        var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (string guid in AssetDatabase.FindAssets("t:BlockDatabase"))
        {
            var db = AssetDatabase.LoadAssetAtPath<BlockDatabase>(AssetDatabase.GUIDToAssetPath(guid));
            if (db == null) continue;
            for (int i = 0; i < db.blocks.Count; i++)
            {
                string n = db.blocks[i].name?.Trim();
                if (!string.IsNullOrEmpty(n) && seen.Add(n)) cachedNames.Add(n);
            }
        }
        foreach (string guid in AssetDatabase.FindAssets("t:OreDatabase"))
        {
            var db = AssetDatabase.LoadAssetAtPath<OreDatabase>(AssetDatabase.GUIDToAssetPath(guid));
            if (db == null) continue;
            foreach (OreDefinition ore in db.ores)
            {
                if (ore == null || string.IsNullOrWhiteSpace(ore.name)) continue;
                string n = ore.BlockName;
                if (seen.Add(n)) cachedNames.Add(n);
            }
        }
        return cachedNames;
    }
}
