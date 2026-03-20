using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

[DisallowMultipleComponent]
public sealed class WorldMapController : MonoBehaviour
{
    private const string CanvasName = "WorldMapCanvas_Runtime";
    private const string CameraName = "WorldMapCamera_Runtime";
    private const string EventSystemName = "WorldMapEventSystem_Runtime";

    [Header("References")]
    [SerializeField] private TileStreamCoordinator streaming;
    [SerializeField] private Camera mapCamera;
    [SerializeField] private RenderTexture mapRenderTexture;

    [Header("Input")]
    [SerializeField] private Key toggleKey = Key.M;

    [Header("Layout")]
    [SerializeField, Range(0.5f, 1f)] private float screenCoverage = 0.9f;
    [SerializeField] private Vector2 mapAspectPadding = new(40f, 40f);
    [SerializeField] private Color backdropColor = new(0f, 0f, 0f, 0.82f);
    [SerializeField] private Color frameColor = new(0.07f, 0.07f, 0.07f, 0.96f);
    [SerializeField] private Color mapTintColor = new(1f, 1f, 1f, 1f);

    [Header("Tile Overlay")]
    [SerializeField] private Color loadedTileFill = new(1f, 1f, 1f, 0.08f);
    [SerializeField] private Color loadedTileBorder = new(1f, 1f, 1f, 0.92f);
    [SerializeField] private Color unloadedTileFill = new(0f, 0f, 0f, 0.6f);
    [SerializeField] private Color unloadedTileBorder = new(0.45f, 0.45f, 0.45f, 0.5f);
    [SerializeField] private Color currentTileFill = new(1f, 0.82f, 0.18f, 0.18f);
    [SerializeField] private Color currentTileBorder = new(1f, 0.85f, 0.25f, 1f);
    [SerializeField] private Color playerMarkerColor = new(1f, 0.4f, 0.12f, 1f);
    [SerializeField, Range(0.002f, 0.03f)] private float tileBorderThickness = 0.004f;
    [SerializeField, Range(0.01f, 0.05f)] private float playerMarkerNormalizedSize = 0.018f;

    [Header("Render Texture")]
    [SerializeField] private int renderTextureSize = 2048;
    [SerializeField] private float cameraHeightPadding = 250f;
    [SerializeField] private float cameraNearClip = 0.3f;
    [SerializeField] private float cameraFarClipPadding = 3000f;

    private Canvas rootCanvas;
    private CanvasScaler canvasScaler;
    private GraphicRaycaster graphicRaycaster;
    private RectTransform canvasRect;
    private Image backdropImage;
    private Image frameImage;
    private RawImage mapImage;
    private RectTransform mapRect;
    private RectTransform tileOverlayRoot;
    private RectTransform markerRoot;
    private Image playerMarker;
    private Text titleLabel;
    private Text detailLabel;
    private bool isOpen;
    private bool hadPriorCursorVisibility;
    private CursorLockMode priorCursorLockMode;
    private int cachedScreenWidth;
    private int cachedScreenHeight;
    private readonly Dictionary<string, WorldMapTileOverlay> tileOverlays = new();
    private readonly List<TileIndex.TileRecord> sortedTiles = new();
    private readonly HashSet<string> activeTilePaths = new();


    public void Bind(TileStreamCoordinator coordinator)
    {
        streaming = coordinator;
    }

    #region Unity Events

    private void Awake()
    {
        ResolveStreaming();
        EnsureUi();
        EnsureCamera();
        BuildTileOverlay();
        SetOpen(false, true);
    }

    private void OnEnable()
    {
        EnsureUi();
        EnsureCamera();
        HandleScreenResize(force: true);
    }

    private void LateUpdate()
    {
        HandleToggleInput();
        HandleScreenResize(force: false);

        if (!isOpen)
        {
            return;
        }

        ResolveStreaming();
        RefreshMapCamera();
        RefreshOverlayState();
    }

