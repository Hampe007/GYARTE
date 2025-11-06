using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using UnityEngine.UI;
using UnityEngine.InputSystem;

public class SplashLoader : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] CanvasGroup canvasGroup;   // CanvasGroup på Canvas (fadar hela UI:t)
    [SerializeField] Slider progressBar;        // Alternativ 1: Unity UI Slider (0..1)
    [SerializeField] Image progressFill;        // Alternativ 2: Image med Type=Filled

    [Header("Flow")]
    [SerializeField] string nextScene = "MainMenu";
    [SerializeField] float minReadTime = 5f;    // min tid att visa splash
    [SerializeField] float fadeIn = 0.35f;
    [SerializeField] float fadeOut = 0.35f;

    [Header("Progress Smoothing")]
    [SerializeField] float smoothTime = 0.2f;   // mjuk visning av progress
    [SerializeField] bool waitFor100 = true;    // kräver visuell 100% före byte

    AsyncOperation loadOp;
    float shown;    // visad progress 0..1
    float vel;      // smoothDamp

    void Reset()
    {
        canvasGroup = FindFirstObjectByType<CanvasGroup>();
        if (!progressBar && !progressFill) progressBar = FindFirstObjectByType<Slider>();
    }

    void Awake()
    {
        Debug.Log("SplashLoader: Loading scene '" + nextScene + "'");
        if (!canvasGroup) canvasGroup = FindFirstObjectByType<CanvasGroup>();

        // Lås input helt (valfritt, nya Input System)
        // if (InputSystem.settings != null) InputSystem.DisableAllEnabledActions();

        // Init UI
        canvasGroup.alpha = 0f;
        SetProgressImmediate(0f);
        if (progressBar) { progressBar.minValue = 0f; progressBar.maxValue = 1f; progressBar.interactable = false; }

        // Ladda nästa scen i bakgrunden (utan att aktivera den)
        loadOp = SceneManager.LoadSceneAsync(nextScene);
        loadOp.allowSceneActivation = false;

        StartCoroutine(Run());
    }

    IEnumerator Run()
    {
        Debug.Log("SplashLoader: Starting splash sequence");
        // Fade in
        yield return Fade(0f, 1f, fadeIn);

        float t = 0f;
        while (true)
        {
            // Unity rapporterar 0..0.9 under last, 0.9 == redo för aktivering
            float raw = Mathf.Clamp01(loadOp.progress / 0.9f);
            shown = Mathf.SmoothDamp(shown, raw, ref vel, smoothTime);
            SetProgressImmediate(shown);

            t += Time.unscaledDeltaTime;

            bool minTimeOk = t >= minReadTime;
            bool opReady   = loadOp.progress >= 0.9f;
            bool visualOk  = !waitFor100 || shown >= 0.999f;

            if (minTimeOk && opReady && visualOk) break;
            yield return null;
        }

        // Fade out för sömlös övergång
        yield return Fade(1f, 0f, fadeOut);

        // Aktivera nästa scen
        loadOp.allowSceneActivation = true;
    }

    void SetProgressImmediate(float v01)
    {
        v01 = Mathf.Clamp01(v01);
        if (progressBar) progressBar.value = v01;
        if (progressFill) progressFill.fillAmount = v01;
    }

    IEnumerator Fade(float a, float b, float dur)
    {
        if (dur <= 0f) { canvasGroup.alpha = b; yield break; }
        float t = 0f;
        while (t < dur)
        {
            t += Time.unscaledDeltaTime;
            canvasGroup.alpha = Mathf.Lerp(a, b, t / dur);
            yield return null;
        }
        canvasGroup.alpha = b;
    }
}