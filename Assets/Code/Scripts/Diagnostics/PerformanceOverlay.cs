// Fixed overlay in the top-left corner, non-draggable.

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
    private Vector2 position = new Vector2(0, 0);
    public bool visible = true;
    public float scale = 3f;

    private float _emaDt = 0.0167f;
    private const float EmaAlpha = 0.1f;
    private const int HistSize = 600;
    private readonly float[] _dtHist = new float[HistSize];
    private int _histIndex;
    private float _p99FrameMs;

    public IStreamStats streamStats;
    public Func<int> getActiveTiles;
    public Func<int> getQueuedLoads;
    public Func<int> getLoadsThisFrame;

    void Awake()
    {
        for (int i = 0; i < _dtHist.Length; i++) _dtHist[i] = _emaDt;
        if (streamStats == null)
        {
            foreach (var behaviour in FindObjectsOfType<MonoBehaviour>(true))
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

        _emaDt = Mathf.Lerp(_emaDt, Time.unscaledDeltaTime, EmaAlpha);
        _dtHist[_histIndex] = Time.unscaledDeltaTime;
        _histIndex = (_histIndex + 1) % HistSize;

        if (Time.frameCount % 15 == 0)
            _p99FrameMs = PercentileMs(_dtHist, 0.99f);
    }

    void OnGUI()
    {
        if (!visible) return;

        var prevMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

        var rect = new Rect(position.x, position.y, 380, 100);
        GUILayout.BeginArea(rect, GUI.skin.box);

        float fps = 1f / Mathf.Max(_emaDt, 1e-5f);
        float frameMs = _emaDt * 1000f;

        long monoUsed = Profiler.GetMonoUsedSizeLong();
        long totalAlloc = Profiler.GetTotalAllocatedMemoryLong();
        long totalReserved = Profiler.GetTotalReservedMemoryLong();

        float monoMB = monoUsed / (1024f * 1024f);
        float allocMB = totalAlloc / (1024f * 1024f);
        float reservedMB = totalReserved / (1024f * 1024f);

        int activeTiles = streamStats != null ? streamStats.ActiveTiles :
                          (getActiveTiles != null ? getActiveTiles() : -1);
        int queuedLoads = streamStats != null ? streamStats.QueuedLoads :
                          (getQueuedLoads != null ? getQueuedLoads() : -1);
        int loadsThisFrame = streamStats != null ? streamStats.LoadsThisFrame :
                             (getLoadsThisFrame != null ? getLoadsThisFrame() : 0);

        GUILayout.Label($"FPS: {fps:0.0} | Frame: {frameMs:0.0} ms | P99: {_p99FrameMs:0.0} ms");
        GUILayout.Label($"RAM Alloc: {allocMB:0.0} MB | Reserved: {reservedMB:0.0} MB | Mono: {monoMB:0.0} MB");

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