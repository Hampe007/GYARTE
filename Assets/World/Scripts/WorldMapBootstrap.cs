using UnityEngine;
using UnityEngine.SceneManagement;

public static class WorldMapBootstrap
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Install()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        EnsureWorldMapController();
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        EnsureWorldMapController();
    }

    private static void EnsureWorldMapController()
    {
        if (Object.FindFirstObjectByType<WorldMapController>() != null)
        {
            return;
        }

        TileStreamCoordinator streaming = Object.FindFirstObjectByType<TileStreamCoordinator>();
        if (streaming == null)
        {
            return;
        }

        GameObject controllerObject = new GameObject("WorldMapController_Auto");
        Object.DontDestroyOnLoad(controllerObject);
        WorldMapController controller = controllerObject.AddComponent<WorldMapController>();
        controller.Bind(streaming);
    }
}
