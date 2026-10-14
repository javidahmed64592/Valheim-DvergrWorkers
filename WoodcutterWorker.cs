using System.Collections.Generic;
using UnityEngine;

namespace DvergrWorkers
{
    /// <summary>
    /// Woodcutter role: each round it fells a few standing trees inside the job radius, chops the logs
    /// they leave, clears leftover stumps, picks up the wood and delivers it to the closest Worker Output Chest. It stops and
    /// says why on the bed if there is no output chest, the chests are full, or its own inventory is.
    /// </summary>
    public class WoodcutterWorker : WorkerBrain
    {
        private enum Phase { Plan, Walk, Windup, Recover, Pickup, Deliver }
        private enum TargetKind { Tree, Stump, Log, Drop }

        private const float WindupTime = 0.5f;
        private const float RecoverTime = 1.0f;
        private const float PickupTimeout = 3f;

        /// <summary>Deliver once this few inventory slots remain.</summary>
        private const int FullSlots = 2;

        private static readonly HashSet<string> WoodItems = new HashSet<string>
        {
            "$item_wood", "$item_finewood", "$item_corewood", "$item_elderbark", "$item_yggdrasilwood",
        };

        private Phase _phase = Phase.Plan;
        private TargetKind _kind;
        private GameObject _target;
        private float _timer;
        private bool _deliverFirst;
        private int _treesChopped;
        private int _stumpsCleared;
        private int _deliveredThisRound;
        private int _deliveredLastRound;
        private readonly HashSet<GameObject> _failed = new HashSet<GameObject>();

        internal override WorkerRole Role => WorkerRoles.Woodcutter;

        protected override string IdleStatus()
        {
            return _deliveredLastRound > 0
                ? "Idle (delivered " + _deliveredLastRound + " wood last round)"
                : "Idle";
        }

        protected override void OnRoundEnded(bool finished)
        {
            _target = null;
            _deliveredLastRound = _deliveredThisRound;
        }

        private static bool IsWood(ItemDrop.ItemData item)
        {
            return item != null && WoodItems.Contains(item.m_shared.m_name);
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
            _treesChopped = 0;
            _stumpsCleared = 0;
            _deliveredThisRound = 0;
            _target = null;
            _deliverFirst = CarryingWood();

            if (!_deliverFirst && !ChooseTarget(out _, out _))
            {
                _deliveredLastRound = 0;
                IdleNote = "No trees to chop nearby";
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
                return Abort("Inventory full");
            }

            if (carryingWood && (_deliverFirst || carried.GetEmptySlots() <= FullSlots))
            {
                _deliverFirst = false;
                return StartDelivery();
            }

            if (ChooseTarget(out GameObject target, out TargetKind kind))
            {
                if (Navigator.Begin(target.name, from => ReachPoint(target, kind, from)))
                {
                    _target = target;
                    _kind = kind;
                    _phase = Phase.Walk;
                    SetStatus(kind == TargetKind.Drop ? "Collecting wood"
                        : kind == TargetKind.Log ? "Walking to a log"
                        : kind == TargetKind.Stump ? "Walking to a stump"
                        : "Walking to a tree");
                }
                else
                {
                    DvergrWorkersPlugin.Log.LogInfo("No reachable spot next to " + target.name + ", skipping");
                    _failed.Add(target);
                }
                return true;
            }

            // Nothing left to cut or collect.
            if (carryingWood)
            {
                return StartDelivery();
            }
            EndRound(finished: true);
            return false;
        }

