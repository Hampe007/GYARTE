using UnityEngine;

[CreateAssetMenu(menuName = "Render/Render Scenario")]
public class RenderScenario : ScriptableObject
{
    public bool enableTerrainTextures = true;
    public bool enableDetails = true;
    public bool enableTrees = true;
    public bool enableRocks = true;
}
