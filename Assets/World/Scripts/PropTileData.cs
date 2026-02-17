using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public struct PropTransform
{
    public Vector3 localPosition;
    public Quaternion localRotation;
    public Vector3 localScale;
}

[Serializable]
public sealed class PropPrefabGroup
{
    public GameObject prefab;
    public List<PropTransform> transforms = new();
}

[CreateAssetMenu(menuName = "Tiles/Prop Tile Data", fileName = "PropTileData")]
public sealed class PropTileData : ScriptableObject
{
    public Vector2Int coord;
    public Vector3 tileOrigin;
    public Vector3 tileSize;

    // Grouped by prefab
    public List<PropPrefabGroup> groups = new();

    #region API

    public void ResetForTile(Vector2Int tileCoord, Vector3 origin, Vector3 size)
    {
        coord = tileCoord;
        tileOrigin = origin;
        tileSize = size;

        if (groups == null) groups = new List<PropPrefabGroup>();
        groups.Clear();
    }

    public void AddInstance(GameObject prefab, Vector3 localPos, Quaternion localRot, Vector3 localScale)
    {
        if (prefab == null) return;

        var g = GetOrCreateGroup(prefab);
        g.transforms.Add(new PropTransform
        {
            localPosition = localPos,
            localRotation = localRot,
            localScale = localScale
        });
    }

    private PropPrefabGroup GetOrCreateGroup(GameObject prefab)
    {
        for (int i = 0; i < groups.Count; i++)
        {
            if (groups[i] != null && groups[i].prefab == prefab)
                return groups[i];
        }

        var created = new PropPrefabGroup { prefab = prefab };
        groups.Add(created);
        return created;
    }

    #endregion
}