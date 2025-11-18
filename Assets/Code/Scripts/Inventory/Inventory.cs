using System;
using System.Collections.Generic;
using UnityEngine;

namespace InventorySystem
{
    [Serializable]
    public sealed class ItemStack
    {
        public ItemDefinition item;
        public int count;
    }

    public enum InventoryMode { DynamicList, SlotBased /* reserved */ }

    [AddComponentMenu("Inventory/Inventory (Dynamic List)")]
    public sealed class Inventory : MonoBehaviour
    {
        [Header("Mode")]
        [Tooltip("Inventory structure. Dynamic List mode keeps a flexible list of items. Slot-based may be added later.")]
        public InventoryMode mode = InventoryMode.DynamicList;

        [Header("Items")]
        [Tooltip("Current items (each entry is a separate stack).")]
        public List<ItemStack> items = new();

        [Tooltip("Raised whenever the inventory content changes (added/removed/stacked).")]
        public event Action OnChanged;
        public void NotifyChanged() => OnChanged?.Invoke();

        public bool Add(ItemDefinition item, int count = 1)
        {
            if (item == null || count <= 0) return false;
            if (mode != InventoryMode.DynamicList) return false;
            if (item.stackable)
            {
                var stack = items.Find(s => s.item == item);
                if (stack != null)
                {
                    int newCount = Mathf.Min(item.maxStack, stack.count + count);
                    if (newCount != stack.count) { stack.count = newCount; OnChanged?.Invoke(); return true; }
                    return false;
                }
            }
            items.Add(new ItemStack { item = item, count = Mathf.Clamp(count, 1, item.stackable ? item.maxStack : 1) });
            NotifyChanged();
            return true;
        }

        public bool Remove(ItemDefinition item, int count = 1)
        {
            var stack = items.Find(s => s.item == item);
            if (stack == null) return false;
            stack.count -= count;
            if (stack.count <= 0) items.Remove(stack);
            NotifyChanged();
            return true;
        }
    }
}
