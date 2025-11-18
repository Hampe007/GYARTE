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
            return coordinator.ClientTiles.Count;
        }
    }

    public int QueuedLoads
    {
        get
        {
            if (coordinator == null) return 0;
#if MIRROR
            if (Mirror.NetworkClient.active) return coordinator.ClientQueuedLoads;
            if (Mirror.NetworkServer.active) return coordinator.ServerQueuedLoads;
#endif
            return coordinator.ClientQueuedLoads;
        }
    }

    public int LoadsThisFrame
    {
        get
        {
            if (coordinator == null) return 0;
#if MIRROR
            if (Mirror.NetworkClient.active) return coordinator.ClientLoadsThisFrame;
            if (Mirror.NetworkServer.active) return coordinator.ServerLoadsThisFrame;
#endif
            return coordinator.ClientLoadsThisFrame;
        }
    }
}