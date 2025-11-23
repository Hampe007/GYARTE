using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

[CreateAssetMenu(fileName = "VillageRules", menuName = "Generation/Village Rule Set")]
public class VillageRuleSet : ScriptableObject
{
    [Header("Center Buildings (near plaza)")]
    public GameObject[] centerBuildings;

    [Header("Roadside Houses")]
    public GameObject[] roadsideHouses;

    [Header("Extra Small Huts / Scattered Houses")]
    public GameObject[] extraHuts;

    [Header("Center Decorations (well, statue, etc.)")]
    public GameObject[] decorationsCenter;

    [Header("Roadside Decorations (crates, carts, fences)")]
    public GameObject[] decorationsRoadside;

    [Header("Random Decorations (anywhere in area)")]
    public GameObject[] decorationsRandom;

#if UNITY_EDITOR
    void OnValidate()
    {
        ValidateArray(centerBuildings, "centerBuildings");
        ValidateArray(roadsideHouses, "roadsideHouses");
        ValidateArray(extraHuts, "extraHuts");
        ValidateArray(decorationsCenter, "decorationsCenter");
        ValidateArray(decorationsRoadside, "decorationsRoadside");
        ValidateArray(decorationsRandom, "decorationsRandom");
    }

    void ValidateArray(GameObject[] arr, string label)
    {
        if (arr == null) return;

        for (int i = 0; i < arr.Length; i++)
        {
            var obj = arr[i];
            if (obj == null) continue;

            var type = PrefabUtility.GetPrefabAssetType(obj);
            if (type == PrefabAssetType.NotAPrefab)
            {
                Debug.LogWarning($"{name}: {label}[{i}] is not a prefab asset, clearing reference.");
                arr[i] = null;
            }
        }
    }
#endif

    public GameObject GetRandomCenterBuilding(System.Random rand)
    {
        return SafeRandomPrefab(centerBuildings, rand)
               ?? SafeRandomPrefab(roadsideHouses, rand)
               ?? SafeRandomPrefab(extraHuts, rand);
    }

    public GameObject GetRandomRoadsideHouse(System.Random rand)
    {
        return SafeRandomPrefab(roadsideHouses, rand)
               ?? SafeRandomPrefab(extraHuts, rand);
    }

    public GameObject GetRandomExtraHut(System.Random rand)
    {
        return SafeRandomPrefab(extraHuts, rand)
               ?? SafeRandomPrefab(roadsideHouses, rand);
    }

    public GameObject GetRandomDecorationCenter(System.Random rand)
    {
        return SafeRandomPrefab(decorationsCenter, rand)
               ?? SafeRandomPrefab(decorationsRandom, rand);
    }

    public GameObject GetRandomDecorationRoadside(System.Random rand)
    {
        return SafeRandomPrefab(decorationsRoadside, rand)
               ?? SafeRandomPrefab(decorationsRandom, rand);
    }

    public GameObject GetRandomDecorationAnywhere(System.Random rand)
    {
        return SafeRandomPrefab(decorationsRandom, rand)
               ?? SafeRandomPrefab(decorationsCenter, rand)
               ?? SafeRandomPrefab(decorationsRoadside, rand);
    }

    GameObject SafeRandomPrefab(GameObject[] list, System.Random rand)
    {
        if (list == null || list.Length == 0) return null;

        for (int attempts = 0; attempts < 16; attempts++)
        {
            int i = rand.Next(0, list.Length);
            var prefab = list[i];
            if (prefab != null)
                return prefab;
        }

        return null;
    }
}