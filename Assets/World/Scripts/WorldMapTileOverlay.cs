using UnityEngine;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class WorldMapTileOverlay : MonoBehaviour
{
    [SerializeField] private Image fillImage;
    [SerializeField] private Image borderImage;

    public RectTransform RectTransform { get; private set; }
    public Vector2Int Coord { get; private set; }
    public string ScenePath { get; private set; }

    private void Awake()
    {
        RectTransform = transform as RectTransform;

        if (fillImage == null)
        {
            fillImage = GetComponent<Image>();
        }
    }

    #region Public API

    public void Initialize(Vector2Int coord, string scenePath, Image fill, Image border)
    {
        Coord = coord;
        ScenePath = scenePath;
        fillImage = fill;
        borderImage = border;
        RectTransform = transform as RectTransform;
    }

    public void SetVisual(Color fillColor, Color borderColor)
    {
        if (fillImage != null)
        {
            fillImage.color = fillColor;
        }

        if (borderImage != null)
        {
            borderImage.color = borderColor;
            borderImage.enabled = borderColor.a > 0.001f;
        }
    }

    #endregion
}
