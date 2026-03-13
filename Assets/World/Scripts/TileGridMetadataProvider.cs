using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

public static class TileGridMetadataProvider
{
    public const string CanonicalAssetPath = "Assets/Resources/TileGridMetadata.asset";
    private static TileGridMetadata cached;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetCache() => cached = null;

    public static TileGridMetadata GetOrLoad()
    {
        if (cached != null)
            return cached;

        cached = Resources.Load<TileGridMetadata>(TileGridMetadata.ResourcesAssetName);
#if UNITY_EDITOR
        if (cached == null)
        {
            cached = AssetDatabase.LoadAssetAtPath<TileGridMetadata>(CanonicalAssetPath);
        }
#endif
        return cached;
    }

    public static void ClearCache()
    {
        cached = null;
    }
}
