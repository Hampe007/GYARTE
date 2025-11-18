using UnityEngine;

namespace Environment.Water
{
    // Simple water trigger with a flat surface at SurfaceY.
    // Make the collider a trigger. Position the object so SurfaceY matches water plane height.
    [RequireComponent(typeof(Collider))]
    public sealed class WaterVolume : MonoBehaviour
    {
        [Tooltip("Water surface world Y height. Defaults to this transform's Y.")]
        public float surfaceY;

        private void Reset()
        {
            var c = GetComponent<Collider>();
            c.isTrigger = true;
            surfaceY = transform.position.y;
        }

        public float SurfaceY => surfaceY == 0f ? transform.position.y : surfaceY;
    }
}

