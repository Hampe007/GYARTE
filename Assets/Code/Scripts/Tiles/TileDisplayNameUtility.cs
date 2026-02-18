using System;
using System.Text;
using UnityEngine;

public static class TileDisplayNameUtility
{
    public static string FormatTileReference(Vector2Int coord)
        => FormatTileReference(coord.x, coord.y);

    public static string FormatTileReference(int x, int z)
        => $"{ToColumnLabel(x)}{z + 1}";

    public static string FormatTerrainTileLabel(string terrainLabel, Vector2Int coord)
        => FormatTerrainTileLabel(terrainLabel, coord.x, coord.y);

    public static string FormatTerrainTileLabel(string terrainLabel, int x, int z)
        => $"{terrainLabel}_{FormatTileReference(x, z)}";

    public static bool TryParseTileReference(string token, out int x, out int z)
    {
        x = 0;
        z = 0;

        if (string.IsNullOrWhiteSpace(token))
            return false;

        int split = 0;
        while (split < token.Length && char.IsLetter(token[split]))
            split++;

        if (split == 0 || split >= token.Length)
            return false;

        string letters = token[..split].ToUpperInvariant();
        string digits = token[split..];

        int column = 0;
        for (int i = 0; i < letters.Length; i++)
        {
            char c = letters[i];
            if (c < 'A' || c > 'Z')
                return false;

            column = checked(column * 26 + (c - 'A' + 1));
        }

        if (!int.TryParse(digits, out int oneBasedRow) || oneBasedRow <= 0)
            return false;

        x = column - 1;
        z = oneBasedRow - 1;
        return true;
    }

    public static string ToColumnLabel(int x)
    {
        if (x < 0)
            throw new ArgumentOutOfRangeException(nameof(x), "Tile X must be non-negative.");

        var sb = new StringBuilder(4);
        int value = x;

        do
        {
            int rem = value % 26;
            sb.Insert(0, (char)('A' + rem));
            value = value / 26 - 1;
        }
        while (value >= 0);

        return sb.ToString();
    }
}
