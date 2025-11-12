using UnityEngine;
using Characters.Inputs;
using Characters.Combat;

namespace Characters.Equipment
{
    // Minimal equipment manager for 3 quick slots.
    // Switches WeaponConfig/AttackProfile on CharacterCombat when slot 1/2/3 is selected.
    public sealed class CharacterEquipment : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private InputReader input;
        [SerializeField] private CharacterCombat combat;

        [Header("Slots (1/2/3)")]
        [Tooltip("Weapon configs for each quick slot.")]
        [SerializeField] private WeaponConfig[] weaponSlots = new WeaponConfig[3];
        [Tooltip("Attack profiles for each quick slot.")]
        [SerializeField] private AttackProfile[] attackProfiles = new AttackProfile[3];

        [Header("Runtime")]
        [SerializeField] private int currentSlot = 0; // 0-based index

        public int CurrentSlot => currentSlot;

        private void Awake()
        {
            if (input == null) TryGetComponent(out input);
            if (combat == null) TryGetComponent(out combat);
            ApplySlot(currentSlot);
        }

        private void Update()
        {
            if (input == null) return;
            int sel = input.ConsumeSlotSelect();
            if (sel >= 1 && sel <= 3)
            {
                EquipSlot(sel - 1);
            }
        }

        public void EquipSlot(int index)
        {
            if (index < 0 || index >= 3) return;
            if (currentSlot == index) return;
            currentSlot = index;
            ApplySlot(currentSlot);
        }

        private void ApplySlot(int index)
        {
            if (combat == null) return;
            if (index < 0 || index >= 3) return;
            combat.SetConfigs(
                weapon: weaponSlots != null && weaponSlots.Length > index ? weaponSlots[index] : null,
                attack: attackProfiles != null && attackProfiles.Length > index ? attackProfiles[index] : null
            );
        }
    }
}

