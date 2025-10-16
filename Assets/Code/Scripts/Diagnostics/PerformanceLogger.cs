using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;

public class PerformanceLogger : MonoBehaviour
{
    public enum LoggingMode { Continuous, EndOnly }

    [Header("Mode")]
    [SerializeField, Tooltip("Choose how data is written.\n- Continuous: write+flush every sample (crash-resilient; readable mid-run).\n- EndOnly: buffer in memory and write once on exit (zero runtime I/O).")]
    LoggingMode mode = LoggingMode.Continuous;

    [SerializeField, Tooltip("Desktop only. Forces OS to flush writes (WriteThrough). Improves crash durability at small I/O cost. Disable if hunting microstutter.")]
    bool writeThroughOnDesktop = true;

    [Header("Sampling")]
    [SerializeField, Min(0.05f), Tooltip("Seconds between samples. 1 is a good default.\nLower = finer detail, larger files, more I/O.")]
    float sampleIntervalSeconds = 1f;

    [SerializeField, Tooltip(
         "Label written in each CSV row to identify the test run.\n" +
         "Purely informational — does not affect performance behavior.\n\n" +
         "Examples:\n" +
         "  • baseline — default scene, no streaming\n" +
         "  • streaming_250m — terrain streaming test (250m range)\n" +
         "  • ai_enabled — with NPC logic active\n" +
         "  • lod_off — LODs disabled for stress testing\n" +
         "  • hdrp_high — running on High quality preset\n\n" +
         "  • temp — CSV file will automatically be deleted when unity quits\n\n" +
         "Tip: use simple lowercase tags with underscores so CSVs merge cleanly.")]
    string scenarioTag = "run";


    [Header("Performance Target")]
    [SerializeField, Tooltip("Sets Application.targetFrameRate in Awake. vSync is disabled so this cap applies.")]
    FpsCap targetFPS = FpsCap._60;

    public enum FpsCap
    {
        [InspectorName("30")] _30 = 30,
        [InspectorName("60")] _60 = 60,
        [InspectorName("90")] _90 = 90,
        [InspectorName("120")] _120 = 120,
        [InspectorName("144")] _144 = 144,
        [InspectorName("165")] _165 = 165,
        [InspectorName("240")] _240 = 240,
        [InspectorName("Unlimited")] Unlimited = -1
    }

#if UNITY_EDITOR
    void OnValidate()
    {
        if (Application.isPlaying)
            Application.targetFrameRate = (int)targetFPS;
    }
#endif
    
    void ApplyTargetFps()
    {
        QualitySettings.vSyncCount = 0; // ensure vSync doesn’t override the cap
        Application.targetFrameRate = (int)targetFPS;

        // Small delay for accuracy (in case Unity doesn’t apply instantly)
        StartCoroutine(VerifyFpsAfterDelay());
    }
    
    IEnumerator VerifyFpsAfterDelay()
    {
        yield return new WaitForSecondsRealtime(0.25f); // wait a bit for timing to settle

        int appliedFps = Application.targetFrameRate;
        if (appliedFps == (int)targetFPS)
        {
            Debug.Log($"[PerfLogger] Target FPS applied successfully: {appliedFps} (vSync off)");
        }
        else
        {
            Debug.LogWarning($"[PerfLogger] Target FPS mismatch: requested {(int)targetFPS}, got {appliedFps}. " +
                             "Check QualitySettings.vSyncCount or platform limitations.");
        }
    }
    
    string _path;
    FileStream _stream;
    StreamWriter _writer;
    float _t;

    StringBuilder _buffer;
    List<string> _rows;

    void Awake()
    {
        // Apply chosen target FPS and disable vSync so it takes effect
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = (int)targetFPS;
        ApplyTargetFps();
    }

    void Start()
    {
        // Build full path under persistentDataPath/Logger/
        string logDir = Path.Combine(Application.persistentDataPath, "Logger");
        if (!Directory.Exists(logDir))
            Directory.CreateDirectory(logDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = $"perf_{scenarioTag}_{stamp}.csv";
        _path = Path.Combine(logDir, fileName);

        // CSV header
        string header = $"# Scenario: {scenarioTag}\n" + 
                        "time_s,fps,cpu_ms,gpu_ms,ram_mb";

        if (mode == LoggingMode.Continuous)
        {
            // Open a live-writable stream; allow read sharing to inspect mid-run
            var options =
                
            #if UNITY_WEBGL
            FileOptions.None;
            #else
            (writeThroughOnDesktop ? FileOptions.WriteThrough : FileOptions.None);
            #endif
            
            _stream = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, options);
            _writer = new StreamWriter(_stream, new UTF8Encoding(false), 1024, leaveOpen: true);
            _writer.NewLine = "\n";
            _writer.AutoFlush = true;
            _writer.WriteLine(header);
            _stream.Flush();
        }
        else
        {
            // EndOnly: no file I/O during play; buffer rows and dump once
            _buffer = new StringBuilder(1024);
            _rows = new List<string>(512);
            _buffer.AppendLine(header);
        }

        // Warm up GPU timings
        FrameTimingManager.CaptureFrameTimings();

        string deletionNote = scenarioTag.Equals("temp", StringComparison.OrdinalIgnoreCase)
            ? " <b><color=red>(will be deleted on quit)</color></b>"
            : "";

        Debug.Log(
            $"<b>[PerfLogger]</b> Mode={mode}, TargetFPS={targetFPS}\n" +
            $"<b>Log file:</b> <color=#88CCFF>{_path}</color>{deletionNote}\n" +
            $"<b>Open log folder:</b> <color=#88CCFF>{logDir}</color>"
        );
    }

