using System.Collections.Generic;

using DvergrWorkers.Core;
using DvergrWorkers.Logistics;
using DvergrWorkers.Movement;
using DvergrWorkers.Roles.Farmer;

using UnityEngine;

namespace DvergrWorkers.Roles.Woodcutter;

/// <summary>
/// Woodcutter role: each round it fells a few standing trees inside the job radius, chops the logs
/// they leave, clears leftover stumps, picks up the wood and delivers it to the closest Worker Output Chest. It stops and
/// says why on the bed if there is no output chest, the chests are full, or its own inventory is.
/// </summary>
public class WoodcutterWorker : WorkerBrain
{
    private enum Phase { Plan = 0, Walk = 1, Windup = 2, Recover = 3, Pickup = 4, Deliver = 5 }
    private enum TargetKind { Tree = 0, Stump = 1, Log = 2, Drop = 3 }

    private const float WindupTime = 0.5f;
    private const float RecoverTime = 1.0f;
    private const float PickupTimeout = 3f;

    /// <summary>Deliver once this few inventory slots remain.</summary>
    private const int FullSlots = 2;

    /// <summary>
    /// Target choice is by distance, nudged so the worker finishes what it started (logs and wood lying about count
    /// as this much closer) and fells trees before wandering off to clear stumps (which count as this much further).
    /// </summary>
    private const float CleanupBonus = 6f;
    private const float StumpPenalty = 12f;

    /// <summary>A target with no standing spot found yet is tried again after this long, while the navmesh catches up.</summary>
    private const float RetryTargetAfter = 15f;

    /// <summary>When a round stops at TreesPerRound with trees still standing, the next one starts this soon.</summary>
    private const float MoreTreesDelay = 15f;

    private static readonly HashSet<string> WoodItems =
    [
        "$item_wood", "$item_finewood", "$item_corewood", "$item_elderbark", "$item_yggdrasilwood",
        "$item_resin", "$item_feathers",
    ];

    private Phase _phase = Phase.Plan;
    private TargetKind _kind;
    private GameObject _target;
    private float _timer;
    private bool _deliverFirst;
    private int _treesChopped;
    private int _stumpsCleared;
    private int _deliveredThisRound;
    private int _deliveredLastRound;
    private bool _deliveryBlocked;
    private readonly HashSet<GameObject> _failed = [];
    private readonly Dictionary<GameObject, float> _skipUntil = [];

    internal override WorkerRole Role => WorkerRoles.Woodcutter;

    protected override void Awake()
    {
        base.Awake();

        // Make sure the tree seeds are registered as this role's output before anyone asks for them.
        _ = TreeSeeds();
    }

    protected override string IdleStatus()
    {
        return _deliveredLastRound > 0
            ? Text.Of("$dw_status_idle_delivered_wood", _deliveredLastRound)
            : "$dw_status_idle";
    }

    protected override void OnRoundEnded(bool finished)
    {
        _target = null;
        _deliveredLastRound = _deliveredThisRound;
    }

    /// <summary>Wood, plus the resin, feathers, seeds, cones and acorns felled trees drop (the tree farmer plants those).</summary>
    private static bool IsWood(ItemDrop.ItemData item)
    {
        if (item == null)
        {
            return false;
        }
        string name = item.m_shared.m_name;
        return WoodItems.Contains(name) || TreeSeeds().Contains(name);
    }

    private static HashSet<string> _treeSeeds;

    private static HashSet<string> TreeSeeds()
    {
        if (_treeSeeds == null)
        {
            _treeSeeds = PlantCatalogue.TreeSeedNames;

            // Tell the other workers that the woodcutter is a source of these.
            WorkerRoles.Woodcutter.Produces.UnionWith(_treeSeeds);
        }
        return _treeSeeds;
    }

    private bool CarryingWood()
    {
        foreach (ItemDrop.ItemData item in Logistics.Carried.GetAllItems())
        {
            if (IsWood(item))
            {
                return true;
            }
        }
        return false;
    }

    // ---------------------------------------------------------------- rounds

    protected override bool BeginRound()
    {
        _failed.Clear();
        _skipUntil.Clear();
        _deliveryBlocked = false;
        _treesChopped = 0;
        _stumpsCleared = 0;
        _deliveredThisRound = 0;
        _target = null;
        _deliverFirst = CarryingWood();

        if (!_deliverFirst && !ChooseTarget(out _, out _))
        {
            _deliveredLastRound = 0;
            IdleNote = "$dw_status_no_trees";
            return false;
        }

        Equipment.EquipTool();
        _phase = Phase.Plan;
        DvergrWorkersPlugin.Log.LogInfo("Woodcutter round started");
        return true;
    }

