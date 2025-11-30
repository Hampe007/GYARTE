using Unity.Cinemachine;
using Mirror;
using UnityEngine;

/// <summary>
/// Ensures that only the local player's Cinemachine camera is active.
/// Designed for Cinemachine 3 (Unity 6000), where a "FreeLook Camera"
/// is implemented as a CinemachineCamera with Orbital Follow etc.
///
/// Attach this to the Player root. Assign:
/// - freeLookCameraObject: the child GameObject with CinemachineCamera
/// - cameraFollowTarget: the CameraTarget under the player
/// </summary>
public class PlayerCameraController : NetworkBehaviour
{
    [Header("Cinemachine Setup")]
    [Tooltip("The GameObject that has the CinemachineCamera component (your FreeLook camera).")]
    public GameObject freeLookCameraObject;

    [Tooltip("The transform the camera should follow and look at (usually CameraTarget under the player).")]
    public Transform cameraFollowTarget;

    // Internal reference to the CinemachineCamera component (Cinemachine 3)
    private CinemachineCamera cmCamera;

    private void Awake()
    {
        // Try to find / cache the CinemachineCamera component
        if (freeLookCameraObject != null)
        {
            cmCamera = freeLookCameraObject.GetComponent<CinemachineCamera>();
            if (cmCamera == null)
            {
                Debug.LogWarning("[PlayerCameraController] The assigned FreeLookCameraObject does not have a CinemachineCamera component. Did you assign the correct object?");
            }
        }
        else
        {
            // If nothing assigned, try to auto-find it in children
            cmCamera = GetComponentInChildren<CinemachineCamera>(true);
            if (cmCamera != null)
            {
                freeLookCameraObject = cmCamera.gameObject;
            }
            else
            {
                Debug.LogWarning("[PlayerCameraController] No CinemachineCamera found in children. Camera will not follow this player.");
            }
        }

        if (cameraFollowTarget == null)
        {
            // Try to auto-find a child named 'CameraTarget'
            Transform found = transform.Find("CameraTarget");
            if (found != null)
            {
                cameraFollowTarget = found;
            }
        }
    }

    private void Start()
    {
        if (freeLookCameraObject == null || cmCamera == null)
        {
            return;
        }

        bool shouldUseThisCamera = IsLocalOrOffline();

        if (shouldUseThisCamera)
        {
            // Enable and configure for local/offline player
            freeLookCameraObject.SetActive(true);

            if (cameraFollowTarget != null)
            {
                cmCamera.Follow = cameraFollowTarget;
                cmCamera.LookAt = cameraFollowTarget;
            }
        }
        else
        {
            // Disable camera on non-local players in a networked game
            freeLookCameraObject.SetActive(false);
        }
    }

    /// <summary>
    /// Returns true if this instance should control the camera:
    /// - If networking is NOT active: treat as singleplayer -> true.
    /// - If networking IS active: only for the local player.
    /// </summary>
    private bool IsLocalOrOffline()
    {
        if (NetworkClient.active)
        {
            return isLocalPlayer;
        }

        // Network not running -> offline / singleplayer
        return true;
    }
}