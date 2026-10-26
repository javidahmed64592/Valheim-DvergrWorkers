using System.Collections.Generic;

using UnityEngine;

namespace DvergrWorkers;

/// <summary>
/// Gives a worker its tool as a free, indestructible inventory item and holds it while working.
/// Anything weapon-like the Dvergr was carrying (including its crossbow, which sits in the Utility slot)
/// is stashed while the tool is out and put back afterwards. Clothes and armour are never touched.
/// </summary>
internal sealed class WorkerEquipment(Humanoid humanoid, string toolPrefab)
{
    private readonly Humanoid _humanoid = humanoid;
    private readonly string _toolPrefab = toolPrefab;
    private readonly List<ItemDrop.ItemData> _stashed = [];
    private float _restoreUntil;
    private bool _holding;

    public ItemDrop.ItemData Tool { get; private set; }

    /// <summary>Tier of the tool, available before it has been equipped (0 if there is no tool).</summary>
    public int ToolTier
    {
        get
        {
            if (Tool != null)
            {
                return Tool.m_shared.m_toolTier;
            }
            GameObject prefab = string.IsNullOrEmpty(_toolPrefab) ? null : ObjectDB.instance.GetItemPrefab(_toolPrefab);
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            return drop != null ? drop.m_itemData.m_shared.m_toolTier : 0;
        }
    }

    /// <summary>
    /// True for whatever a Dvergr fights with. The crossbow and the mages' staffs sit in the Utility slot and
    /// are not always typed as weapons (the staffs show up as "Club"), so that slot counts as a weapon too.
    /// </summary>
    public static bool IsHeldWeapon(Humanoid humanoid, ItemDrop.ItemData item)
    {
        return item.IsWeapon()
            || item == humanoid.m_utilityItem
            || item.m_shared.m_name.IndexOf("crossbow", System.StringComparison.InvariantCultureIgnoreCase) >= 0;
    }

    public void EquipTool()
    {
        // Roles without a tool (the machine tenders) still keep their hands free while they work.
        if (string.IsNullOrEmpty(_toolPrefab))
        {
            _restoreUntil = 0f;
            StashOtherWeapons();
            _holding = true;
            return;
        }

        Inventory inventory = _humanoid.GetInventory();
        Tool = FindTool(inventory);
        if (Tool == null)
        {
            GameObject prefab = ObjectDB.instance.GetItemPrefab(_toolPrefab);
            if (prefab == null)
            {
                DvergrWorkersPlugin.Log.LogError("Tool prefab not found: " + _toolPrefab);
                return;
            }
            _ = inventory.AddItem(prefab, 1);
            Tool = FindTool(inventory);
        }

        if (Tool == null)
        {
            return;
        }

        // Already out (a second task in the same round): just make sure nothing else is in hand.
        if (_holding && _humanoid.IsItemEquiped(Tool))
        {
            StashOtherWeapons();
            return;
        }

        // Tools are carried for free and never wear out.
        Tool.m_durability = Tool.GetMaxDurability();
        _restoreUntil = 0f;

        ItemDrop.ItemData[] slots =
        [
            _humanoid.m_leftItem, _humanoid.m_rightItem, _humanoid.m_chestItem, _humanoid.m_legItem,
            _humanoid.m_helmetItem, _humanoid.m_shoulderItem, _humanoid.m_utilityItem, _humanoid.m_trinketItem,
            _humanoid.m_ammoItem
        ];
        List<string> dump = [];
        foreach (ItemDrop.ItemData item in slots)
        {
            if (item == null)
            {
                continue;
            }
            dump.Add(item.m_shared.m_name + "(" + item.m_shared.m_itemType + ")");
            if (IsHeldWeapon(_humanoid, item) && !_stashed.Contains(item))
            {
                _stashed.Add(item);
            }
        }
        DvergrWorkersPlugin.Log.LogInfo("Equipped slots: " + string.Join(", ", dump));

        foreach (ItemDrop.ItemData item in _stashed)
        {
            _humanoid.UnequipItem(item, false);
        }
        bool ok = _humanoid.EquipItem(Tool, false);
        _holding = true;
        DvergrWorkersPlugin.Log.LogInfo("Equipped " + _toolPrefab + ": " + ok + ", stashed " + _stashed.Count + " item(s)");
    }

    /// <summary>Puts the tool away and starts restoring whatever was stashed.</summary>
    public void ReleaseTool()
    {
        if (!_holding)
        {
            return;
        }
        _holding = false;
        _humanoid.UnequipItem(Tool, false);
        _restoreUntil = Time.time + 5f;
        Restore();
    }

    /// <summary>
    /// Call every AI tick. While the tool is out, makes sure it really is in hand and nothing else is (the
    /// vanilla AI can swap a staff or crossbow back in); otherwise finishes a restore that did not succeed
    /// straight away, but not while the worker lies in bed.
    /// </summary>
    public void Update(bool resting)
    {
        if (_holding)
        {
            if (Tool != null && !_humanoid.IsItemEquiped(Tool))
            {
                _ = _humanoid.EquipItem(Tool, false);
            }
            StashOtherWeapons();
        }
        else if (_restoreUntil > 0f && !resting)
        {
            Restore();
        }
    }

    /// <summary>Puts away anything weapon-like in hand other than the tool, to be restored with the rest afterwards.</summary>
    private void StashOtherWeapons()
    {
        foreach (ItemDrop.ItemData item in new[] { _humanoid.m_rightItem, _humanoid.m_leftItem, _humanoid.m_utilityItem })
        {
            if (item != null && item != Tool && IsHeldWeapon(_humanoid, item))
            {
                if (!_stashed.Contains(item))
                {
                    _stashed.Add(item);
                }
                _humanoid.UnequipItem(item, false);
            }
        }
    }

    /// <summary>Removes the tool from the inventory entirely (the worker is being despawned).</summary>
    public void Discard()
    {
        _holding = false;
        if (Tool != null)
        {
            _ = _humanoid.GetInventory().RemoveItem(Tool);
            Tool = null;
        }
    }

    private void Restore()
    {
        for (int i = _stashed.Count - 1; i >= 0; i--)
        {
            ItemDrop.ItemData item = _stashed[i];
            if (!_humanoid.GetInventory().ContainsItem(item))
            {
                _stashed.RemoveAt(i);
                continue;
            }

            // Equipping refuses worn-out items; the worker's gear never degrades.
            if (item.m_shared.m_useDurability && item.m_durability <= 0f)
            {
                item.m_durability = item.GetMaxDurability();
            }

            if (_humanoid.EquipItem(item, false))
            {
                _stashed.RemoveAt(i);
            }
        }

        if (_stashed.Count == 0 || Time.time > _restoreUntil)
        {
            DvergrWorkersPlugin.Log.LogInfo(_stashed.Count == 0
                ? "Restored stashed equipment"
                : "Failed to restore " + _stashed.Count + " item(s), e.g. " + _stashed[0].m_shared.m_name);
            _stashed.Clear();
            _restoreUntil = 0f;
        }
    }

    private ItemDrop.ItemData FindTool(Inventory inventory)
    {
        foreach (ItemDrop.ItemData item in inventory.GetAllItems())
        {
            if (item.m_dropPrefab != null && item.m_dropPrefab.name == _toolPrefab)
            {
                return item;
            }
        }
        return null;
    }
}
