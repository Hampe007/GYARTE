using System.Text.RegularExpressions;

public static class TileTerrainLabelUtility
{
    private static readonly Regex InvalidLabelChars = new("[^A-Za-z0-9_\\-]", RegexOptions.Compiled);

    public static string ToLabel(string terrainName)
    {
        if (string.IsNullOrEmpty(terrainName))
            return string.Empty;

        return InvalidLabelChars.Replace(terrainName, "_");
    }
}
