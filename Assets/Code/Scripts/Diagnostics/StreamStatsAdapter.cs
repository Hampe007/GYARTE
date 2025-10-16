// Bridges TileStreamCoordinator → RuntimePerfOverlay.IStreamStats

using UnityEngine;

public class StreamStatsAdapter : MonoBehaviour, IStreamStats
{
    public TileStreamCoordinator coordinator;

    public int ActiveTiles
    {
        get
        {
            if (coordinator == null) return 0;
#if MIRROR
            // Prefer client view if active, otherwise server
            if (Mirror.NetworkClient.active) return coordinator.ClientTiles.Count;
            if (Mirror.NetworkServer.active) return coordinator.ServerTiles.Count;
#endif
            return 0;
        }
    }

    // TileStreamCoordinator doesn’t expose queues directly,
    // so keep these simple placeholders for now.
    public int QueuedLoads => 0;
    public int LoadsThisFrame => 0;
}