    void Update()
    {
        _t += Time.unscaledDeltaTime;
        if (_t < sampleIntervalSeconds) return;
        _t = 0f;

        // FPS and CPU frame time
        float dt = Time.unscaledDeltaTime;
        float fps = dt > 0f ? 1f / dt : 0f;
        float cpuMs = dt * 1000f;

        // GPU frame time (best-effort)
        FrameTimingManager.CaptureFrameTimings();
        double gpuMs = double.NaN;
        FrameTiming[] frames = new FrameTiming[1];
        if (FrameTimingManager.GetLatestTimings(1, frames) > 0)
            gpuMs = frames[0].gpuFrameTime;

        // RAM in MB (Unity-tracked)
        double ramMB = Profiler.GetTotalAllocatedMemoryLong() / (1024.0 * 1024.0);

        // Compose CSV row
        // "Fn" is how mau decimals to write
        var inv = CultureInfo.InvariantCulture;
        string row = string.Join(",",
            Time.time.ToString("F1", inv),
            fps.ToString("F0", inv),
            cpuMs.ToString("F1", inv),
            double.IsNaN(gpuMs) ? "" : gpuMs.ToString("F1", inv),
            ramMB.ToString("F1", inv)
        );

        if (mode == LoggingMode.Continuous)
        {
            _writer.WriteLine(row);
            try { _stream.Flush(); } catch { /* ignore transient I/O errors */ }
        }
        else
        {
            _rows.Add(row);
        }
    }
    
    void OnApplicationQuit()
    {
        // Ensure a final sample is taken right before exit
        ForceOneSample();
    }

    void OnDestroy()
    {
        try
        {
            if (mode == LoggingMode.EndOnly && _buffer != null)
            {
                foreach (var r in _rows) _buffer.AppendLine(r);
                File.WriteAllText(_path, _buffer.ToString(), new UTF8Encoding(false));
            }

            _writer?.Flush();
            _stream?.Flush();
        }
        catch { }
        finally
        {
            try { _writer?.Dispose(); } catch {}
            try { _stream?.Dispose(); } catch {}
        }
    }

    static int ResolveFallbackFps(FpsCap cap)
    {
        int v = (int)cap;
        return v <= 0 ? 60 : v; // pick a sane default when Unlimited(-1)
    }
    
    // Force a sample immediately (used on quit)
    void ForceOneSample()
    {
        float dt = Time.unscaledDeltaTime;
        if (dt <= 0f)
        {
            int safeFps = ResolveFallbackFps(targetFPS); // avoid -1
            dt = 1f / Mathf.Max(1, safeFps);
        }

        float fps = dt > 0f ? 1f / dt : 0f;
        float cpuMs = dt * 1000f;

        FrameTimingManager.CaptureFrameTimings();
        double gpuMs = double.NaN;
        FrameTiming[] frames = new FrameTiming[1];
        if (FrameTimingManager.GetLatestTimings(1, frames) > 0)
            gpuMs = frames[0].gpuFrameTime;

        double ramMB = Profiler.GetTotalAllocatedMemoryLong() / (1024.0 * 1024.0);

        var inv = CultureInfo.InvariantCulture;
        string row = string.Join(",",
            Time.time.ToString("F1", inv),
            fps.ToString("F0", inv),
            cpuMs.ToString("F1", inv),
            double.IsNaN(gpuMs) ? "" : gpuMs.ToString("F1", inv),
            ramMB.ToString("F1", inv)
        );

        if (mode == LoggingMode.Continuous)
        {
            _writer?.WriteLine(row);
            try { _stream?.Flush(); } catch {}
        }
        else
        {
            _rows?.Add(row);
        }
    }
}

#if UNITY_EDITOR
public static class LoggerFolderOpener
{
    [MenuItem("Window/Open Logger Folder")]
    public static void OpenLoggerFolder()
    {
        string logDir = Path.Combine(Application.persistentDataPath, "Logger");

        if (!Directory.Exists(logDir))
        {
            EditorUtility.DisplayDialog("Logger Folder", "No Logger folder found yet.", "OK");
            return;
        }

        EditorUtility.RevealInFinder(logDir);
    }
}
#endif