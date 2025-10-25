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
    

    bool _deleteOnExit;

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

    // Texture layer toggles
    [SerializeField] private bool enableDirtLayer = true;
    [SerializeField] private bool enableGrassLayer = true;
    [SerializeField] private bool enableSnowLayer = true;

    // Props toggle
    [Tooltip("Rocks, trees etc.")]
    [SerializeField] private bool enableProps = true;
    
    // Toggle keys
    [SerializeField] private KeyCode togglePropsKey = KeyCode.F5;
    [SerializeField] private KeyCode cycleTexturesKey = KeyCode.F6;

    // Cache of original layers
    private TerrainLayer[] _originalLayers;
    private bool _layersCached;
    
    float _sumFps, _sumCpuMs, _sumGpuMs, _sumRam;
    int _sampleCount, _gpuCount;
    
    float _minFps = float.MaxValue, _maxFps = float.MinValue;
    float _minCpu = float.MaxValue, _maxCpu = float.MinValue;
    float _minGpu = float.MaxValue, _maxGpu = float.MinValue;
    float _minRam = float.MaxValue, _maxRam = float.MinValue;

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
            $"<b>[PerfLogger]</b> Mode=<color=yellow>{mode}</color>, " +
            $"TargetFPS=<color=lime>{targetFPS}</color>\n" +
            $"<b>Log file:</b> {_path}{deletionNote}\n" +  // raw path = clickable
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

        if (cycleTexturesKey != KeyCode.None && Input.GetKeyDown(cycleTexturesKey))
        {
            CycleTextureLayerPreset();
            ApplyTextureLayerToggles();
            LogScenarioStamp();
        }

        _t += Time.unscaledDeltaTime;
        if (_t < sampleIntervalSeconds) return;
        _t = 0f;

        float elapsed = Time.time; // seconds since play
        float dt = Time.unscaledDeltaTime;
        float fps = dt > 0f ? 1f / dt : 0f;
        float cpuMs = dt * 1000f;

        FrameTimingManager.CaptureFrameTimings();
        double gpuMs = double.NaN;
        FrameTiming[] frames = new FrameTiming[1];
        if (FrameTimingManager.GetLatestTimings(1, frames) > 0)
            gpuMs = frames[0].gpuFrameTime;

        double ramMB = Profiler.GetTotalAllocatedMemoryLong() / (1024.0 * 1024.0);

        /* Skip first N seconds to avoid startup spikes */
        if (elapsed < discardFirstSeconds)
            return;

        // Accumulate stats only after warmup
        _sumFps += fps;
        _sumCpuMs += cpuMs;
        if (!double.IsNaN(gpuMs))
        {
            _sumGpuMs += (float)gpuMs;
            _gpuCount++;
        }
        _sumRam += (float)ramMB;
        _sampleCount++;

        // Track min/max
        _minFps = Mathf.Min(_minFps, fps);
        _maxFps = Mathf.Max(_maxFps, fps);
        _minCpu = Mathf.Min(_minCpu, cpuMs);
        _maxCpu = Mathf.Max(_maxCpu, cpuMs);
        if (!double.IsNaN(gpuMs))
        {
            _minGpu = Mathf.Min(_minGpu, (float)gpuMs);
            _maxGpu = Mathf.Max(_maxGpu, (float)gpuMs);
        }
        _minRam = Mathf.Min(_minRam, (float)ramMB);
        _maxRam = Mathf.Max(_maxRam, (float)ramMB);

        // Write the CSV row normally
        var inv = CultureInfo.InvariantCulture;
        string row = string.Join(",",
            elapsed.ToString("F1", inv),
            fps.ToString("F0", inv),
            cpuMs.ToString("F1", inv),
            double.IsNaN(gpuMs) ? "" : gpuMs.ToString("F1", inv),
            ramMB.ToString("F1", inv)
        );

        if (mode == LoggingMode.Continuous)
        {
            _writer.WriteLine(row);
            try { _stream.Flush(); } catch {}
        }
        else
        {
            _rows.Add(row);
        }
    }
    
    // Call this from your UI/overlay buttons if you prefer
    public void ApplyGraphicsToggles()
    {
        ApplyTextureLayerToggles();
        ApplyPropsToggle();
    }

    // Explicit setter you can call before starting a benchmark
    public void SetTextureToggles(bool dirt, bool grass, bool snow)
    {
        enableDirtLayer = dirt;
        enableGrassLayer = grass;
        enableSnowLayer = snow;
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
        if (_layersCached) return;
        if (targetTerrain != null && targetTerrain.terrainData != null)
        {
            var layers = targetTerrain.terrainData.terrainLayers;
            if (layers != null && layers.Length > 0)
            {
                _originalLayers = (TerrainLayer[])layers.Clone();
                _layersCached = true;
            }
        }
    }

    private void ApplyTextureLayerToggles()
    {
        if (targetTerrain == null || targetTerrain.terrainData == null) return;

        CacheOriginalTerrainLayers();
        if (!_layersCached || _originalLayers == null) return;

        // Build a filtered list based on layer names; non-matched layers are excluded
        var list = new System.Collections.Generic.List<TerrainLayer>();
        for (int i = 0; i < _originalLayers.Length; i++)
        {
            var layer = _originalLayers[i];
            if (layer == null) continue;
            var name = layer.name ?? string.Empty;

            bool isDirt = name.IndexOf("dirt", System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool isGrass = name.IndexOf("grass", System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool isSnow = name.IndexOf("snow", System.StringComparison.OrdinalIgnoreCase) >= 0;

            if ((isDirt && enableDirtLayer) ||
                (isGrass && enableGrassLayer) ||
                (isSnow && enableSnowLayer))
            {
                list.Add(layer);
            }
        }

        // If nothing matched, assign an empty array to simulate "no painted layers"
        targetTerrain.terrainData.terrainLayers = list.ToArray();

        // Force a refresh so the change is visible immediately
        targetTerrain.Flush();
    }

    // Simple preset cycler for testing via hotkey
    private void CycleTextureLayerPreset()
    {
        if (enableDirtLayer && enableGrassLayer && enableSnowLayer)
        {
            enableDirtLayer = true;  enableGrassLayer = false; enableSnowLayer = false;
            return;
        }
        if (enableDirtLayer && !enableGrassLayer && !enableSnowLayer)
        {
            enableDirtLayer = false; enableGrassLayer = true;  enableSnowLayer = false;
            return;
        }
        if (!enableDirtLayer && enableGrassLayer && !enableSnowLayer)
        {
            enableDirtLayer = false; enableGrassLayer = false; enableSnowLayer = true;
            return;
        }
        if (!enableDirtLayer && !enableGrassLayer && enableSnowLayer)
        {
            enableDirtLayer = false; enableGrassLayer = false; enableSnowLayer = false;
            return;
        }
        enableDirtLayer = true; enableGrassLayer = true; enableSnowLayer = true;
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
            _deleteOnExit = true;
    }

    void OnDestroy()
    {
        try
        {
            // If EndOnly, dump buffered rows first
            if (mode == LoggingMode.EndOnly && _buffer != null)
            {
                foreach (var r in _rows) _buffer.AppendLine(r);
                File.WriteAllText(_path, _buffer.ToString(), new UTF8Encoding(false));
            }

            _writer?.Flush();
            _stream?.Flush();

            if (_sampleCount > 0)
            {
                var inv = CultureInfo.InvariantCulture;

                float avgFps = _sumFps / _sampleCount;
                float avgCpu = _sumCpuMs / _sampleCount;
                string avgGpuStr = _gpuCount > 0 ? (_sumGpuMs / _gpuCount).ToString("F1", inv) : "";
                float avgRam = _sumRam / _sampleCount;

                string avgLine = string.Join(",",
                    "AVERAGE",
                    avgFps.ToString("F1", inv),
                    avgCpu.ToString("F1", inv),
                    avgGpuStr,
                    avgRam.ToString("F1", inv)
                );

                string minLine = string.Join(",",
                    "MIN",
                    _minFps.ToString("F1", inv),
                    _minCpu.ToString("F1", inv),
                    _gpuCount > 0 ? _minGpu.ToString("F1", inv) : "",
                    _minRam.ToString("F1", inv)
                );

                string maxLine = string.Join(",",
                    "MAX",
                    _maxFps.ToString("F1", inv),
                    _maxCpu.ToString("F1", inv),
                    _gpuCount > 0 ? _maxGpu.ToString("F1", inv) : "",
                    _maxRam.ToString("F1", inv)
                );

                if (mode == LoggingMode.Continuous)
                {
                    _writer?.WriteLine(avgLine);
                    _writer?.WriteLine(minLine);
                    _writer?.WriteLine(maxLine);
                    try { _stream?.Flush(); } catch {}
                }
                else
                {
                    if (File.Exists(_path))
                        File.AppendAllText(_path, "\n" + avgLine + "\n" + minLine + "\n" + maxLine);
                }
            }
        }
        catch { }
        finally
        {
            // Close handles before deletion
            try { _writer?.Dispose(); } catch {}
            try { _stream?.Dispose(); } catch {}

            // Delete temp logs after everything is written and closed
            try
            {
                if (scenarioTag.Equals("temp", StringComparison.OrdinalIgnoreCase) && File.Exists(_path))
                {
                    File.Delete(_path);
                    Debug.Log($"[PerfLogger] Deleted temp log: {_path}");
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
        float cpuMs = dt * 1000f;

        FrameTimingManager.CaptureFrameTimings();
        double gpuMs = double.NaN;
        FrameTiming[] frames = new FrameTiming[1];
        if (FrameTimingManager.GetLatestTimings(1, frames) > 0)
            gpuMs = frames[0].gpuFrameTime;

        double ramMB = Profiler.GetTotalAllocatedMemoryLong() / (1024.0 * 1024.0);

        // Accumulate for averages
        _sumFps += fps;
        _sumCpuMs += cpuMs;
        if (!double.IsNaN(gpuMs))
        {
            _sumGpuMs += (float)gpuMs;
            _gpuCount++;
        }
        _sumRam += (float)ramMB;
        _sampleCount++;

        // Track min/max
        _minFps = Mathf.Min(_minFps, fps);
        _maxFps = Mathf.Max(_maxFps, fps);
        _minCpu = Mathf.Min(_minCpu, cpuMs);
        _maxCpu = Mathf.Max(_maxCpu, cpuMs);
        if (!double.IsNaN(gpuMs))
        {
            _minGpu = Mathf.Min(_minGpu, (float)gpuMs);
            _maxGpu = Mathf.Max(_maxGpu, (float)gpuMs);
        }
        _minRam = Mathf.Min(_minRam, (float)ramMB);
        _maxRam = Mathf.Max(_maxRam, (float)ramMB);

        // Compose CSV row
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