using UnityEditor;
using UnityEngine;

public class PrefabDeleteWindow : EditorWindow
{
    private GameObject prefabToDelete;

    [MenuItem("Tools/Debug/Delete Prefab Instances")]
    private static void Open()
    {
        GetWindow<PrefabDeleteWindow>("Delete Prefab Instances");
    }

    private void OnGUI()
    {
        GUILayout.Label("Select prefab to delete from the scene", EditorStyles.boldLabel);

        prefabToDelete = (GameObject)EditorGUILayout.ObjectField("Prefab", prefabToDelete, typeof(GameObject), false);

        if (prefabToDelete == null)
        {
            EditorGUILayout.HelpBox("Drag a prefab here.", MessageType.Info);
            return;
        }

        if (GUILayout.Button("Delete All Instances"))
        {
            DeleteAllInstances(prefabToDelete);
        }
    }

    private void DeleteAllInstances(GameObject prefab)
    {
        int deleted = 0;

        // Finds ALL scene objects, including hidden ones
        var allObjects = Resources.FindObjectsOfTypeAll<GameObject>();

        foreach (var obj in allObjects)
        {
            if (!obj.scene.IsValid()) 
                continue; // skip assets

            // Compare by prefab source
            var source = PrefabUtility.GetCorrespondingObjectFromSource(obj);
            if (source == prefab)
            {
                GameObject.DestroyImmediate(obj);
                deleted++;
            }
        }

        Debug.Log($"Deleted {deleted} instance(s) of prefab '{prefab.name}'.");
    }
}