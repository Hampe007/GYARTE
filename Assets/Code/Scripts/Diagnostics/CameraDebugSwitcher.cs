using UnityEngine;
using System.Collections;

public class CameraDebugSwitcher : MonoBehaviour
{
    [Header("Cameras")]
    public Camera normalCamera;
    public Camera debugCamera;
    
    [Header("Streamer")]
    public TileStreamCoordinator stream;

    [Header("Auto Player Detection")]
    public string playerTag = "Player";     // set your player prefab to this
    public bool keepSearching = true;       // keeps trying until found

    [Header("Settings")]
    public float debugHeight = 500f;
    public float moveSmooth = 5f;
    public KeyCode toggleKey = KeyCode.F8;
    public KeyCode radiusUpKey = KeyCode.Equals;
    public KeyCode radiusDownKey = KeyCode.Minus;

    private Transform followTarget;
    private bool debugMode;

    void Start()
    {
        debugCamera.enabled = false;
        StartCoroutine(FindPlayerRoutine());
    }

    IEnumerator FindPlayerRoutine()
    {
        while (followTarget == null)
        {
            // 1. Tagged player first (if used)
            var tagged = GameObject.FindGameObjectWithTag(playerTag);
            if (tagged != null)
            {
                followTarget = tagged.transform;
                yield break;
            }

            // 2. Mirror local player (if using Mirror)
#if MIRROR
            foreach (var ni in FindObjectsOfType<Mirror.NetworkIdentity>())
            {
                if (ni != null && ni.isLocalPlayer)
                {
                    followTarget = ni.transform;
                    yield break;
                }
            }
#endif

            // 3. Name-based fallback: GamePlayer, GamePlayer(Clone), etc.
            var objs = GameObject.FindObjectsOfType<GameObject>();
            for (int i = 0; i < objs.Length; i++)
            {
                var o = objs[i];
                if (o == null) continue;

                string n = o.name;

                if (n.StartsWith("GamePlayer", System.StringComparison.OrdinalIgnoreCase))
                {
                    followTarget = o.transform;
                    yield break;
                }
            }

            yield return new WaitForSeconds(0.25f);
        }
    }
    
    void Update()
    {
        // Allow late player spawns (optional)
        if (followTarget == null && keepSearching)
        {
            StartCoroutine(FindPlayerRoutine());
        }

        // Toggle camera
        if (Input.GetKeyDown(toggleKey))
        {
            debugMode = !debugMode;

            normalCamera.enabled = !debugMode;
            debugCamera.enabled = debugMode;
        }

        if (!debugMode) return;

        if (followTarget != null)
        {
            FollowFromAbove();
        }

        RadiusControl();
    }

    void FollowFromAbove()
    {
        Vector3 p = followTarget.position;
        Vector3 desired = new Vector3(p.x, debugHeight, p.z);

        debugCamera.transform.position = Vector3.Lerp(
            debugCamera.transform.position,
            desired,
            Time.deltaTime * moveSmooth
        );
    }

    void RadiusControl()
    {
        if (stream == null) return;

        if (Input.GetKey(radiusUpKey))
            stream.loadRadius += 5f;

        if (Input.GetKey(radiusDownKey))
            stream.loadRadius -= 5f;

        stream.loadRadius = Mathf.Max(20f, stream.loadRadius);
    }
}
