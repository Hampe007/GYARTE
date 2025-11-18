using UnityEngine;

namespace Characters.Status.Effects
{
    [CreateAssetMenu(menuName = "Status/Bleeding Effect", fileName = "BleedingEffect")]
    public sealed class BleedingEffect : StatusEffect
    {
        [Header("Bleed")]
        [Tooltip("Damage per second per stack.")]
        public float dps = 3f;

        private void OnEnable()
        {
            if (tickRate <= 0f) tickRate = 10f; // fine-grained tick by default
        }

        public override void OnTick(StatusController controller, float deltaTime)
        {
            var health = controller.GetComponentOnTarget<Characters.Health.Health>();
            if (health == null) return;
            float dmg = Mathf.Max(0f, dps) * deltaTime;
            health.ApplyDamage(dmg, controller.transform.position, Vector3.up, controller.gameObject, ignoreIFrames: true);
        }
    }
}
