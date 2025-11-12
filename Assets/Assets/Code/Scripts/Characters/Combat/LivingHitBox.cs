using UnityEngine;

namespace Characters.Combat
{
    // Marker component for valid damage targets. Put this on the collider(s)
    // that should receive weapon hits. Optionally reference the Health on the root.
    [DisallowMultipleComponent]
    [AddComponentMenu("Combat/Living Hitbox (Hurt Target)")]
    public sealed class LivingHitBox : MonoBehaviour
    {
        public enum HitZone
        {
            Generic,
            Head,
            Torso,
            Arm,
            Leg,
            Hand,
            Foot,
            Tail
        }

        [Tooltip("Health to route damage to. If null, searches in parents on first use.")]
        [SerializeField] private Characters.Health.Health health;

        [Header("Hit Zone")]
        [Tooltip("Semantic zone for this living hitbox (used for analytics/FX; optional).")]
        [SerializeField] private HitZone zone = HitZone.Generic;

        [Tooltip("If enabled, uses the owning HealthConfig's default multiplier for this zone. Disable to provide a custom value below.")]
        [SerializeField] private bool useDefaultMultiplier = true;

        [Tooltip("Damage multiplier applied when this hitbox is struck (e.g., Head 2.0, Torso 1.0, Limbs 0.75).")]
        [Min(0f)]
        [SerializeField] private float damageMultiplier = 1.0f;

        [Header("Debug")]
        [Tooltip("Optional team/faction id for friendly-fire rules (not used yet).")]
        [SerializeField] private int teamId = 0;

        public Characters.Health.Health Health
        {
            get
            {
                if (health == null) health = GetComponentInParent<Characters.Health.Health>();
                return health;
            }
        }

        public int TeamId => teamId;
        public HitZone Zone => zone;
        public float DamageMultiplier
        {
            get
            {
                if (useDefaultMultiplier)
                {
                    var cfg = Health != null ? Health.Config : null;
                    if (cfg != null)
                    {
                        return zone switch
                        {
                            HitZone.Head => Mathf.Max(0f, cfg.headMultiplier),
                            HitZone.Torso => Mathf.Max(0f, cfg.torsoMultiplier),
                            HitZone.Arm => Mathf.Max(0f, cfg.armMultiplier),
                            HitZone.Leg => Mathf.Max(0f, cfg.legMultiplier),
                            HitZone.Hand => Mathf.Max(0f, cfg.handMultiplier),
                            HitZone.Foot => Mathf.Max(0f, cfg.footMultiplier),
                            HitZone.Tail => Mathf.Max(0f, cfg.tailMultiplier),
                            _ => Mathf.Max(0f, cfg.genericMultiplier)
                        };
                    }
                }
                return Mathf.Max(0f, damageMultiplier);
            }
        }

        private void OnDrawGizmosSelected()
        {
            var col = GetComponent<Collider>();
            if (!col) return;
            Color zoneColor = zone switch
            {
                HitZone.Head => new Color(1f, 0.2f, 0.2f, 0.45f),
                HitZone.Torso => new Color(0.2f, 1f, 0.2f, 0.45f),
                HitZone.Arm => new Color(0.2f, 0.6f, 1f, 0.45f),
                HitZone.Leg => new Color(0.7f, 0.4f, 1f, 0.45f),
                HitZone.Hand => new Color(1f, 0.8f, 0.2f, 0.45f),
                HitZone.Foot => new Color(1f, 0.5f, 0.2f, 0.45f),
                HitZone.Tail => new Color(0.8f, 0.8f, 0.2f, 0.45f),
                _ => new Color(0.2f, 1f, 0.2f, 0.45f)
            };
            Gizmos.color = zoneColor;
            var bounds = col.bounds;
            Gizmos.DrawWireCube(bounds.center, bounds.size);
        }
    }
}
