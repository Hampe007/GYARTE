// Fixed overlay in the top-right corner, non-draggable.

using System;
using UnityEngine;
using UnityEngine.Profiling;

public interface IStreamStats
{
    int ActiveTiles { get; }
    int QueuedLoads { get; }
    int LoadsThisFrame { get; }
}

public class PerformanceOverlay : MonoBehaviour
{
    private KeyCode toggleKey = KeyCode.F1;
    private Vector2 margin = new (8f, 8f);
    public bool visible = true;
    public float scale = 3f;

    private float emaDt = 0.0167f;
    private const float EmaAlpha = 0.1f;
    private const int HistSize = 600;
    private readonly float[] dtHist = new float[HistSize];
    private int histIndex;
    private float p99FrameMs;

    public IStreamStats streamStats;
    public Func<int> getActiveTiles;
    public Func<int> getQueuedLoads;
    public Func<int> getLoadsThisFrame;

    void Awake()
    {
        for (int i = 0; i < dtHist.Length; i++) dtHist[i] = emaDt;
        if (streamStats == null)
        {
            foreach (var behaviour in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.InstanceID))
            {
                if (behaviour is IStreamStats stats)
                {
                    streamStats = stats;
                    break;
                }
            }
        }
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            visible = !visible;

        emaDt = Mathf.Lerp(emaDt, Time.unscaledDeltaTime, EmaAlpha);
        dtHist[histIndex] = Time.unscaledDeltaTime;
        histIndex = (histIndex + 1) % HistSize;

        if (Time.frameCount % 15 == 0)
            p99FrameMs = PercentileMs(dtHist, 0.99f);
    }

    void OnGUI()
    {
        if (!visible) return;

        var prevMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

        float scaledScreenWidth = Screen.width / scale;
        float rectWidth = 380f;
        float rectHeight = 100f;
        float x = scaledScreenWidth - rectWidth - margin.x;
        float y = margin.y;
        var rect = new Rect(x, y, rectWidth, rectHeight);
        GUILayout.BeginArea(rect, GUI.skin.box);

        float fps = 1f / Mathf.Max(emaDt, 1e-5f);
        float frameMs = emaDt * 1000f;

        long monoUsed = Profiler.GetMonoUsedSizeLong();
        long totalAlloc = Profiler.GetTotalAllocatedMemoryLong();
        long totalReserved = Profiler.GetTotalReservedMemoryLong();

        float monoMb = monoUsed / (1024f * 1024f);
        float allocMb = totalAlloc / (1024f * 1024f);
        float reservedMb = totalReserved / (1024f * 1024f);

        int activeTiles = streamStats != null ? streamStats.ActiveTiles :
                          (getActiveTiles != null ? getActiveTiles() : -1);
        int queuedLoads = streamStats != null ? streamStats.QueuedLoads :
                          (getQueuedLoads != null ? getQueuedLoads() : -1);
        int loadsThisFrame = streamStats != null ? streamStats.LoadsThisFrame :
                             (getLoadsThisFrame != null ? getLoadsThisFrame() : 0);

        GUILayout.Label($"FPS: {fps:0.0} | Frame: {frameMs:0.0} ms | P99: {p99FrameMs:0.0} ms");
        GUILayout.Label($"RAM Alloc: {allocMb:0.0} MB | Reserved: {reservedMb:0.0} MB | Mono: {monoMb:0.0} MB");

        if (activeTiles >= 0)
            GUILayout.Label($"Tiles Active: {activeTiles} | Queued: {queuedLoads} | Loads/frame: {loadsThisFrame}");

        GUILayout.EndArea();
        GUI.matrix = prevMatrix;
    }

    static float PercentileMs(float[] samples, float p)
    {
        var tmp = new float[samples.Length];
        Array.Copy(samples, tmp, samples.Length);
        Array.Sort(tmp);
        int idx = Mathf.Clamp(Mathf.RoundToInt((tmp.Length - 1) * p), 0, tmp.Length - 1);
        return tmp[idx] * 1000f;
    }
}
