using System;
using System.Collections.Generic;
using UnityEngine;

namespace InventorySystem
{
    [Serializable]
    public sealed class EquipmentSlot
    {
        [Tooltip("Slot name (e.g., Weapon, Armor, Trinket).")]
        public string slotName; // e.g. Weapon, Armor, Trinket
        [Tooltip("Item currently equipped in this slot.")]
        public ItemDefinition equipped;
    }

    [AddComponentMenu("Inventory/Equipment Slots")]
    public sealed class EquipmentSlots : MonoBehaviour
    {
        [Header("Slots")] 
        [Tooltip("All equipment slots on this character and their current items.")]
        public List<EquipmentSlot> slots = new();

        [Tooltip("Raised when equipment changes (equip/unequip).")]
        public event Action OnChanged;
        public void NotifyChanged() => OnChanged?.Invoke();

        public bool Equip(string slot, ItemDefinition item)
        {
            var s = slots.Find(x => x.slotName == slot);
            if (s == null) { s = new EquipmentSlot { slotName = slot }; slots.Add(s); }
            s.equipped = item;
            NotifyChanged();
            return true;
        }

        public ItemDefinition GetEquipped(string slot)
        {
            var s = slots.Find(x => x.slotName == slot);
            return s != null ? s.equipped : null;
        }
    }
}
