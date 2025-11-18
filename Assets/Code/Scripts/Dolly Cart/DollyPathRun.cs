using System;
using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

public class DollyPathRun : MonoBehaviour
{
    // Assign in Inspector
    public SplineContainer splineContainer;     // The rail (first spline used)
    public Transform cartTransform;             // Moved along the rail
    public float totalDuration = 10f;           // Seconds for a full pass
    public bool playOnStart = true;
    public bool loop = false;
    public int samples = 512;                   // LUT resolution for arc-length mapping

    // Read-only state
    public bool IsRunning { get; private set; }
    public float NormalizedTime { get; private set; }  // 0..1 progress

    Spline _spline;
    float[] _cumLengths;     // cumulative length LUT
    float[] _ts;             // corresponding t in [0,1]
    float _totalLength;
    float _t;                // normalized progress 0..1

    void Awake()
    {
        if (splineContainer == null)
        {
            Debug.LogError("DollyPathRun_Splines: Missing SplineContainer.");
            enabled = false;
            return;
        }

        if (splineContainer.Splines.Count == 0)
        {
            Debug.LogError("DollyPathRun_Splines: SplineContainer has no splines.");
            enabled = false;
            return;
        }

        _spline = splineContainer.Splines[0];
        BuildArcLengthLUT();
    }

    void Start()
    {
        // Optional: tighten determinism
        Time.fixedDeltaTime = 1f / 120f;
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = 120;

        if (playOnStart) StartRun();
    }

    public void StartRun()
    {
        _t = 0f;
        NormalizedTime = 0f;
        IsRunning = true;
    }

    public void StopRun()
    {
        IsRunning = false;
    }

    void FixedUpdate()
    {
        if (!IsRunning || totalDuration <= 0f || _totalLength <= 0f || cartTransform == null) return;

        _t += Time.fixedDeltaTime / totalDuration;

        if (_t >= 1f)
        {
            if (loop) _t = Mathf.Repeat(_t, 1f);
            else { _t = 1f; IsRunning = false; }
        }

        NormalizedTime = _t;

        float targetDistance = _t * _totalLength;
        float interp = DistanceToT(targetDistance);

        SplineUtility.Evaluate(_spline, interp, out float3 pos, out float3 tangent, out float3 up);
        Quaternion rot = Quaternion.LookRotation((Vector3)tangent == Vector3.zero ? Vector3.forward : (Vector3)tangent, (Vector3)up);

        cartTransform.SetPositionAndRotation((Vector3)pos, rot);
    }

    void BuildArcLengthLUT()
    {
        samples = Mathf.Max(8, samples);

        _ts = new float[samples + 1];
        _cumLengths = new float[samples + 1];

        SplineUtility.Evaluate(_spline, 0f, out float3 p0, out _, out _);
        _ts[0] = 0f;
        _cumLengths[0] = 0f;

        float cum = 0f;
        float3 prev = p0;

        for (int i = 1; i <= samples; i++)
        {
            float t = (float)i / samples;
            SplineUtility.Evaluate(_spline, t, out float3 p, out _, out _);
            cum += math.length(p - prev);
            _ts[i] = t;
            _cumLengths[i] = cum;
            prev = p;
        }

        _totalLength = cum;
    }

    float DistanceToT(float distance)
    {
        if (distance <= 0f) return 0f;
        if (distance >= _totalLength) return 1f;

        int idx = Array.BinarySearch(_cumLengths, distance);
        if (idx >= 0) return _ts[idx];

        int hi = ~idx;
        int lo = hi - 1;

        float d0 = _cumLengths[lo];
        float d1 = _cumLengths[hi];
        float t0 = _ts[lo];
        float t1 = _ts[hi];

        float f = (distance - d0) / Mathf.Max(1e-6f, d1 - d0);
        return Mathf.Lerp(t0, t1, Mathf.Clamp01(f));
    }
}