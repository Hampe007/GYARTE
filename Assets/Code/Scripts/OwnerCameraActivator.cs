using System.Collections.Generic;
using Mirror;
using Unity.Cinemachine;
using UnityEngine;

/// <summary>
/// Ensures only the owning client drives/enables the Cinemachine cameras on a player prefab.
/// Attach is automatic via CustomNetworkManager when spawning the in-game character.
/// </summary>
[DisallowMultipleComponent]
public class OwnerCameraActivator : NetworkBehaviour
{
    [SerializeField] private List<CinemachineCamera> cameras = new();
    [SerializeField] private List<Camera> legacyCameras = new();
    [SerializeField] private bool disableForNonOwners = true;
    [SerializeField] private int ownerPriority = 100;

    private readonly List<int> _initialPriorities = new();
    private bool _initialized;

    private void Awake()
    {
        CacheCamerasIfNeeded();
        ApplyOwnershipState(false); // default: keep cameras disabled until authority arrives
    }

    private void CacheCamerasIfNeeded()
    {
        if (_initialized) return;

        if (cameras.Count == 0)
            cameras.AddRange(GetComponentsInChildren<CinemachineCamera>(true));

        if (legacyCameras.Count == 0)
            legacyCameras.AddRange(GetComponentsInChildren<Camera>(true));

        _initialPriorities.Clear();
        foreach (var cam in cameras)
            _initialPriorities.Add(cam != null ? cam.Priority.Value : 0);

        _initialized = true;
    }

    public override void OnStartAuthority()
    {
        ApplyOwnershipState(true);
    }

    public override void OnStopAuthority()
    {
        ApplyOwnershipState(false);
    }

    public override void OnStartClient()
    {
        // Ensure non-owners don't drive the camera if they joined late.
        if (!isOwned)
            ApplyOwnershipState(false);
    }

    private void ApplyOwnershipState(bool isOwner)
    {
        CacheCamerasIfNeeded();

        for (int i = 0; i < cameras.Count; i++)
        {
            var cam = cameras[i];
            if (cam == null) continue;

            if (isOwner)
            {
                cam.gameObject.SetActive(true);
                int basePriority = (i < _initialPriorities.Count) ? _initialPriorities[i] : cam.Priority.Value;
                cam.Priority.Value = Mathf.Max(basePriority + 1, ownerPriority);
                cam.Prioritize();
            }
            else if (disableForNonOwners)
            {
                cam.gameObject.SetActive(false);
            }
        }

        foreach (var cam in legacyCameras)
        {
            if (cam == null) continue;
            if (isOwner)
                cam.gameObject.SetActive(true);
            else if (disableForNonOwners)
                cam.gameObject.SetActive(false);
        }

        if (isOwner)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else
        {
            // Only unlock if this is the local client and we disabled their camera
            if (isClient && disableForNonOwners)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }
    }
}
