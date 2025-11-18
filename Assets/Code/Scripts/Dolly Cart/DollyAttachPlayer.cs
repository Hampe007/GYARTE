using UnityEngine;

public class DollyAttachPlayer : MonoBehaviour
{
    public Transform cartTransform;
    public Behaviour[] componentsToDisableDuringRun;
    public MonoBehaviour runner;  // Assign DollyPathRun
    public Vector3 localPositionOffset;
    public Vector3 localEulerOffset;

    bool _disabled;

    bool RunnerIsActive()
    {
        if (runner == null) return false;

        // Interface-free duck typing to avoid hard dependency on a specific runner
        var prop = runner.GetType().GetProperty("IsRunning");
        return prop != null && prop.PropertyType == typeof(bool) && (bool)prop.GetValue(runner);
    }

    void LateUpdate()
    {
        if (cartTransform == null || runner == null) return;

        bool active = RunnerIsActive();

        if (active && !_disabled)
        {
            if (componentsToDisableDuringRun != null)
                foreach (var b in componentsToDisableDuringRun) if (b) b.enabled = false;
            _disabled = true;
        }
        else if (!active && _disabled)
        {
            if (componentsToDisableDuringRun != null)
                foreach (var b in componentsToDisableDuringRun) if (b) b.enabled = true;
            _disabled = false;
        }

        transform.SetPositionAndRotation(cartTransform.position, cartTransform.rotation);
        transform.Translate(localPositionOffset, Space.Self);
        transform.Rotate(localEulerOffset, Space.Self);
    }
}