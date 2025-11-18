using UnityEngine;
using System;

public class ScenarioToggler : MonoBehaviour
{
    public RenderScenario scenario;
    public Terrain terrain;
    public GameObject[] rockGroups;

    public static event Action<string> OnScenarioChanged;

    public void ApplyScenario()
    {
        if (terrain)
        {
            terrain.drawTreesAndFoliage = scenario.enableTrees || scenario.enableDetails;
            terrain.detailObjectDensity = scenario.enableDetails ? 1f : 0f;
        }

        foreach (var group in rockGroups)
            if (group) group.SetActive(scenario.enableRocks);

        OnScenarioChanged?.Invoke(scenario.name);
    }
}