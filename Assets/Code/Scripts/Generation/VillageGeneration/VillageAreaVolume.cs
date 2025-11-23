using UnityEngine;

[ExecuteAlways]
public class VillageAreaVolume : MonoBehaviour
{
    [Header("Collider")]
    public BoxCollider col;

    // Grass backup data
    [System.NonSerialized] bool hasGrassBackup;
    [System.NonSerialized] int backupX;
    [System.NonSerialized] int backupZ;
    [System.NonSerialized] int backupWidth;
    [System.NonSerialized] int backupHeight;
    [System.NonSerialized] int[][,] grassBackup; // [detailLayer][z,x]

    void Reset()
    {
        EnsureCollider();
        SnapToTerrain();
    }

    void OnValidate()
    {
        EnsureCollider();
        SnapToTerrain();
    }

    void Update()
    {
        if (!Application.isPlaying)
            SnapToTerrain();
    }

    void EnsureCollider()
    {
        if (col == null)
        {
            col = GetComponent<BoxCollider>();
            if (col == null)
                col = gameObject.AddComponent<BoxCollider>();
        }

        col.isTrigger = true;
    }

    void SnapToTerrain()
    {
        EnsureCollider();

        Vector3 pos = transform.position;

        Ray ray = new Ray(new Vector3(pos.x, pos.y + 2000f, pos.z), Vector3.down);
        RaycastHit hit;

        if (Physics.Raycast(ray, out hit, 5000f, ~0, QueryTriggerInteraction.Ignore))
        {
            pos.y = hit.point.y + col.size.y * 0.5f;
            transform.position = pos;
            return;
        }

        if (Terrain.activeTerrain != null)
        {
            float h = Terrain.activeTerrain.SampleHeight(pos) +
                      Terrain.activeTerrain.transform.position.y;
            pos.y = h + col.size.y * 0.5f;
            transform.position = pos;
        }
    }

    public Bounds GetWorldBounds()
    {
        EnsureCollider();
        return col.bounds;
    }

    public void BackupAndClearGrass(Terrain terrain)
    {
        if (terrain == null || terrain.terrainData == null)
            return;

        var td = terrain.terrainData;
        int detailRes = td.detailResolution;
        int layerCount = td.detailPrototypes != null ? td.detailPrototypes.Length : 0;

        if (layerCount == 0 || detailRes <= 0)
            return;

        Bounds b = GetWorldBounds();
        Vector3 tPos = terrain.transform.position;
        Vector3 tSize = td.size;

        float nxMin = Mathf.InverseLerp(tPos.x, tPos.x + tSize.x, b.min.x);
        float nxMax = Mathf.InverseLerp(tPos.x, tPos.x + tSize.x, b.max.x);
        float nzMin = Mathf.InverseLerp(tPos.z, tPos.z + tSize.z, b.min.z);
        float nzMax = Mathf.InverseLerp(tPos.z, tPos.z + tSize.z, b.max.z);

        nxMin = Mathf.Clamp01(nxMin);
        nxMax = Mathf.Clamp01(nxMax);
        nzMin = Mathf.Clamp01(nzMin);
        nzMax = Mathf.Clamp01(nzMax);

        if (nxMax <= nxMin || nzMax <= nzMin)
            return;

        int maxIdx = detailRes - 1;

        int x0 = Mathf.Clamp(Mathf.FloorToInt(nxMin * maxIdx), 0, maxIdx);
        int x1 = Mathf.Clamp(Mathf.CeilToInt(nxMax * maxIdx), 0, maxIdx);
        int z0 = Mathf.Clamp(Mathf.FloorToInt(nzMin * maxIdx), 0, maxIdx);
        int z1 = Mathf.Clamp(Mathf.CeilToInt(nzMax * maxIdx), 0, maxIdx);

        int width = x1 - x0 + 1;
        int height = z1 - z0 + 1;

        if (width <= 0 || height <= 0)
            return;

        backupX = x0;
        backupZ = z0;
        backupWidth = width;
        backupHeight = height;

        grassBackup = new int[layerCount][,];

        for (int layer = 0; layer < layerCount; layer++)
        {
            int[,] patch = td.GetDetailLayer(x0, z0, width, height, layer);
            grassBackup[layer] = patch;

            int[,] cleared = new int[height, width];
            td.SetDetailLayer(x0, z0, layer, cleared);
        }

        hasGrassBackup = true;
    }

    public void RestoreGrass(Terrain terrain)
    {
        if (!hasGrassBackup)
            return;

        if (terrain == null || terrain.terrainData == null)
            return;

        var td = terrain.terrainData;
        int layerCount = grassBackup != null ? grassBackup.Length : 0;

        if (layerCount == 0)
            return;

        for (int layer = 0; layer < layerCount; layer++)
        {
            int[,] patch = grassBackup[layer];
            if (patch == null) continue;

            td.SetDetailLayer(backupX, backupZ, layer, patch);
        }

        hasGrassBackup = false;
        grassBackup = null;
    }
    
    public void ApplyDynamicGrassSuppression(Terrain terrain)
    {
        if (terrain == null || terrain.terrainData == null)
            return;

        var td = terrain.terrainData;

        // House + prop + road detection inside this area
        var objs = FindObjectsOfType<Transform>();
        foreach (var t in objs)
        {
            if (!IsInsideArea(t.position)) continue;

            float radius = 2.5f;

            // You can map based on tags
            if (t.CompareTag("House")) radius = 4f;
            if (t.CompareTag("Prop")) radius = 2f;
            if (t.CompareTag("RoadSample")) radius = 2.5f;

            SuppressGrassCircle(t.position, radius, terrain);
        }
    }
    
    bool IsInsideArea(Vector3 world)
    {
        return col.bounds.Contains(world);
    }

    public void SuppressGrassCircle(Vector3 worldPos, float radius, Terrain terrain)
    {
        var td = terrain.terrainData;

        Vector3 tPos = terrain.transform.position;
        Vector3 local = worldPos - tPos;

        float nx = Mathf.InverseLerp(0, td.size.x, local.x);
        float nz = Mathf.InverseLerp(0, td.size.z, local.z);

        int cx = Mathf.RoundToInt(nx * (td.detailResolution - 1));
        int cz = Mathf.RoundToInt(nz * (td.detailResolution - 1));

        int r = Mathf.RoundToInt(radius / td.size.x * td.detailResolution);

        int layerCount = td.detailPrototypes.Length;

        for (int z = -r; z <= r; z++)
        for (int l = 0; l < layerCount; l++)
        {
            int[,] map = td.GetDetailLayer(0, 0, td.detailResolution, td.detailResolution, l);

            for (int x = -r; x <= r; x++)
            {
                int ix = cx + x;
                int iz = cz + z;

                if (ix < 0 || iz < 0 || ix >= td.detailResolution || iz >= td.detailResolution)
                    continue;

                if (x * x + z * z > r * r)
                    continue;

                map[iz, ix] = 0;
            }

            td.SetDetailLayer(0, 0, l, map);
        }

    }
    
#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        EnsureCollider();

        Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.15f);
        Gizmos.DrawCube(col.bounds.center, col.bounds.size);

        Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.9f);
        Gizmos.DrawWireCube(col.bounds.center, col.bounds.size);
    }
#endif
}