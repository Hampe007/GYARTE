using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using TMPro;

public class SplashLoader : MonoBehaviour
{
[Header("Targets")]
    [SerializeField] private string mainMenuSceneName = "MainMenu";
    [SerializeField] private CanvasGroup canvasGroup;      // Fade in/out for the whole splash UI
    [SerializeField] private Slider progressSlider;        // UI Slider (0..100, Whole Numbers)
    [SerializeField] private TextMeshProUGUI statusLabel;  // Optional funny medieval status text

    [Header("Timing")]
    [SerializeField, Tooltip("Minimum display time before scene activation is allowed.")]
    private float minDisplayTime = 15f;

    [SerializeField, Tooltip("Duration of the final 90%→100% tween before activation.")]
    private float finishTweenDuration = 0.75f;

    [SerializeField, Tooltip("Canvas fade in/out duration.")]
    private float fadeDuration = 0.6f;

    [Header("Progress feel")]
    [SerializeField, Tooltip("Designer curve for how progress should FEEL from 0 to 90% over minDisplayTime (x=0..1 time, y=0..0.9 display).")]
    private AnimationCurve progressCurveTo90 = new AnimationCurve(
        new Keyframe(0.00f, 0.00f, 0, 0),
        new Keyframe(0.20f, 0.08f, 0, 0),
        new Keyframe(0.45f, 0.35f, 0, 0),
        new Keyframe(0.70f, 0.65f, 0, 0),
        new Keyframe(0.90f, 0.85f, 0, 0),
        new Keyframe(1.00f, 0.90f, 0, 0)
    );

    [SerializeField, Tooltip("Max visual percent points per second the bar is allowed to increase.")]
    private float maxPercentPerSecond = 25f;

    [SerializeField, Range(0f, 1f), Tooltip("Exponential smoothing per second (0=rigid, 1=very soft).")]
    private float smoothingPerSecond = 0.35f;

    [SerializeField, Tooltip("How far ahead of true load (normalized) the bar may lead in 0..1 units (0.05=5%).")]
    private float trueHeadroom = 0.05f;

    [SerializeField, Tooltip("Visual cap before the activation gate opens.")]
    private float displayCap = 0.90f; // 90%

    [Header("Misc")]
    [SerializeField] private bool setHighBackgroundLoadingPriority = true;

    // Internal
    private AsyncOperation _opMain;
    private float _display01;   // 0..1 visual progress
    private float _startTime;
    private bool _finishing;

    // Medieval quips
    private static readonly string[] EarlyQuips = 
    {
        "Summoning the court jester…",
        "Sharpening wooden swords (safety first)…",
        "Feeding the royal chickens…",
    };

    private static readonly string[] MidQuips = 
    {
        "Bargaining with goblins over loading fees…",
        "Brewing stamina potions (taste questionable)…",
        "Teaching peasants to spell ‘queue’…",
    };

    private static readonly string[] LateQuips = 
    {
        "Convincing the dragon it’s just a big lizard…",
        "Consulting the wizard (he’s on lunch)…",
        "Testing catapult safety (do not volunteer)…"
    };

    private static readonly string[] GateHoldQuips = 
    {
        "Holding the gate at 90%—no barbarians beyond this point…",
        "The hourglass says ‘not yet’—patience, brave hero…",
        "Royal decree: linger dramatically at 90%…",
    };

    private float _statusSwapTimer;
    private const float StatusSwapInterval = 2.2f; // rotate quips every ~2 seconds while waiting

    private void Awake()
    {
        if (progressSlider != null)
        {
            progressSlider.minValue = 0f;
            progressSlider.maxValue = 100f;
            progressSlider.wholeNumbers = true;
            progressSlider.value = 0f;
        }
        if (canvasGroup != null) canvasGroup.alpha = 0f;
    }

    private void Start()
    {
        if (setHighBackgroundLoadingPriority)
            Application.backgroundLoadingPriority = ThreadPriority.High;

        _startTime = Time.time;
        StartCoroutine(RunSequence());
    }

