using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public struct SerializedVector3
{
    public float x;
    public float y;
    public float z;

    public SerializedVector3(Vector3 v)
    {
        x = v.x;
        y = v.y;
        z = v.z;
    }

    public Vector3 ToVector3() => new Vector3(x, y, z);
}

[Serializable]
public struct TileData
{
    public int x;
    public int y;
    public string scenePath;
    public SerializedVector3 worldOrigin;
    public SerializedVector3 tileSize;
    public SerializedVector3 boundsCenter;
    public SerializedVector3 boundsSize;

    public Bounds ToBounds() => new Bounds(boundsCenter.ToVector3(), boundsSize.ToVector3());
    public Vector2Int Coord => new Vector2Int(x, y);
}

[Serializable]
public struct TerrainTileData
{
    public SerializedVector3 worldOrigin;
    public SerializedVector3 size;
    public string terrainDataPath;
}

[Serializable]
public struct PropInstanceData
{
    public GameObject prefab;
    public Vector3 localPosition;
    public Quaternion localRotation;
    public Vector3 localScale;
}

[CreateAssetMenu(menuName = "Tiles/Prop Tile Data", fileName = "PropTileData")]
public sealed class PropTileData : ScriptableObject
{
    public Vector2Int coord;
    public Vector3 tileOrigin;
    public Vector3 tileSize;
    public List<PropInstanceData> instances = new();

    public void ResetForTile(Vector2Int tileCoord, Vector3 origin, Vector3 size)
    {
        coord = tileCoord;
        tileOrigin = origin;
        tileSize = size;
        instances.Clear();
    }
}
