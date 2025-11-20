using UnityEngine;
using UnityEditor;

[InitializeOnLoad]
public static class TerrainPrefabPainterEditor
{
    static TerrainPrefabPainter painter;
    static SceneView sceneView;

    static TerrainPrefabPainterEditor()
    {
        // Hook into the SceneView GUI loop
        SceneView.duringSceneGui += OnSceneGUI;
        EditorApplication.update += FindPainterWindow;
    }

    static void FindPainterWindow()
    {
        if (painter == null)
            painter = EditorWindow.GetWindow<TerrainPrefabPainter>(false, null, false);
    }

    static void OnSceneGUI(SceneView view)
    {
        if (painter == null) return;

        // Only run when painter has interactive placement enabled
        if (!painter.placingVolume) return;

        Event e = Event.current;

        // Ray from mouse
        Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
        RaycastHit hit;

        // Move preview
        if (Physics.Raycast(ray, out hit, 5000f))
        {
            painter.preview.SetPosition(hit.point);
        }
        else
        {
            painter.preview.SetPosition(ray.origin + ray.direction * 20f);
        }

        // Left click = confirm
        if (e.type == EventType.MouseDown && e.button == 0)
        {
            painter.ConfirmVolumePlacement(painter.preview.preview.transform.position);
            painter.StopPlacingVolume();
            e.Use();
        }

        // Right click = cancel
        if (e.type == EventType.MouseDown && e.button == 1)
        {
            painter.StopPlacingVolume();
            e.Use();
        }

        SceneView.RepaintAll();
    }
}