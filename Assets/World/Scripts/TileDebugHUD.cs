#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Text;
using Mirror;
using UnityEngine;

public class TileDebugHUD : MonoBehaviour
{
    public TileStreamCoordinator coordinator;
    [Range(0.01f, 1f)]
    public float smoothing = 0.1f;

    private float deltaTime;

    private void Awake()
    {
        if (coordinator == null)
        {
            coordinator = FindObjectOfType<TileStreamCoordinator>();
        }
    }

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;
        deltaTime += (dt - deltaTime) * Mathf.Clamp01(smoothing);
    }

    private void OnGUI()
    {
        if (coordinator == null)
        {
            return;
        }

        float fps = deltaTime > 1e-4f ? 1f / deltaTime : 0f;

        var sb = new StringBuilder();
        sb.AppendLine($"FPS: {fps:0.0}");

        if (NetworkServer.active)
        {
            sb.AppendLine($"Server tiles: {coordinator.ServerTiles.Count}");
        }

        if (NetworkClient.active)
        {
            sb.AppendLine($"Client tiles: {coordinator.ClientTiles.Count}");
        }

        var content = sb.ToString().TrimEnd();
        var style = new GUIStyle(GUI.skin.box)
        {
            fontSize = 14,
            alignment = TextAnchor.UpperLeft
        };

        Vector2 size = style.CalcSize(new GUIContent(content));
        var rect = new Rect(10f, 10f, size.x + 20f, size.y + 20f);

        GUI.Box(rect, content, style);
    }
}
#endif
