using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;


[RequireComponent(typeof(RawImage))]
public class OpenUrlOnClick : MonoBehaviour, IPointerClickHandler
{
    [SerializeField, Tooltip("Absolute URL to open (e.g., https://example.com).")]
    private string url = "";

    void Reset() => GetComponent<RawImage>().raycastTarget = true;

    public void OnPointerClick(PointerEventData eventData)
    {
        var u = (url ?? "").Trim();
        if (System.Uri.TryCreate(u, System.UriKind.Absolute, out _)) Application.OpenURL(u);
        else Debug.LogWarning($"[OpenUrlOnClick] Invalid URL: '{u}'", this);
    }
}
