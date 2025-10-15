// Assets/Scripts/Tiles/Editor/TileGizmoToggle.cs
#if UNITY_EDITOR
using UnityEditor;

public static class TileGizmoToggle
{
    private const string MenuPath = "Tools/Tiles/Toggle Tile Gizmos %&t"; // Ctrl/Cmd+Alt+T

    [MenuItem(MenuPath)]
    public static void Toggle()
    {
        TileGridGizmo.ShowGizmos = !TileGridGizmo.ShowGizmos;
        EditorApplication.RepaintHierarchyWindow();
        SceneView.RepaintAll();
    }

    [MenuItem(MenuPath, true)]
    public static bool Validate()
    {
        Menu.SetChecked(MenuPath, TileGridGizmo.ShowGizmos);
        return true;
    }
}
#endif