    private void OnDisable()
    {
        if (isOpen)
        {
            RestoreCursorState();
        }

        if (rootCanvas != null)
        {
            rootCanvas.gameObject.SetActive(false);
        }

        if (mapCamera != null)
        {
            mapCamera.gameObject.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        if (mapRenderTexture != null)
        {
            if (mapCamera != null && mapCamera.targetTexture == mapRenderTexture)
            {
                mapCamera.targetTexture = null;
            }

            Destroy(mapRenderTexture);
        }
    }

    #endregion

    #region Setup

    private void ResolveStreaming()
    {
        if (streaming == null)
        {
            streaming = FindFirstObjectByType<TileStreamCoordinator>();
        }
    }

    private void EnsureUi()
    {
        if (rootCanvas != null)
        {
            return;
        }

        GameObject canvasObject = new GameObject(CanvasName, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);

        rootCanvas = canvasObject.GetComponent<Canvas>();
        rootCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        rootCanvas.sortingOrder = short.MaxValue;

        canvasScaler = canvasObject.GetComponent<CanvasScaler>();
        canvasScaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        canvasScaler.referenceResolution = new Vector2(1920f, 1080f);
        canvasScaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        canvasScaler.matchWidthOrHeight = 0.5f;

        graphicRaycaster = canvasObject.GetComponent<GraphicRaycaster>();
        canvasRect = canvasObject.transform as RectTransform;
        canvasRect.anchorMin = Vector2.zero;
        canvasRect.anchorMax = Vector2.one;
        canvasRect.offsetMin = Vector2.zero;
        canvasRect.offsetMax = Vector2.zero;

        backdropImage = CreateImage("Backdrop", canvasRect, backdropColor);
        Stretch(backdropImage.rectTransform);

        frameImage = CreateImage("Frame", canvasRect, frameColor);
        mapImage = CreateRawImage("MapView", frameImage.rectTransform, mapTintColor);
        tileOverlayRoot = CreateRect("TileOverlay", mapImage.rectTransform);
        markerRoot = CreateRect("MarkerOverlay", mapImage.rectTransform);
        playerMarker = CreateImage("PlayerMarker", markerRoot, playerMarkerColor);
        titleLabel = CreateText("Title", frameImage.rectTransform, "WORLD MAP", 20, TextAnchor.UpperLeft);
        detailLabel = CreateText("Details", frameImage.rectTransform, string.Empty, 15, TextAnchor.UpperLeft);

        tileOverlayRoot.anchorMin = Vector2.zero;
        tileOverlayRoot.anchorMax = Vector2.one;
        tileOverlayRoot.offsetMin = Vector2.zero;
        tileOverlayRoot.offsetMax = Vector2.zero;

        markerRoot.anchorMin = Vector2.zero;
        markerRoot.anchorMax = Vector2.one;
        markerRoot.offsetMin = Vector2.zero;
        markerRoot.offsetMax = Vector2.zero;

        playerMarker.rectTransform.anchorMin = new Vector2(0.5f, 0.5f);
        playerMarker.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
        playerMarker.raycastTarget = false;

        titleLabel.raycastTarget = false;
        detailLabel.raycastTarget = false;

        EnsureEventSystem();
    }

    private void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null)
        {
            return;
        }

