using UnityEngine;

namespace Characters.Combat
{
    // Minimal damage surface interface; implement on Health component later.
    public interface IDamageable
    {
        void ApplyDamage(float amount, Vector3 hitPoint, Vector3 hitNormal, GameObject source);
    }
}

