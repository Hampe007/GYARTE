using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(-50)] // be available very early after scene load
public class SpawnPointManager : MonoBehaviour
{
    [System.Serializable]
    public struct SpawnPose
    {
        [Tooltip("World-space position")]
        public Vector3 position;

        [Tooltip("Optional Y-axis rotation (degrees). Leave 0 if you don't care.")]
        public float yaw;
    }

    [Tooltip("Fill this with your spawn coordinates (X,Y,Z) and optional yaw.")]
    public List<SpawnPose> spawns = new();

    // steamId -> index in 'spawns'
    private readonly Dictionary<ulong, int> assigned = new();
    private readonly HashSet<int> used = new();

    public bool HasPoints => spawns != null && spawns.Count > 0;

    public void ResetAll()
    {
        assigned.Clear();
        used.Clear();
    }

    /// <summary>Force a specific spawn index for a SteamID (set before replacement).</summary>
    public void SetSpawnFor(ulong steamId, int index)
    {
        if (!HasPoints) return;
        index = Mathf.Clamp(index, 0, spawns.Count - 1);
        assigned[steamId] = index;
        used.Add(index);
    }

    public void ClearSpawnFor(ulong steamId)
    {
        if (assigned.TryGetValue(steamId, out var idx))
            used.Remove(idx);
        assigned.Remove(steamId);
    }

    public (Vector3 pos, Quaternion rot) GetSpawnFor(ulong steamId)
    {
        if (!HasPoints) return (Vector3.zero, Quaternion.identity);

        // Pre-assigned?
        if (assigned.TryGetValue(steamId, out int idx))
            return ToPose(spawns[idx]);

        // First free slot
        for (int i = 0; i < spawns.Count; i++)
        {
            if (used.Contains(i)) continue;
            used.Add(i);
            assigned[steamId] = i;
            return ToPose(spawns[i]);
        }

        // Overflow fallback: stable wrap by SteamID
        int fallback = Mathf.Abs((int)(steamId % (ulong)spawns.Count));
        return ToPose(spawns[fallback]);
    }

    public bool TryGetNearestSpawn(Vector3 worldPosition, out int index, out SpawnPose spawn)
    {
        index = -1;
        spawn = default;
        if (!HasPoints)
        {
            return false;
        }

        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < spawns.Count; i++)
        {
            float distance = (spawns[i].position - worldPosition).sqrMagnitude;
            if (distance >= bestDistance)
            {
                continue;
            }

            bestDistance = distance;
            index = i;
            spawn = spawns[i];
        }

        return index >= 0;
    }

    private static (Vector3, Quaternion) ToPose(in SpawnPose s)
        => (s.position, Quaternion.Euler(0f, s.yaw, 0f));

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!HasPoints) return;
        for (int i = 0; i < spawns.Count; i++)
        {
            var s = spawns[i];
            Gizmos.DrawWireSphere(s.position, 0.4f);
            // Draw a short direction line to visualize yaw
            Vector3 fwd = Quaternion.Euler(0f, s.yaw, 0f) * Vector3.forward;
            Gizmos.DrawLine(s.position, s.position + fwd * 1.0f);
            UnityEditor.Handles.Label(s.position + Vector3.up * 0.5f,
                $"#{i}  ({s.position.x:0.##}, {s.position.y:0.##}, {s.position.z:0.##})");
        }
    }
#endif
}