        /// <summary>Logs first, then loose wood, then the next standing tree. Nearest of each first.</summary>
        private bool ChooseTarget(out GameObject target, out TargetKind kind)
        {
            Vector3 from = transform.position;
            int toolTier = Equipment.ToolTier;

            target = null;
            kind = TargetKind.Log;
            float best = float.MaxValue;

            foreach (TreeLog log in Object.FindObjectsByType<TreeLog>(FindObjectsSortMode.None))
            {
                Consider(log.gameObject, log.m_minToolTier <= toolTier, from, ref best, ref target);
            }
            if (target != null)
            {
                return true;
            }

            kind = TargetKind.Drop;
            foreach (ItemDrop drop in ItemDrop.s_instances)
            {
                if (drop != null && IsWood(drop.m_itemData))
                {
                    Consider(drop.gameObject, true, from, ref best, ref target);
                }
            }
            if (target != null)
            {
                return true;
            }

            // Clear leftover stumps before felling anything new, so the area ends up tidier than it started.
            kind = TargetKind.Stump;
            if (_stumpsCleared < WorkerConfig.StumpsPerRound.Value)
            {
                foreach (Destructible stump in Object.FindObjectsByType<Destructible>(FindObjectsSortMode.None))
                {
                    if (IsStump(stump))
                    {
                        Consider(stump.gameObject, stump.m_minToolTier <= toolTier, from, ref best, ref target);
                    }
                }
                if (target != null)
                {
                    return true;
                }
            }

            kind = TargetKind.Tree;
            if (_treesChopped >= WorkerConfig.TreesPerRound.Value)
            {
                return false;
            }
            foreach (TreeBase tree in Object.FindObjectsByType<TreeBase>(FindObjectsSortMode.None))
            {
                Consider(tree.gameObject, tree.m_minToolTier <= toolTier, from, ref best, ref target);
            }
            return target != null;
        }

        /// <summary>Tree stumps are Destructibles of type Tree named like "Beech_Stub"; player pieces are never touched.</summary>
        private static bool IsStump(Destructible destructible)
        {
            if (destructible == null || destructible.m_destructibleType != DestructibleType.Tree
                || destructible.GetComponent<Piece>() != null)
            {
                return false;
            }
            string name = destructible.gameObject.name.ToLowerInvariant();
            return name.Contains("stub") || name.Contains("stump");
        }

        /// <summary>
        /// Where reach is measured to. Standing trees and stumps are measured to their trunk axis, because
        /// their colliders include the crown and would make a tree look close from far away.
        /// </summary>
        private static Vector3 ReachPoint(GameObject target, TargetKind kind, Vector3 from)
        {
            if (kind == TargetKind.Tree || kind == TargetKind.Stump)
            {
                Vector3 axis = target.transform.position;
                axis.y = Mathf.Clamp(from.y, axis.y, axis.y + 2f);
                return axis;
            }
            return WorkerNavigator.ClosestPointOnColliders(target, from);
        }

        private void Consider(GameObject go, bool usable, Vector3 from, ref float best, ref GameObject target)
        {
            if (!usable || !IsAlive(go) || _failed.Contains(go) || !InJobArea(go.transform.position))
            {
                return;
            }
            float d = Vector3.Distance(go.transform.position, from);
            if (d < best)
            {
                best = d;
                target = go;
            }
        }

        private static bool IsAlive(GameObject go)
        {
            return go != null && go.activeInHierarchy;
        }

        /// <summary>Ends the round and leaves the reason on the bed until the next round.</summary>
        private bool Abort(string reason)
        {
            DvergrWorkersPlugin.Log.LogInfo("Woodcutter stopping: " + reason);
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
                        SetStatus("Collecting wood");
                    }
                    else
                    {
                        _phase = Phase.Windup;
                        _timer = WindupTime;
                        SetStatus(_kind == TargetKind.Tree ? "Chopping a tree"
                            : _kind == TargetKind.Stump ? "Clearing a stump"
                            : "Chopping a log");
                    }
                    break;

                case NavResult.Failed:
                    _failed.Add(_target);
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

            var hit = new HitData
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
                _target = null;
                _phase = Phase.Plan;
                return true;
            }

            _timer += dt;
            if (_timer > PickupTimeout)
            {
                _failed.Add(_target);
                _target = null;
                _phase = Phase.Plan;
            }
            return true;
        }

        // ---------------------------------------------------------------- delivery

        private bool StartDelivery()
        {
            if (!CarryingWood())
            {
                _phase = Phase.Plan;
                return true;
            }

            if (!Logistics.BeginDeposit(IsWood))
            {
                return Abort(Logistics.Reason);
            }

            _phase = Phase.Deliver;
            SetStatus("Delivering wood to a chest");
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
            if (Logistics.Transferred == 0)
            {
                return Abort("Output chests are full");
            }

            // Still carrying wood: the chest filled up, so try the next closest one.
            if (CarryingWood())
            {
                return StartDelivery();
            }
            _phase = Phase.Plan;
            return true;
        }
    }
}
