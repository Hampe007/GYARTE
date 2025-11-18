using System;
using System.Collections.Generic;
using UnityEngine;

namespace Characters.Combat
{
    [Serializable]
    public sealed class ResistanceEntry
    {
        [Tooltip("Damage type this entry applies to.")]
        public DamageType type;
        [Range(0f, 2f)]
        [Tooltip("Multiplier for incoming damage of this type (0=immune, 0.5=50% resist, 1=normal, >1=weakness).")]
        public float multiplier = 1f;
    }

    // Optional component attached to a character to scale incoming damage by type.
    [AddComponentMenu("Combat/Damage Resistances")]
    public sealed class DamageResistances : MonoBehaviour
    {
        [Header("Resistances")]
        [Tooltip("Per-type damage multipliers applied to Health before damage is taken.")]
        public List<ResistanceEntry> resistances = new();

        public float GetMultiplier(DamageType type)
        {
            if (type == null) return 1f;
            for (int i = 0; i < resistances.Count; i++)
            {
                var e = resistances[i];
                if (e.type == type) return Mathf.Clamp(e.multiplier, 0f, 100f);
            }
            return 1f;
        }
    }
}