    private IEnumerator RunSequence()
    {
        // Fade in
        if (canvasGroup != null)
            yield return StartCoroutine(FadeCanvas(canvasGroup, 0f, 1f, fadeDuration));

        // Begin preloading MainMenu additively, keep it from activating
        _opMain = SceneManager.LoadSceneAsync(mainMenuSceneName, LoadSceneMode.Additive);
        _opMain.allowSceneActivation = false;

        while (!_finishing)
        {
            float elapsed = Time.time - _startTime;
            float dt = Time.deltaTime;

            // True load normalized to 0..1 (0.9 means ready to activate)
            float trueLoad01 = 0f;
            if (_opMain != null)
                trueLoad01 = Mathf.Clamp01(_opMain.progress / 0.9f); // normalize to 0..1

            // Design driven feel curve toward 90%, evaluated over minDisplayTime
            float tNorm = Mathf.Clamp01(elapsed / Mathf.Max(minDisplayTime, 0.0001f));
            float curveTarget = Mathf.Clamp01(progressCurveTo90.Evaluate(tNorm)); // should stay <= 0.9 by design

            // True ceiling so we don't get ahead of reality by more than headroom
            float trueCeiling = Mathf.Min(displayCap, trueLoad01 * displayCap + trueHeadroom);

            // Final target between 0..90%: curve, but never exceeding the ceiling
            float targetDisplay = Mathf.Min(curveTarget, trueCeiling);
            targetDisplay = Mathf.Clamp(targetDisplay, 0f, displayCap);

            // Speed limit + smoothing
            float maxStep = (maxPercentPerSecond / 100f) * dt; // in 0..1 units
            float stepped = Mathf.MoveTowards(_display01, targetDisplay, maxStep);
            float lerpA = 1f - Mathf.Exp(-smoothingPerSecond * dt);
            _display01 = Mathf.Lerp(_display01, stepped, lerpA);

            // Update UI
            UpdateSlider(_display01);

            bool gateHolding = _display01 >= displayCap - 0.001f;
            UpdateStatusLabel(trueLoad01, elapsed, gateHolding);

            // Gate: wait for both min time and true load ready
            bool minTimeOK = elapsed >= minDisplayTime;
            bool loadReady = trueLoad01 >= 1f - 1e-3f;

            if (minTimeOK && loadReady)
            {
                StartCoroutine(FinishAndActivate());
                _finishing = true;
                yield break;
            }

            yield return null;
        }
    }

    private IEnumerator FinishAndActivate()
    {
        // Smooth finish 90% → 100%
        float start = _display01;
        float t = 0f;
        while (t < finishTweenDuration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / finishTweenDuration);
            k = k * k * (3f - 2f * k); // ease in-out
            _display01 = Mathf.Lerp(start, 1f, k);
            UpdateSlider(_display01);
            if (statusLabel) statusLabel.text = "Sound the trumpets! Final preparations…";
            yield return null;
        }
        _display01 = 1f;
        UpdateSlider(_display01);

        // Fade out before activation to mask any tiny hitch
        if (canvasGroup != null)
            yield return StartCoroutine(FadeCanvas(canvasGroup, 1f, 0f, fadeDuration));

        // Allow MainMenu to activate
        _opMain.allowSceneActivation = true;

        // Wait until actually done
        while (!_opMain.isDone)
            yield return null;

        // Make MainMenu the active scene
        Scene main = SceneManager.GetSceneByName(mainMenuSceneName);
        if (main.IsValid())
            SceneManager.SetActiveScene(main);

        // Unload this splash scene
        var current = gameObject.scene;
        yield return SceneManager.UnloadSceneAsync(current);

        // Optional clean-up
        yield return Resources.UnloadUnusedAssets();
    }

    private void UpdateSlider(float display01)
    {
        if (progressSlider == null) return;
        int percent = Mathf.Clamp(Mathf.RoundToInt(display01 * 100f), 0, 100);
        progressSlider.value = percent;
    }

 private void UpdateStatusLabel(float trueLoad01, float elapsed, bool gateHolding)
    {
        if (statusLabel == null) return;

        _statusSwapTimer += Time.deltaTime;
        if (_statusSwapTimer < StatusSwapInterval && !gateHolding)
            return;
        _statusSwapTimer = 0f;

        string[] pool;
        if (gateHolding)      pool = GateHoldQuips;
        else if (trueLoad01 < 0.33f) pool = EarlyQuips;
        else if (trueLoad01 < 0.66f) pool = MidQuips;
        else                   pool = LateQuips;

        int idx = Mathf.Abs((int)(elapsed * 7.0f)) % Mathf.Max(1, pool.Length);
        statusLabel.text = pool[idx];
    }

    private static IEnumerator FadeCanvas(CanvasGroup cg, float from, float to, float duration)
    {
        if (cg == null || duration <= 0f)
        {
            if (cg != null) cg.alpha = to;
            yield break;
        }
        float t = 0f;
        cg.alpha = from;
        while (t < duration)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / duration);
            k = k * k * (3f - 2f * k); // smoothstep
            cg.alpha = Mathf.Lerp(from, to, k);
            yield return null;
        }
        cg.alpha = to;
    }
}