    protected override bool RunTask(float dt)
    {
        switch (_phase)
        {
            case Phase.Plan:
                return Plan();

            case Phase.Walk:
                return Walk(dt);

            case Phase.Windup:
                Ai.StopMoving();
                Face();
                _timer -= dt;
                if (_timer <= 0f)
                {
                    Swing();
                    _phase = Phase.Recover;
                    _timer = RecoverTime;
                }
                return true;

            case Phase.Recover:
                Ai.StopMoving();
                _timer -= dt;
                if (_timer <= 0f)
                {
                    if (!IsAlive(_target))
                    {
                        if (_kind == TargetKind.Tree)
                        {
                            _treesChopped++;
                        }
                        else if (_kind == TargetKind.Stump)
                        {
                            _stumpsCleared++;
                        }
                        _target = null;
                        _phase = Phase.Plan;
                    }
                    else
                    {
                        _phase = Phase.Windup;
                        _timer = WindupTime;
                    }
                }
                return true;

            case Phase.Pickup:
                return PickUp(dt);

            case Phase.Deliver:
                return Deliver(dt);
        }
        return false;
    }

    // ---------------------------------------------------------------- planning

    private bool Plan()
    {
        Inventory carried = Logistics.Carried;
        bool carryingWood = CarryingWood();

        if (!carryingWood && carried.GetEmptySlots() == 0)
        {
            return Abort("$dw_reason_inventory_full");
        }

        bool low = carried.GetEmptySlots() <= FullSlots;
        if (carryingWood && (low || (_deliverFirst && !_deliveryBlocked)))
        {
            _deliverFirst = false;
            return StartDelivery(canWait: !low);
        }

        if (ChooseTarget(out GameObject target, out TargetKind kind))
        {
            if (Navigator.Begin(target.name, from => ReachPoint(target, kind, from), rememberFailure: true))
            {
                _target = target;
                _kind = kind;
                _phase = Phase.Walk;
                SetStatus(kind switch
                {
                    TargetKind.Drop => "$dw_status_collecting_wood",
                    TargetKind.Log => "$dw_status_walking_to_log",
                    TargetKind.Stump => "$dw_status_walking_to_stump",
                    _ => "$dw_status_walking_to_tree",
                });
            }
            else
            {
                // Often just the navmesh there not being built yet: look again in a little while.
                DvergrWorkersPlugin.Log.LogInfo("No reachable spot next to " + target.name + " yet, trying others first");
                _skipUntil[target] = Time.time + RetryTargetAfter;
            }
            return true;
        }

        // Nothing left to cut or collect.
        if (carryingWood)
        {
            return StartDelivery(canWait: false);
        }
        bool moreTrees = _treesChopped >= WorkerConfig.TreesPerRound.Value;
        EndRound(finished: true);
        if (moreTrees)
        {
            StartNextRoundWithin(MoreTreesDelay);
        }
        return false;
    }

    /// <summary>
    /// The closest thing worth doing: a log, wood lying on the ground, a standing tree or a stump. Logs and loose
    /// wood count as a little closer so the worker finishes what it felled; stumps as further, so it fells trees
    /// before going off to tidy up.
    /// </summary>
    private bool ChooseTarget(out GameObject target, out TargetKind kind)
    {
        Vector3 from = transform.position;
        int toolTier = Equipment.ToolTier;

        target = null;
        kind = TargetKind.Tree;
        float best = float.MaxValue;

        foreach (TreeLog log in Scene.Logs.All)
        {
            Consider(log.gameObject, TargetKind.Log, log.m_minToolTier <= toolTier, from, -CleanupBonus, ref best, ref target, ref kind);
        }
        foreach (ItemDrop drop in ItemDrop.s_instances)
        {
            if (drop != null && IsWood(drop.m_itemData))
            {
                Consider(drop.gameObject, TargetKind.Drop, true, from, -CleanupBonus, ref best, ref target, ref kind);
            }
        }
        if (_stumpsCleared < WorkerConfig.StumpsPerRound.Value)
        {
            foreach (Destructible stump in Scene.Stumps.All)
            {
                Consider(stump.gameObject, TargetKind.Stump, stump.m_minToolTier <= toolTier, from, StumpPenalty, ref best, ref target, ref kind);
            }
        }
        if (_treesChopped < WorkerConfig.TreesPerRound.Value)
        {
            foreach (TreeBase tree in Scene.Trees.All)
            {
                Consider(tree.gameObject, TargetKind.Tree, tree.m_minToolTier <= toolTier, from, 0f, ref best, ref target, ref kind);
            }
        }
        return target != null;
    }

    /// <summary>
    /// Where reach is measured to. Standing trees and stumps are measured to their trunk axis, because
    /// their colliders include the crown and would make a tree look close from far away.
    /// </summary>
    private static Vector3 ReachPoint(GameObject target, TargetKind kind, Vector3 from)
    {
        if (kind is TargetKind.Tree or TargetKind.Stump)
        {
            Vector3 axis = target.transform.position;
            axis.y = Mathf.Clamp(from.y, axis.y, axis.y + 2f);
            return axis;
        }
        return WorkerNavigator.ClosestPointOnColliders(target, from);
    }

    private void Consider(GameObject go, TargetKind candidateKind, bool usable, Vector3 from, float bias,
        ref float best, ref GameObject target, ref TargetKind kind)
    {
        if (!usable || !IsAlive(go) || _failed.Contains(go) || !InJobArea(go.transform.position)
            || (_skipUntil.TryGetValue(go, out float until) && Time.time < until))
        {
            return;
        }
        float score = Vector3.Distance(go.transform.position, from) + bias;
        if (score < best)
        {
            best = score;
            target = go;
            kind = candidateKind;
        }
    }

