using System.Collections.Generic;
using UnityEngine;

public partial class TerrainPrefabPainter
{
    #region Rule Classes

    [System.Serializable]
    public class PrefabPaintRule
    {
        public string name = "Prefab Rule";
        public GameObject prefab;
        public PrefabVariant[] variants;

        public float density = 0.15f;
        public float minHeight = 0f;
        public float maxHeight = 1000f;
        public float maxSlope = 35f;

        public int splatIndex = -1;

        public float noiseScale = 0.01f;
        public float noiseThreshold = 0.5f;

        public Vector2 randomScale = new Vector2(0.9f, 1.2f);
        public float clearRadius = 1.5f;

        public bool deleteBeforeSpawn = false;

        public bool useVolumeArea = false;
        public ForestAreaVolume volumeRef = null;

        // Saved detail values so we can restore grass after deletion
        public Dictionary<Vector2Int, int> clearedGrass;
    }

    [System.Serializable]
    public class PrefabVariant
    {
        public GameObject prefab;
        public float weight = 1.0f;
    }

    #endregion
}

#region Helper Components

// Marker component for spawned props
public class TileProp : MonoBehaviour {}

public class ForestAreaVolume : MonoBehaviour
{
    public BoxCollider col;

    void OnValidate()
    {
        col = GetComponent<BoxCollider>();
    }
}

// Circle area used for global sampling
public class SpawnCircleVolume : MonoBehaviour
{
    public float radius = 25f;

    public bool Contains(Vector3 worldPos)
    {
        Vector2 p = new Vector2(worldPos.x, worldPos.z);
        Vector2 c = new Vector2(transform.position.x, transform.position.z);
        return (p - c).sqrMagnitude <= radius * radius;
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        Gizmos.color = new Color(0, 1, 0, 0.5f);
        UnityEditor.Handles.DrawSolidDisc(
            new Vector3(transform.position.x, transform.position.y, transform.position.z),
            Vector3.up,
            radius
        );

        Gizmos.color = Color.green;
        UnityEditor.Handles.DrawWireDisc(
            new Vector3(transform.position.x, transform.position.y, transform.position.z),
            Vector3.up,
            radius
        );
    }
#endif
}

#endregion