        GameObject eventSystemObject = new GameObject(EventSystemName, typeof(EventSystem), typeof(InputSystemUIInputModule));
        eventSystemObject.transform.SetParent(transform, false);
    }

    private void EnsureCamera()
    {
        if (mapCamera == null)
        {
            GameObject cameraObject = new GameObject(CameraName, typeof(Camera), typeof(AudioListener));
            cameraObject.transform.SetParent(transform, false);
            mapCamera = cameraObject.GetComponent<Camera>();
            var listener = cameraObject.GetComponent<AudioListener>();
            if (listener != null)
            {
                listener.enabled = false;
            }
        }

        mapCamera.orthographic = true;
        mapCamera.enabled = false;
        mapCamera.clearFlags = CameraClearFlags.SolidColor;
        mapCamera.backgroundColor = Color.black;
        mapCamera.cullingMask = ~0;
        mapCamera.allowHDR = true;
        mapCamera.allowMSAA = true;
        mapCamera.nearClipPlane = cameraNearClip;

        EnsureRenderTexture();
    }

    private void EnsureRenderTexture()
    {
        int textureSize = Mathf.Max(512, renderTextureSize);
        if (mapRenderTexture != null && mapRenderTexture.width == textureSize && mapRenderTexture.height == textureSize)
        {
            if (mapImage != null)
            {
                mapImage.texture = mapRenderTexture;
            }

            mapCamera.targetTexture = mapRenderTexture;
            return;
        }

        if (mapRenderTexture != null)
        {
            if (mapCamera != null && mapCamera.targetTexture == mapRenderTexture)
            {
                mapCamera.targetTexture = null;
            }

            Destroy(mapRenderTexture);
        }

        mapRenderTexture = new RenderTexture(textureSize, textureSize, 24, RenderTextureFormat.ARGB32)
        {
            name = "WorldMap_RT",
            useMipMap = false,
            autoGenerateMips = false,
            antiAliasing = 1
        };
        mapRenderTexture.Create();

        if (mapImage != null)
        {
            mapImage.texture = mapRenderTexture;
        }

        mapCamera.targetTexture = mapRenderTexture;
    }

    private void BuildTileOverlay()
    {
        tileOverlays.Clear();
        sortedTiles.Clear();

        if (streaming == null || streaming.index == null || tileOverlayRoot == null)
        {
            return;
        }

        foreach (Transform child in tileOverlayRoot)
        {
            Destroy(child.gameObject);
        }

        sortedTiles.AddRange(streaming.index.Tiles);
        sortedTiles.Sort((a, b) =>
        {
            int y = b.coord.y.CompareTo(a.coord.y);
            return y != 0 ? y : a.coord.x.CompareTo(b.coord.x);
        });

        for (int i = 0; i < sortedTiles.Count; i++)
        {
            TileIndex.TileRecord record = sortedTiles[i];
            if (string.IsNullOrEmpty(record.scenePath))
            {
                continue;
            }

            CreateTileOverlay(record);
        }
    }

    private void CreateTileOverlay(TileIndex.TileRecord record)
    {
        GameObject tileObject = new GameObject($"Tile_{record.coord.x}_{record.coord.y}", typeof(RectTransform), typeof(Image), typeof(WorldMapTileOverlay));
        tileObject.transform.SetParent(tileOverlayRoot, false);

        Image borderImage = tileObject.GetComponent<Image>();
        borderImage.raycastTarget = false;
        borderImage.type = Image.Type.Simple;

        RectTransform tileRect = tileObject.transform as RectTransform;
        tileRect.pivot = new Vector2(0f, 1f);

        GameObject fillObject = new GameObject("Fill", typeof(RectTransform), typeof(Image));
        fillObject.transform.SetParent(tileRect, false);
        Image fillImage = fillObject.GetComponent<Image>();
        fillImage.raycastTarget = false;
        fillImage.type = Image.Type.Simple;

        RectTransform fillRect = fillObject.transform as RectTransform;
        fillRect.anchorMin = Vector2.zero;
        fillRect.anchorMax = Vector2.one;
        fillRect.offsetMin = Vector2.zero;
        fillRect.offsetMax = Vector2.zero;

        WorldMapTileOverlay overlay = tileObject.GetComponent<WorldMapTileOverlay>();
        overlay.Initialize(record.coord, record.scenePath, fillImage, borderImage);
        tileOverlays[record.scenePath] = overlay;
    }

    #endregion

    #region Runtime

    private void HandleToggleInput()
    {
        if (Keyboard.current == null)
        {
            return;
        }

        KeyControl keyControl = Keyboard.current[toggleKey];
        if (keyControl != null && keyControl.wasPressedThisFrame)
        {
            SetOpen(!isOpen, false);
        }
    }

    private void SetOpen(bool open, bool instant)
    {
        isOpen = open;

        if (rootCanvas != null)
        {
            rootCanvas.gameObject.SetActive(open);
        }

        if (mapCamera != null)
        {
            mapCamera.gameObject.SetActive(open);
            mapCamera.enabled = open;
        }

        if (open)
        {
            CacheCursorState();
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            HandleScreenResize(force: true);
            RefreshMapCamera();
            RefreshOverlayState();
        }
        else if (!instant)
        {
            RestoreCursorState();
        }
    }

    private void CacheCursorState()
    {
        priorCursorLockMode = Cursor.lockState;
        hadPriorCursorVisibility = Cursor.visible;
    }

    private void RestoreCursorState()
    {
        Cursor.lockState = priorCursorLockMode;
        Cursor.visible = hadPriorCursorVisibility;
    }

    private void HandleScreenResize(bool force)
    {
        if (!force && cachedScreenWidth == Screen.width && cachedScreenHeight == Screen.height)
        {
            return;
        }

        cachedScreenWidth = Screen.width;
        cachedScreenHeight = Screen.height;

        if (frameImage == null || mapImage == null)
        {
            return;
        }

        float width = Screen.width * Mathf.Clamp(screenCoverage, 0.5f, 1f);
        float height = Screen.height * Mathf.Clamp(screenCoverage, 0.5f, 1f);

        RectTransform frameRect = frameImage.rectTransform;
        frameRect.anchorMin = new Vector2(0.5f, 0.5f);
        frameRect.anchorMax = new Vector2(0.5f, 0.5f);
        frameRect.pivot = new Vector2(0.5f, 0.5f);
        frameRect.sizeDelta = new Vector2(width, height);
        frameRect.anchoredPosition = Vector2.zero;

        mapRect = mapImage.rectTransform;
        mapRect.anchorMin = Vector2.zero;
        mapRect.anchorMax = Vector2.one;
        mapRect.offsetMin = new Vector2(mapAspectPadding.x, mapAspectPadding.y);
        mapRect.offsetMax = new Vector2(-mapAspectPadding.x, -mapAspectPadding.y - 48f);

        titleLabel.rectTransform.anchorMin = new Vector2(0f, 1f);
        titleLabel.rectTransform.anchorMax = new Vector2(1f, 1f);
        titleLabel.rectTransform.pivot = new Vector2(0f, 1f);
        titleLabel.rectTransform.offsetMin = new Vector2(18f, -32f);
        titleLabel.rectTransform.offsetMax = new Vector2(-18f, -8f);

        detailLabel.rectTransform.anchorMin = new Vector2(0f, 0f);
        detailLabel.rectTransform.anchorMax = new Vector2(1f, 0f);
        detailLabel.rectTransform.pivot = new Vector2(0f, 0f);
        detailLabel.rectTransform.offsetMin = new Vector2(18f, 10f);
        detailLabel.rectTransform.offsetMax = new Vector2(-18f, 34f);

        LayoutTileRects();
    }

    private void RefreshMapCamera()
    {
        if (streaming == null || streaming.index == null || mapCamera == null)
        {
            return;
        }

        Bounds worldBounds = GetIndexedWorldBounds(streaming.index);
        float totalWidth = Mathf.Max(1f, worldBounds.size.x);
        float totalHeight = Mathf.Max(1f, worldBounds.size.z);
        float centerX = worldBounds.center.x;
        float centerZ = worldBounds.center.z;
        float orthoSize = Mathf.Max(totalHeight * 0.5f, totalWidth * 0.5f / Mathf.Max(0.01f, mapCamera.aspect)) + 10f;
        float cameraHeight = cameraHeightPadding + cameraFarClipPadding * 0.15f;

        mapCamera.transform.SetPositionAndRotation(new Vector3(centerX, cameraHeight, centerZ), Quaternion.Euler(90f, 0f, 0f));
        mapCamera.orthographicSize = orthoSize;
        mapCamera.farClipPlane = cameraHeight + cameraFarClipPadding;
        mapCamera.Render();
    }

    private void RefreshOverlayState()
    {
        if (streaming == null || streaming.index == null)
        {
            return;
        }

        if (tileOverlays.Count != streaming.index.Tiles.Count)
        {
            BuildTileOverlay();
            LayoutTileRects();
        }

        activeTilePaths.Clear();
        foreach (string path in streaming.GetActiveTilePaths())
        {
            activeTilePaths.Add(path);
        }

        Transform target = streaming.CurrentStreamingTarget;
        Vector2Int playerCoord = target != null ? streaming.index.WorldToTile(target.position) : new Vector2Int(int.MinValue, int.MinValue);

        foreach (var pair in tileOverlays)
        {
            WorldMapTileOverlay overlay = pair.Value;
            bool isLoaded = activeTilePaths.Contains(pair.Key);
            bool isPlayerTile = overlay.Coord == playerCoord;

            Color fillColor = isLoaded ? loadedTileFill : unloadedTileFill;
            Color borderColor = isLoaded ? loadedTileBorder : unloadedTileBorder;

            if (isPlayerTile)
            {
                fillColor = Color.Lerp(fillColor, currentTileFill, currentTileFill.a);
                borderColor = currentTileBorder;
            }

            overlay.SetVisual(fillColor, borderColor);
        }

        RefreshPlayerMarker(target);
        RefreshLabels(playerCoord);
    }

    private void RefreshPlayerMarker(Transform target)
    {
        if (playerMarker == null || mapRect == null)
        {
            return;
        }

        bool hasTarget = target != null && streaming != null && streaming.index != null;
        playerMarker.enabled = hasTarget;
        if (!hasTarget)
        {
            return;
        }

        Vector2 normalized = GetNormalizedWorldPosition(target.position);
        float width = mapRect.rect.width;
        float height = mapRect.rect.height;
        float markerSize = Mathf.Min(width, height) * playerMarkerNormalizedSize;

        playerMarker.rectTransform.anchorMin = normalized;
        playerMarker.rectTransform.anchorMax = normalized;
        playerMarker.rectTransform.sizeDelta = new Vector2(markerSize, markerSize);
        playerMarker.rectTransform.anchoredPosition = Vector2.zero;
    }

    private void RefreshLabels(Vector2Int playerCoord)
    {
        if (titleLabel == null || detailLabel == null)
        {
            return;
        }

        titleLabel.text = $"WORLD MAP  [{toggleKey}]";
        detailLabel.text = $"Loaded: {activeTilePaths.Count} / {tileOverlays.Count}    Current Tile: {(playerCoord.x == int.MinValue ? "--" : $"{playerCoord.x}, {playerCoord.y}")}";
    }

    private void LayoutTileRects()
    {
        if (streaming == null || streaming.index == null || mapRect == null)
        {
            return;
        }

        foreach (TileIndex.TileRecord record in streaming.index.Tiles)
        {
            if (!tileOverlays.TryGetValue(record.scenePath, out WorldMapTileOverlay overlay) || overlay.RectTransform == null)
            {
                continue;
            }

            Rect tileRect = WorldTileToMapRect(record.worldOrigin, record.tileSize);
            overlay.RectTransform.anchorMin = new Vector2(0f, 1f);
            overlay.RectTransform.anchorMax = new Vector2(0f, 1f);
            overlay.RectTransform.pivot = new Vector2(0f, 1f);
            overlay.RectTransform.anchoredPosition = new Vector2(tileRect.xMin, -tileRect.yMin);
            overlay.RectTransform.sizeDelta = new Vector2(tileRect.width, tileRect.height);

            Transform fillTransform = overlay.transform.childCount > 0 ? overlay.transform.GetChild(0) : null;
            RectTransform fillRect = fillTransform as RectTransform;
            if (fillRect != null)
            {
                float thickness = Mathf.Max(1f, Mathf.Min(tileRect.width, tileRect.height) * tileBorderThickness);
                fillRect.offsetMin = new Vector2(thickness, thickness);
                fillRect.offsetMax = new Vector2(-thickness, -thickness);
            }
        }
    }

    #endregion

    #region Helpers

    private Rect WorldTileToMapRect(Vector3 worldOrigin, Vector3 worldSize)
    {
        Vector2 min = GetNormalizedWorldPosition(worldOrigin);
        Vector2 max = GetNormalizedWorldPosition(worldOrigin + new Vector3(worldSize.x, 0f, worldSize.z));

        float xMin = mapRect.rect.width * min.x;
        float xMax = mapRect.rect.width * max.x;
        float yMin = mapRect.rect.height * (1f - max.y);
        float yMax = mapRect.rect.height * (1f - min.y);

        return Rect.MinMaxRect(xMin, yMin, xMax, yMax);
    }

    private Vector2 GetNormalizedWorldPosition(Vector3 worldPosition)
    {
        TileIndex tileIndex = streaming != null ? streaming.index : null;
        Bounds worldBounds = tileIndex != null ? GetIndexedWorldBounds(tileIndex) : new Bounds(Vector3.zero, Vector3.one);
        Vector3 min = worldBounds.min;
        float totalWidth = Mathf.Max(1f, worldBounds.size.x);
        float totalHeight = Mathf.Max(1f, worldBounds.size.z);

        return new Vector2(
            Mathf.Clamp01((worldPosition.x - min.x) / totalWidth),
            Mathf.Clamp01((worldPosition.z - min.z) / totalHeight));
    }

    private Bounds GetIndexedWorldBounds(TileIndex tileIndex)
    {
        if (tileIndex == null || tileIndex.Tiles.Count == 0)
        {
            return new Bounds(Vector3.zero, Vector3.one);
        }

        Bounds bounds = tileIndex.Tiles[0].worldBounds;
        for (int i = 1; i < tileIndex.Tiles.Count; i++)
        {
            bounds.Encapsulate(tileIndex.Tiles[i].worldBounds.min);
            bounds.Encapsulate(tileIndex.Tiles[i].worldBounds.max);
        }

        return bounds;
    }

    private Vector2Int EstimateGridDimensions(TileIndex tileIndex)
    {
        int maxX = 1;
        int maxY = 1;

        for (int i = 0; i < tileIndex.Tiles.Count; i++)
        {
            TileIndex.TileRecord tile = tileIndex.Tiles[i];
            maxX = Mathf.Max(maxX, tile.coord.x + 1);
            maxY = Mathf.Max(maxY, tile.coord.y + 1);
        }

        return new Vector2Int(maxX, maxY);
    }

    private static Image CreateImage(string name, Transform parent, Color color)
    {
        GameObject imageObject = new GameObject(name, typeof(RectTransform), typeof(Image));
        imageObject.transform.SetParent(parent, false);
        Image image = imageObject.GetComponent<Image>();
        image.color = color;
        return image;
    }

    private static RawImage CreateRawImage(string name, Transform parent, Color color)
    {
        GameObject imageObject = new GameObject(name, typeof(RectTransform), typeof(RawImage));
        imageObject.transform.SetParent(parent, false);
        RawImage image = imageObject.GetComponent<RawImage>();
        image.color = color;
        image.raycastTarget = false;
        return image;
    }

    private static Text CreateText(string name, Transform parent, string content, int fontSize, TextAnchor anchor)
    {
        GameObject textObject = new GameObject(name, typeof(RectTransform), typeof(Text));
        textObject.transform.SetParent(parent, false);
        Text text = textObject.GetComponent<Text>();
        text.text = content;
        text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.fontSize = fontSize;
        text.alignment = anchor;
        text.color = Color.white;
        return text;
    }

    private static RectTransform CreateRect(string name, Transform parent)
    {
        GameObject rectObject = new GameObject(name, typeof(RectTransform));
        rectObject.transform.SetParent(parent, false);
        return rectObject.transform as RectTransform;
    }

    private static void Stretch(RectTransform rectTransform)
    {
        rectTransform.anchorMin = Vector2.zero;
        rectTransform.anchorMax = Vector2.one;
        rectTransform.offsetMin = Vector2.zero;
        rectTransform.offsetMax = Vector2.zero;
    }

    #endregion
}
