using System;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Tile Slice Settings", fileName = "TileSliceSettings")]
public sealed class TileSliceSettings : ScriptableObject
{
    [Header("Desired Inputs")]
    public float tileSizeMeters = 250f;
    public bool evenFitNoRemainder = true;
    public bool forceSquareTiles   = true;

    [Serializable]
    public sealed class PerTerrain
    {
        public string label;      // sanitized terrain name used by slicer
        public Vector3 origin;    // world origin of source terrain
        public Vector3 size;      // source terrain size (meters)
        public int tilesX;
        public int tilesY;
        public float tileSizeX;   // final exact sizes used by slicer
        public float tileSizeY;
    }

    [Header("Last Slice Results (auto-filled by slicer)")]
    public List<PerTerrain> lastResults = new List<PerTerrain>();

    public void Upsert(string label, Vector3 origin, Vector3 size, int tilesX, int tilesY, float tileSizeX, float tileSizeY)
    {
        int i = lastResults.FindIndex(r => r.label == label);
        var r = new PerTerrain { label = label, origin = origin, size = size, tilesX = tilesX, tilesY = tilesY, tileSizeX = tileSizeX, tileSizeY = tileSizeY };
        if (i >= 0) lastResults[i] = r; else lastResults.Add(r);
    }

    public bool TryGet(string label, out PerTerrain r)
    {
        r = lastResults.Find(x => x.label == label);
        return r != null;
    }
}