    private static bool IsAlive(GameObject go)
    {
        return go != null && go.activeInHierarchy;
    }

    /// <summary>Ends the round and leaves the reason on the bed until the next round.</summary>
    private bool Abort(string reason)
    {
        DvergrWorkersPlugin.Log.LogInfo("Woodcutter stopping: " + Text.Show(reason));
        IdleNote = reason;
        EndRound(finished: false);
        return false;
    }

    // ---------------------------------------------------------------- walking and chopping

    private bool Walk(float dt)
    {
        if (!IsAlive(_target))
        {
            _target = null;
            _phase = Phase.Plan;
            return true;
        }

        switch (Navigator.Step(dt))
        {
            case NavResult.InReach:
            case NavResult.CloseEnough:
                if (_kind == TargetKind.Drop)
                {
                    _phase = Phase.Pickup;
                    _timer = 0f;
                    SetStatus("$dw_status_collecting_wood");
                }
                else
                {
                    _phase = Phase.Windup;
                    _timer = WindupTime;
                    SetStatus(_kind switch
                    {
                        TargetKind.Tree => "$dw_status_chopping_tree",
                        TargetKind.Stump => "$dw_status_clearing_stump",
                        _ => "$dw_status_chopping_log",
                    });
                }
                break;

            case NavResult.Failed:
                _ = _failed.Add(_target);
                _target = null;
                _phase = Phase.Plan;
                break;
        }
        return true;
    }

    private void Face()
    {
        if (!IsAlive(_target))
        {
            return;
        }
        Vector3 dir = _target.transform.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.01f)
        {
            Character.SetLookDir(dir.normalized);
            Ai.LookTowards(dir);
        }
    }

    private void Swing()
    {
        if (!IsAlive(_target))
        {
            return;
        }

        ItemDrop.ItemData tool = Equipment.Tool;
        Vector3 dir = _target.transform.position - transform.position;
        dir.y = 0f;

        HitData hit = new()
        {
            m_damage = tool != null ? tool.GetDamage() : new HitData.DamageTypes { m_chop = 20f },
            m_toolTier = (short)Equipment.ToolTier,
            m_point = WorkerNavigator.ClosestPointOnColliders(_target, Character.GetEyePoint()),
            m_dir = dir.normalized,
        };
        hit.SetAttacker(Character);

        _target.GetComponent<IDestructible>()?.Damage(hit);
        Animation.PlaySwing();
    }

    private bool PickUp(float dt)
    {
        ItemDrop drop = IsAlive(_target) ? _target.GetComponent<ItemDrop>() : null;
        if (drop == null)
        {
            _target = null;
            _phase = Phase.Plan;
            return true;
        }

        // Drops are owned by whoever last touched them; ask for ownership and retry for a moment.
        drop.RequestOwn();
        if (Humanoid.Pickup(_target, false, false))
        {
            Animation.PlayPickup();
            _target = null;
            _phase = Phase.Plan;
            return true;
        }

        _timer += dt;
        if (_timer > PickupTimeout)
        {
            _ = _failed.Add(_target);
            _target = null;
            _phase = Phase.Plan;
        }
        return true;
    }

    // ---------------------------------------------------------------- delivery

    /// <summary>
    /// Heads for a chest with the wood. If no chest will take it right now and <paramref name="canWait"/> (there is
    /// still room in the inventory), the worker keeps the wood and carries on cutting, and tries again once its
    /// inventory is nearly full, rather than giving up on the whole round.
    /// </summary>
    private bool StartDelivery(bool canWait)
    {
        if (!CarryingWood())
        {
            _phase = Phase.Plan;
            return true;
        }

        if (!Logistics.BeginDeposit(IsWood))
        {
            if (canWait)
            {
                DvergrWorkersPlugin.Log.LogInfo("Woodcutter keeps its wood for now: " + Text.Show(Logistics.Reason));
                _deliveryBlocked = true;
                _phase = Phase.Plan;
                return true;
            }
            return Abort(Logistics.Reason);
        }

        _phase = Phase.Deliver;
        SetStatus("$dw_status_delivering_wood");
        return true;
    }

    private bool Deliver(float dt)
    {
        switch (Logistics.Step(dt))
        {
            case LogisticsResult.Moving:
                return true;

            case LogisticsResult.Failed:
                return Abort(Logistics.Reason);
        }

        _deliveredThisRound += Logistics.Transferred;
        bool low = Logistics.Carried.GetEmptySlots() <= FullSlots;
        if (Logistics.Transferred == 0)
        {
            if (!low)
            {
                _deliveryBlocked = true;
                _phase = Phase.Plan;
                return true;
            }
            return Abort("$dw_reason_output_full");
        }

        // Still carrying wood (another kind, or the chest filled up): try the next chest for it.
        if (CarryingWood())
        {
            return StartDelivery(canWait: !low);
        }
        _deliveryBlocked = false;
        _phase = Phase.Plan;
        return true;
    }
}
