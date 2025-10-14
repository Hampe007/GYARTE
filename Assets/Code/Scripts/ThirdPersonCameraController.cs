using UnityEngine;
using Mirror;
using Unity.Cinemachine;

[DisallowMultipleComponent]
public class ThirdPersonCameraController : NetworkBehaviour
{
    [Header("Camera Target")]
    [Tooltip("Local target transform the camera follows (created at runtime if not set).")]
    [SerializeField] private Transform cameraTarget;

    [Header("Rotation")]
    [Tooltip("Clamp for vertical look in degrees.")]
    [SerializeField] private Vector2 pitchLimits = new Vector2(-60f, 75f);

    [Tooltip("Mouse/gamepad look sensitivity.")]
    [SerializeField] private Vector2 lookSensitivity = new Vector2(1.0f, 0.8f);

    [Header("Third Person Follow")]
    [Tooltip("Shoulder offset (X=right), Y=height, Z=back distance in meters.")]
    [SerializeField] private Vector3 shoulderOffset = new Vector3(0.6f, 1.6f, -3.5f);

    [Tooltip("Optional: add ThirdPersonAim for future ranged weapons.")]
    [SerializeField] private bool addThirdPersonAim = true;

    private float _yaw;
    private float _pitch;
    public float Yaw => _yaw;     // read-only for other components
    public float Pitch => _pitch; // read-only

    private CinemachineCamera _vcam;

    public override void OnStartAuthority()
    {
        try
        {
            enabled = true;

            if (cameraTarget == null)
            {
                var go = new GameObject("CameraTarget");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = Vector3.zero;
                cameraTarget = go.transform;
            }

            // Find or create a VCam
            var existing = Object.FindFirstObjectByType<CinemachineCamera>(FindObjectsInactive.Exclude);
            if (existing != null && existing.Follow == null)
            {
                _vcam = existing;
            }
            else
            {
                var v = new GameObject("CM vcam (Player)").AddComponent<CinemachineCamera>();
                _vcam = v;
            }

            _vcam.Follow = cameraTarget;
            _vcam.LookAt = cameraTarget;

            // Add ThirdPersonFollow module
            var follow = _vcam.GetComponent<CinemachineThirdPersonFollow>();
            if (follow == null) follow = _vcam.gameObject.AddComponent<CinemachineThirdPersonFollow>();
            follow.CameraDistance = Mathf.Abs(shoulderOffset.z);
            follow.ShoulderOffset = new Vector2(shoulderOffset.x, shoulderOffset.y);
            follow.VerticalArmLength = 0f; // we use shoulder offset for height
            follow.Damping = new Vector3(0.1f, 0.1f, 0.1f);

            if (addThirdPersonAim && _vcam.GetComponent<CinemachineThirdPersonAim>() == null)
                _vcam.gameObject.AddComponent<CinemachineThirdPersonAim>();

            // Ensure Main Camera has a CinemachineBrain
            var cam = Camera.main;
            if (cam != null && cam.GetComponent<CinemachineBrain>() == null)
                cam.gameObject.AddComponent<CinemachineBrain>();

            // Start at current facing
            _yaw = transform.eulerAngles.y;
            _pitch = 10f;
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }

    public void InjectLook(Vector2 lookDelta)
    {
        // Called by GamePlayerController (local owner only)
        try
        {
            _yaw += lookDelta.x * lookSensitivity.x;
            _pitch -= lookDelta.y * lookSensitivity.y;
            _pitch = Mathf.Clamp(_pitch, pitchLimits.x, pitchLimits.y);

            // Rotate the camera target (not the server-authoritative body)
            if (cameraTarget != null)
                cameraTarget.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }
        catch (System.Exception ex)
        {
            Debug.LogException(ex, this);
        }
    }
}