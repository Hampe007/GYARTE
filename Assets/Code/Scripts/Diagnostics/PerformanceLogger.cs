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

    [SerializeField] float discardFirstSeconds = 5f;
    
    [SerializeField, Tooltip("Adds estimated utilization percentages based on frame budget (1000/target FPS). These are not hardware counters.")]
    bool includeEstimatedUtilization = true;
    
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
    

    bool deleteOnExit;

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
    
    // Graphics toggles
    [Header("Diagnostics · Scenario Toggles")]
    [SerializeField] private Terrain targetTerrain;
    [SerializeField] private GameObject[] propGroups;

    [SerializeField] private bool enableTerrainTextures = true;
    
    // Props toggle
    [Tooltip("Rocks, trees etc.")]
    [SerializeField] private bool enableProps = true;
    
    // Toggle keys
    [SerializeField] private KeyCode togglePropsKey = KeyCode.F5;

    // Cache of original layers
    private TerrainLayer[] originalLayers;
    private bool layersCached;
    
    float sumFps, sumCpuMs, sumGpuMs, sumRam;
    int sampleCount, gpuCount;
    
    float minFps = float.MaxValue, maxFps = float.MinValue;
    float minCpu = float.MaxValue, maxCpu = float.MinValue;
    float minGpu = float.MaxValue, maxGpu = float.MinValue;
    float minRam = float.MaxValue, maxRam = float.MinValue;

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
        Debug.Log($"[PerfLogger] Applying FPS cap {(int)targetFPS}");

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
    
    string path;
    FileStream stream;
    StreamWriter writer;
    float t;

    StringBuilder buffer;
    List<string> rows;
    
    float FrameBudgetMs
    {
        get
        {
            int fpsCap = (int)targetFPS;
            if (fpsCap > 0)
                return 1000f / fpsCap;

            // Unlimited mode: use recent frame time as a moving budget fallback.
            return Mathf.Max(Time.unscaledDeltaTime * 1000f, 0.01f);
        }
    }

    void Awake()
    {
        // Apply chosen target FPS and disable vSync so it takes effect
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = (int)targetFPS;
        ApplyTargetFps();
    }

    void Start()
    {
        DeleteOldTempFiles();
        
        CacheOriginalTerrainLayers();
        ApplyGraphicsToggles();
        LogScenarioStamp(); // implement to write a row/field with the current scenario flags
        
        // Build full path under persistentDataPath/Logger/
        string logDir = Path.Combine(Application.persistentDataPath, "Logger");
        if (!Directory.Exists(logDir))
            Directory.CreateDirectory(logDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var fileName = $"perf_{scenarioTag}_{stamp}.csv";
        path = Path.Combine(logDir, fileName);

        // CSV header
        string header = $"# Scenario: {scenarioTag}\n" +
                        "time_s,sample_idx,fps,frame_ms,cpu_ms,gpu_ms,cpu_est_util_pct,gpu_est_util_pct,ram_mb";

        if (mode == LoggingMode.Continuous)
        {
            // Open a live-writable stream; allow read sharing to inspect mid-run
            var options =
                
            #if UNITY_WEBGL
            FileOptions.None;
            #else
            (writeThroughOnDesktop ? FileOptions.WriteThrough : FileOptions.None);
            #endif
            
            stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, options);
            writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true);
            writer.NewLine = "\n";
            writer.AutoFlush = true;
            writer.WriteLine(header);
            stream.Flush();
        }
        else
        {
            // EndOnly: no file I/O during play; buffer rows and dump once
            buffer = new StringBuilder(1024);
            rows = new List<string>(512);
            buffer.AppendLine(header);
        }

        // Warm up GPU timings
        FrameTimingManager.CaptureFrameTimings();

        string deletionNote = scenarioTag.Equals("temp", StringComparison.OrdinalIgnoreCase)
            ? " <b><color=red>(will be deleted on quit)</color></b>"
            : "";

        Debug.Log(
            $"<b>[PerfLogger]</b> Mode=<color=yellow>{mode}</color>, " +
            $"TargetFPS=<color=lime>{targetFPS}</color>\n" +
            $"<b>Log file:</b> {path}{deletionNote}\n" +  // raw path = clickable
            $"<b>Log folder:</b> <color=#88CCFF>{logDir}</color>"
        );
    }

    void Update()
    {
        if (togglePropsKey != KeyCode.None && Input.GetKeyDown(togglePropsKey))
        {
            enableProps = !enableProps;
            ApplyPropsToggle();
            LogScenarioStamp();
        }

        t += Time.unscaledDeltaTime;
        if (t < sampleIntervalSeconds) return;
        t = 0f;

        float elapsed = Time.time; // seconds since play
        float dt = Time.unscaledDeltaTime;
        float fps = dt > 0f ? 1f / dt : 0f;
        float frameMs = dt * 1000f;
        float cpuMs = dt * 1000f;

        FrameTimingManager.CaptureFrameTimings();
        double gpuMs = double.NaN;
        FrameTiming[] frames = new FrameTiming[1];
        if (FrameTimingManager.GetLatestTimings(1, frames) > 0)
            gpuMs = frames[0].gpuFrameTime;

        double ramMB = Profiler.GetTotalAllocatedMemoryLong() / (1024.0 * 1024.0);
        float frameBudgetMs = FrameBudgetMs;
        string cpuUtilPct = includeEstimatedUtilization ? FormatUtilPct(cpuMs, frameBudgetMs) : "";
        string gpuUtilPct = includeEstimatedUtilization ? FormatUtilPct((float)gpuMs, frameBudgetMs) : "";

        /* Skip first N seconds to avoid startup spikes */
        if (elapsed < discardFirstSeconds)
            return;

        // Accumulate stats only after warmup
        sumFps += fps;
        sumCpuMs += cpuMs;
        if (!double.IsNaN(gpuMs))
        {
            sumGpuMs += (float)gpuMs;
            gpuCount++;
        }
        sumRam += (float)ramMB;
        sampleCount++;

        // Track min/max
        minFps = Mathf.Min(minFps, fps);
        maxFps = Mathf.Max(maxFps, fps);
        minCpu = Mathf.Min(minCpu, cpuMs);
        maxCpu = Mathf.Max(maxCpu, cpuMs);
        if (!double.IsNaN(gpuMs))
        {
            minGpu = Mathf.Min(minGpu, (float)gpuMs);
            maxGpu = Mathf.Max(maxGpu, (float)gpuMs);
        }
        minRam = Mathf.Min(minRam, (float)ramMB);
        maxRam = Mathf.Max(maxRam, (float)ramMB);

        // Write the CSV row normally
        var inv = CultureInfo.InvariantCulture;
        string row = string.Join(",",
            elapsed.ToString("F1", inv),
            sampleCount.ToString(inv),
            fps.ToString("F0", inv),
            frameMs.ToString("F2", inv),
            cpuMs.ToString("F1", inv),
            double.IsNaN(gpuMs) ? "" : gpuMs.ToString("F1", inv),
            cpuUtilPct,
            gpuUtilPct,
            ramMB.ToString("F1", inv)
        );

        if (mode == LoggingMode.Continuous)
        {
            writer.WriteLine(row);
            try { stream.Flush(); } catch {}
        }
        else
        {
            rows.Add(row);
        }
    }
    
    // Call this from your UI/overlay buttons if you prefer
    public void ApplyGraphicsToggles()
    {
        ApplyTextureLayerToggles();
        ApplyPropsToggle();
    }

    // Explicit setter you can call before starting a benchmark
    public void SetTextureToggle(bool enable)
    {
        enableTerrainTextures = enable;
        ApplyTextureLayerToggles();
    }

    public void SetPropsToggle(bool value)
    {
        enableProps = value;
        ApplyPropsToggle();
    }

    private void ApplyPropsToggle()
    {
        if (propGroups == null) return;
        for (int i = 0; i < propGroups.Length; i++)
        {
            if (propGroups[i] != null) propGroups[i].SetActive(enableProps);
        }
    }

    // Terrain texture layer handling
    private void CacheOriginalTerrainLayers()
    {
        if (layersCached) return;
        if (targetTerrain != null && targetTerrain.terrainData != null)
        {
            var layers = targetTerrain.terrainData.terrainLayers;
            if (layers != null && layers.Length > 0)
            {
                originalLayers = (TerrainLayer[])layers.Clone();
                layersCached = true;
            }
        }
    }

    private void ApplyTextureLayerToggles()
    {
        if (targetTerrain == null || targetTerrain.terrainData == null)
            return;

        CacheOriginalTerrainLayers();
        if (!layersCached || originalLayers == null)
            return;

        if (enableTerrainTextures)
        {
            // Restore all original terrains
            targetTerrain.terrainData.terrainLayers = (TerrainLayer[])originalLayers.Clone();
        }
        else
        {
            // Disable all layers
            targetTerrain.terrainData.terrainLayers = new TerrainLayer[0];
        }

        targetTerrain.Flush();
    }

    // Emits a single compact tag so your CSV can group results by scenario
    private void LogScenarioStamp()
    {
        // Example: call your existing logging API
        // WriteMeta("Scenario", $"TL[D:{Bool01(enableDirtLayer)} G:{Bool01(enableGrassLayer)} S:{Bool01(enableSnowLayer)}] · Props:{Bool01(enableProps)}");
    }
    
    void OnApplicationQuit()
    {
        // Ensure a final sample is taken right before exit
        ForceOneSample();
        
        // mark for deletion on quit if temp
        if (scenarioTag.Equals("temp", StringComparison.OrdinalIgnoreCase))
            deleteOnExit = true;
    }

    void OnDestroy()
    {
        try
        {
            // If EndOnly, dump buffered rows first
            if (mode == LoggingMode.EndOnly && buffer != null)
            {
                foreach (var r in rows) buffer.AppendLine(r);
                File.WriteAllText(path, buffer.ToString(), new UTF8Encoding(false));
            }

            writer?.Flush();
            stream?.Flush();

            if (sampleCount > 0)
            {
                var inv = CultureInfo.InvariantCulture;

                float avgFps = sumFps / sampleCount;
                float avgCpu = sumCpuMs / sampleCount;
                string avgGpuStr = gpuCount > 0 ? (sumGpuMs / gpuCount).ToString("F1", inv) : "";
                float avgRam = sumRam / sampleCount;

                string avgLine = string.Join(",",
                    "AVERAGE",
                    "",
                    avgFps.ToString("F1", inv),
                    (sumCpuMs / sampleCount).ToString("F2", inv),
                    avgCpu.ToString("F1", inv),
                    avgGpuStr,
                    includeEstimatedUtilization ? FormatUtilPct(avgCpu, FrameBudgetMs) : "",
                    includeEstimatedUtilization && gpuCount > 0 ? FormatUtilPct(sumGpuMs / gpuCount, FrameBudgetMs) : "",
                    avgRam.ToString("F1", inv)
                );

                string minLine = string.Join(",",
                    "MIN",
                    "",
                    minFps.ToString("F1", inv),
                    minCpu.ToString("F1", inv),
                    minCpu.ToString("F1", inv),
                    gpuCount > 0 ? minGpu.ToString("F1", inv) : "",
                    includeEstimatedUtilization ? FormatUtilPct(minCpu, FrameBudgetMs) : "",
                    includeEstimatedUtilization && gpuCount > 0 ? FormatUtilPct(minGpu, FrameBudgetMs) : "",
                    minRam.ToString("F1", inv)
                );

                string maxLine = string.Join(",",
                    "MAX",
                    "",
                    maxFps.ToString("F1", inv),
                    maxCpu.ToString("F1", inv),
                    maxCpu.ToString("F1", inv),
                    gpuCount > 0 ? maxGpu.ToString("F1", inv) : "",
                    includeEstimatedUtilization ? FormatUtilPct(maxCpu, FrameBudgetMs) : "",
                    includeEstimatedUtilization && gpuCount > 0 ? FormatUtilPct(maxGpu, FrameBudgetMs) : "",
                    maxRam.ToString("F1", inv)
                );

                if (mode == LoggingMode.Continuous)
                {
                    writer?.WriteLine(avgLine);
                    writer?.WriteLine(minLine);
                    writer?.WriteLine(maxLine);
                    try { stream?.Flush(); } catch {}
                }
                else
                {
                    if (File.Exists(path))
                        File.AppendAllText(path, "\n" + avgLine + "\n" + minLine + "\n" + maxLine);
                }
            }
        }
        catch { }
        finally
        {
            // Close handles before deletion
            try { writer?.Dispose(); } catch {}
            try { stream?.Dispose(); } catch {}

            // Delete temp logs after everything is written and closed
            try
            {
                if (scenarioTag.Equals("temp", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    File.Delete(path);
                    Debug.Log($"[PerfLogger] Deleted temp log: {path}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[PerfLogger] Failed to delete temp log: {ex.Message}");
            }
        }
    }
    
    void DeleteOldTempFiles()
    {
        try
        {
            string dir = Path.Combine(Application.persistentDataPath, "Logger");
            if (!Directory.Exists(dir)) return;

            foreach (var file in Directory.GetFiles(dir, "perf_temp_*.csv"))
            {
                try
                {
                    File.Delete(file);
                    Debug.Log($"[PerfLogger] Deleted old temp log: {file}");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[PerfLogger] Failed to delete old temp log: {ex.Message}");
                }
            }
        }
        catch { }
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
            int safeFps = ResolveFallbackFps(targetFPS);
            dt = 1f / Mathf.Max(1, safeFps);
        }

        float fps = dt > 0f ? 1f / dt : 0f;
        float frameMs = dt * 1000f;
        float cpuMs = dt * 1000f;

        FrameTimingManager.CaptureFrameTimings();
        double gpuMs = double.NaN;
        FrameTiming[] frames = new FrameTiming[1];
        if (FrameTimingManager.GetLatestTimings(1, frames) > 0)
            gpuMs = frames[0].gpuFrameTime;

        double ramMB = Profiler.GetTotalAllocatedMemoryLong() / (1024.0 * 1024.0);
        float frameBudgetMs = FrameBudgetMs;
        string cpuUtilPct = includeEstimatedUtilization ? FormatUtilPct(cpuMs, frameBudgetMs) : "";
        string gpuUtilPct = includeEstimatedUtilization ? FormatUtilPct((float)gpuMs, frameBudgetMs) : "";

        // Accumulate for averages
        sumFps += fps;
        sumCpuMs += cpuMs;
        if (!double.IsNaN(gpuMs))
        {
            sumGpuMs += (float)gpuMs;
            gpuCount++;
        }
        sumRam += (float)ramMB;
        sampleCount++;

        // Track min/max
        minFps = Mathf.Min(minFps, fps);
        maxFps = Mathf.Max(maxFps, fps);
        minCpu = Mathf.Min(minCpu, cpuMs);
        maxCpu = Mathf.Max(maxCpu, cpuMs);
        if (!double.IsNaN(gpuMs))
        {
            minGpu = Mathf.Min(minGpu, (float)gpuMs);
            maxGpu = Mathf.Max(maxGpu, (float)gpuMs);
        }
        minRam = Mathf.Min(minRam, (float)ramMB);
        maxRam = Mathf.Max(maxRam, (float)ramMB);

        // Compose CSV row
        var inv = CultureInfo.InvariantCulture;
        string row = string.Join(",",
            Time.time.ToString("F1", inv),
            sampleCount.ToString(inv),
            fps.ToString("F0", inv),
            frameMs.ToString("F2", inv),
            cpuMs.ToString("F1", inv),
            double.IsNaN(gpuMs) ? "" : gpuMs.ToString("F1", inv),
            cpuUtilPct,
            gpuUtilPct,
            ramMB.ToString("F1", inv)
        );

        if (mode == LoggingMode.Continuous)
        {
            writer?.WriteLine(row);
            try { stream?.Flush(); } catch {}
        }
        else
        {
            rows?.Add(row);
        }
    }
    
    static string FormatUtilPct(float ms, float frameBudgetMs)
    {
        if (float.IsNaN(ms) || float.IsInfinity(ms) || ms < 0f || frameBudgetMs <= 0f)
            return "";

        float pct = Mathf.Clamp((ms / frameBudgetMs) * 100f, 0f, 999f);
        return pct.ToString("F1", CultureInfo.InvariantCulture);
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