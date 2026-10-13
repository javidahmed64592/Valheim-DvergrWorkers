using System.Collections.Generic;
using UnityEngine;

namespace DvergrWorkers
{
    /// <summary>
    /// Job loop for the repair Dvergr. It is driven from a Harmony prefix on MonsterAI.UpdateAI:
    /// while a job is running we take over movement, otherwise (idle, resting, fighting) the vanilla
    /// AI runs untouched so the Dvergr keeps its normal behaviour and defends itself.
    /// </summary>
    public class RepairWorker : MonoBehaviour
    {
        private static readonly Dictionary<GameObject, RepairWorker> Instances = new Dictionary<GameObject, RepairWorker>();

        public static bool TryGet(GameObject go, out RepairWorker worker)
        {
            return Instances.TryGetValue(go, out worker);
        }

        private enum Phase { Idle, Walking, Windup, Recover }

        private const string HammerPrefab = "Hammer";
        private const float JobRadiusTolerance = 3f;
        private const float WalkTimeout = 20f;
        private const float WindupTime = 0.5f;
        private const float RecoverTime = 1.1f;
        private const float FirstRoundDelay = 10f;
        private const float StandRingStep = 1.5f;
        private const int StandAngles = 8;
        private const float EyeHeight = 1.5f;

        private ZNetView _nview;
        private Humanoid _humanoid;
        private Character _character;
        private int _groundMask;

        private Vector3 _home;
        private bool _hasHome;

        private Phase _phase = Phase.Idle;
        private float _nextRound;
        private float _phaseTimer;
        private float _walkTimer;
        private float _threatTimer;
        private float _bedCheckTimer;
        private bool _threatened;
        private float _lastHealth;

        private readonly List<WearNTear> _queue = new List<WearNTear>();
        private readonly List<Vector3> _pathScratch = new List<Vector3>();
        private WearNTear _current;
        private Vector3 _standPoint;
        private ItemDrop.ItemData _hammer;
        private int _repairedThisRound;
        private int _repairedLastRound;
        private string _status = "";
        private bool _animLogged;
        private readonly List<ItemDrop.ItemData> _stashed = new List<ItemDrop.ItemData>();
        private float _restoreUntil;
        private float _stuckTimer;
        private Vector3 _stuckSamplePos;
        private int _stuckStrikes;
        private readonly HashSet<WearNTear> _failed = new HashSet<WearNTear>();

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
            _humanoid = GetComponent<Humanoid>();
            _character = GetComponent<Character>();
            _groundMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small");
            _nextRound = Time.time + FirstRoundDelay;
            Instances[gameObject] = this;
        }

        private void Start()
        {
            ApplyHome();
            if (_character != null)
            {
                _lastHealth = _character.GetHealth();
            }
        }

        private void OnDestroy()
        {
            Instances.Remove(gameObject);
        }

        /// <summary>Called by the bed right after it spawns this worker and writes the ZDO fields.</summary>
        public void OnBound()
        {
            ApplyHome();
        }

        private void ApplyHome()
        {
            if (_nview == null || !_nview.IsValid())
            {
                return;
            }

            Vector3 home = _nview.GetZDO().GetVec3(WorkerBed.HomeKey, Vector3.zero);
            if (home == Vector3.zero)
            {
                return;
            }

            _home = home;
            _hasHome = true;

            // Vanilla idle wandering is centred on m_spawnPoint within m_randomMoveRange.
            MonsterAI ai = GetComponent<MonsterAI>();
            if (ai != null)
            {
                ai.m_spawnPoint = home;
                ai.m_randomMoveRange = WorkerConfig.JobRadius.Value;
            }
        }

        /// <summary>Returns true if this frame's AI update was handled and the vanilla AI should be skipped.</summary>
        public bool Tick(MonsterAI ai, float dt)
        {
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner())
            {
                return false;
            }

            if (!_hasHome)
            {
                ApplyHome();
                if (!_hasHome)
                {
                    return false;
                }
            }

            if (!CheckBedStillExists(dt))
            {
                return false;
            }

            UpdateThreat(dt);
            bool tookDamage = _character.GetHealth() < _lastHealth - 0.01f;
            _lastHealth = _character.GetHealth();
            bool restTime = EnvMan.IsNight() && !WorkerConfig.WorkAtNight.Value;
            bool hasTarget = ai.GetTargetCreature() != null || ai.GetStaticTarget() != null;

            if (_restoreUntil > 0f)
            {
                RestoreWeapon();
            }

            if (_phase != Phase.Idle)
            {
                if (_threatened || tookDamage || restTime)
                {
                    EndRound(finished: false);
                    // Hand control straight back to the vanilla AI so it can defend or go to bed.
                    return false;
                }
                return RunJob(ai, dt);
            }

            if (restTime)
            {
                SetStatus("Resting");
            }
            else
            {
                SetStatus(_repairedLastRound > 0
                    ? "Idle (repaired " + _repairedLastRound + " pieces last round)"
                    : "Idle");
            }

            if (_threatened || tookDamage || hasTarget || ai.IsAlerted())
            {
                return false;
            }

            if (!restTime && Time.time >= _nextRound)
            {
                if (BeginRound())
                {
                    return RunJob(ai, dt);
                }
                _nextRound = Time.time + WorkerConfig.RepairInterval.Value;
                _repairedLastRound = 0;
                return false;
            }

            return Leash(ai, dt);
        }

        // ---------------------------------------------------------------- idle

        /// <summary>Keeps the idle worker inside its wander radius even if something pushed it out.</summary>
        private bool Leash(MonsterAI ai, float dt)
        {
            float limit = WorkerConfig.JobRadius.Value + 3f;
            if (Utils.DistanceXZ(transform.position, _home) <= limit)
            {
                return false;
            }

            SetStatus("Heading back to bed");
            ai.MoveTo(dt, _home, WorkerConfig.JobRadius.Value * 0.5f, false);
            return true;
        }

        private void UpdateThreat(float dt)
        {
            _threatTimer -= dt;
            if (_threatTimer > 0f)
            {
                return;
            }
            _threatTimer = 0.5f;

            _threatened = false;
            float range = WorkerConfig.ThreatRadius.Value;
            foreach (Character other in Character.GetAllCharacters())
            {
                if (other == _character || other.IsDead() || !BaseAI.IsEnemy(_character, other))
                {
                    continue;
                }
                if (Vector3.Distance(other.transform.position, transform.position) < range)
                {
                    _threatened = true;
                    return;
                }
            }
        }

        private bool CheckBedStillExists(float dt)
        {
            _bedCheckTimer -= dt;
            if (_bedCheckTimer > 0f)
            {
                return true;
            }
            _bedCheckTimer = 10f;

            ZDOID bedId = _nview.GetZDO().GetZDOID(WorkerBed.BedKey);
            if (bedId == ZDOID.None || ZDOMan.instance.GetZDO(bedId) != null)
            {
                return true;
            }

            // The bed was deconstructed: the worker leaves with it.
            DvergrWorkersPlugin.Log.LogInfo("Bed removed, despawning worker");
            if (_hammer != null)
            {
                _humanoid.GetInventory().RemoveItem(_hammer);
            }
            ZNetScene.instance.Destroy(gameObject);
            return false;
        }

        // ---------------------------------------------------------------- rounds

        private bool BeginRound()
        {
            _queue.Clear();
            _failed.Clear();
            _repairedThisRound = 0;

            float limit = WorkerConfig.JobRadius.Value + JobRadiusTolerance;
            foreach (WearNTear wnt in WearNTear.GetAllInstances())
            {
                if (!NeedsRepair(wnt))
                {
                    continue;
                }
                if (Utils.DistanceXZ(wnt.transform.position, _home) > limit)
                {
                    continue;
                }
                _queue.Add(wnt);
            }

            if (_queue.Count == 0)
            {
                return false;
            }

            EquipHammer();
            _current = null;
            _phase = Phase.Walking;
            DvergrWorkersPlugin.Log.LogInfo("Repair round started with " + _queue.Count + " damaged pieces");
            return true;
        }

        private void EndRound(bool finished)
        {
            _queue.Clear();
            _current = null;
            _phase = Phase.Idle;
            _repairedLastRound = _repairedThisRound;
            _nextRound = Time.time + WorkerConfig.RepairInterval.Value;
            _character.GetComponent<MonsterAI>()?.StopMoving();
            UnequipHammer();
            if (!finished)
            {
                DvergrWorkersPlugin.Log.LogInfo("Repair round interrupted");
            }
        }

        private static bool NeedsRepair(WearNTear wnt)
        {
            if (wnt == null || wnt.m_nview == null || !wnt.m_nview.IsValid())
            {
                return false;
            }
            Piece piece = wnt.GetComponent<Piece>();
            if (piece == null || !piece.IsPlacedByPlayer())
            {
                return false;
            }
            return wnt.GetHealthPercentage() < 0.999f;
        }

        // ---------------------------------------------------------------- job

        private bool RunJob(MonsterAI ai, float dt)
        {
            switch (_phase)
            {
                case Phase.Walking:
                    return Walk(ai, dt);

                case Phase.Windup:
                    ai.StopMoving();
                    FacePiece(ai);
                    _phaseTimer -= dt;
                    if (_phaseTimer <= 0f)
                    {
                        Hammer();
                        _phase = Phase.Recover;
                        _phaseTimer = RecoverTime;
                    }
                    return true;

                case Phase.Recover:
                    ai.StopMoving();
                    _phaseTimer -= dt;
                    if (_phaseTimer <= 0f)
                    {
                        _current = null;
                        _phase = Phase.Walking;
                    }
                    return true;
            }
            return false;
        }

        private bool Walk(MonsterAI ai, float dt)
        {
            if (_current == null && !PickNext())
            {
                EndRound(finished: true);
                return false;
            }

            if (!NeedsRepair(_current))
            {
                _current = null;
                return true;
            }

            if (InReach(_current, transform.position))
            {
                ai.StopMoving();
                _phase = Phase.Windup;
                _phaseTimer = WindupTime;
                return true;
            }

            _walkTimer += dt;

            // Path following first; once stuck, fall back to steering with obstacle avoidance.
            bool arrived = _stuckStrikes >= 1
                ? ai.MoveAndAvoid(dt, _standPoint, 0.6f, false)
                : ai.MoveTo(dt, _standPoint, 0.6f, false);

            _stuckTimer += dt;
            if (_stuckTimer >= 1.5f)
            {
                _stuckStrikes = Vector3.Distance(transform.position, _stuckSamplePos) < 0.4f ? _stuckStrikes + 1 : 0;
                _stuckSamplePos = transform.position;
                _stuckTimer = 0f;
            }

            if (arrived || _walkTimer > WalkTimeout || _stuckStrikes >= 3)
            {
                ai.StopMoving();

                // The repair itself works from any distance, so being nearly in reach is good enough.
                if (WithinTolerance(_current, transform.position))
                {
                    DvergrWorkersPlugin.Log.LogInfo("Stopped short of " + _current.name + " but close enough, hammering");
                    _phase = Phase.Windup;
                    _phaseTimer = WindupTime;
                    return true;
                }

                DvergrWorkersPlugin.Log.LogInfo("Could not reach " + _current.name + " (arrived=" + arrived
                    + ", stuck=" + _stuckStrikes + ") at " + transform.position + " -> stand " + _standPoint
                    + " (" + Vector3.Distance(transform.position, _standPoint).ToString("F1") + "m), pathNodes="
                    + ai.m_path.Count + ", onGround=" + _character.IsOnGround() + ", inAttack=" + _character.InAttack()
                    + ", skipping");
                _failed.Add(_current);
                _current = null;
            }
            return true;
        }

        /// <summary>Nearest-neighbour pick from the queue, choosing only pieces we can find a standing spot for.</summary>
        private bool PickNext()
        {
            while (_queue.Count > 0)
            {
                int best = -1;
                float bestDist = float.MaxValue;
                for (int i = 0; i < _queue.Count; i++)
                {
                    if (_queue[i] == null)
                    {
                        continue;
                    }
                    float d = Vector3.Distance(_queue[i].transform.position, transform.position);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = i;
                    }
                }

                if (best < 0)
                {
                    _queue.Clear();
                    return false;
                }

                WearNTear candidate = _queue[best];
                _queue.RemoveAt(best);

                if (!NeedsRepair(candidate) || _failed.Contains(candidate))
                {
                    continue;
                }

                if (FindStandPoint(candidate, out Vector3 stand))
                {
                    _current = candidate;
                    _standPoint = stand;
                    _walkTimer = 0f;
                    _stuckTimer = 0f;
                    _stuckStrikes = 0;
                    _stuckSamplePos = transform.position;
                    SetStatus("Walking to a damaged piece (" + (_queue.Count + 1) + " left)");
                    return true;
                }

                DvergrWorkersPlugin.Log.LogInfo("No reachable spot next to " + candidate.name + ", skipping");
            }
            return false;
        }

        // ---------------------------------------------------------------- pathing

        private static Vector3 Chest(Vector3 feet)
        {
            return feet + Vector3.up * EyeHeight;
        }

        /// <summary>Closest point on the piece's solid colliders (bounds for concave meshes, which can't be queried).</summary>
        private static Vector3 ClosestPointOnPiece(WearNTear wnt, Vector3 from)
        {
            Vector3 best = wnt.transform.position;
            float bestDist = float.MaxValue;
            foreach (Collider col in wnt.GetComponentsInChildren<Collider>())
            {
                if (col.isTrigger || !col.enabled)
                {
                    continue;
                }
                Vector3 p = col is MeshCollider mesh && !mesh.convex
                    ? col.bounds.ClosestPoint(from)
                    : col.ClosestPoint(from);
                float d = (p - from).sqrMagnitude;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = p;
                }
            }
            return best;
        }

        private static bool WithinTolerance(WearNTear wnt, Vector3 feet)
        {
            Vector3 chest = Chest(feet);
            return Vector3.Distance(chest, ClosestPointOnPiece(wnt, chest)) <= WorkerConfig.ReachDistance.Value * 1.75f;
        }

        private static bool InReach(WearNTear wnt, Vector3 feet)
        {
            Vector3 chest = Chest(feet);
            return Vector3.Distance(chest, ClosestPointOnPiece(wnt, chest)) <= WorkerConfig.ReachDistance.Value;
        }

        /// <summary>
        /// Samples ground positions in rings around the piece, keeps those that are both within hammer
        /// reach of it and reachable by a full navmesh path, and returns the one with the shortest walk.
        /// </summary>
        private bool FindStandPoint(WearNTear wnt, out Vector3 best)
        {
            best = Vector3.zero;
            MonsterAI ai = GetComponent<MonsterAI>();
            Vector3 target = ClosestPointOnPiece(wnt, transform.position);
            float reach = WorkerConfig.ReachDistance.Value;
            float bestLength = float.MaxValue;
            bool found = false;

            // Already close enough where we stand: no need to move.
            if (InReach(wnt, transform.position))
            {
                best = transform.position;
                return true;
            }

            // Pass 0 wants a full path ending in hammer reach. Pass 1 (fallback) accepts partial paths
            // and takes whichever end gets closest to the piece.
            float bestGap = float.MaxValue;
            for (int pass = 0; pass < 2 && !found; pass++)
            for (float radius = 0f; radius <= reach; radius += StandRingStep)
            {
                int angles = radius <= 0f ? 1 : StandAngles;
                for (int a = 0; a < angles; a++)
                {
                    float rad = a * Mathf.PI * 2f / angles;
                    Vector3 candidate = target + new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad)) * radius;

                    // Cast down from just above the piece so floors under roofs are found, not the roof.
                    if (!Physics.Raycast(candidate + Vector3.up * 1f, Vector3.down, out RaycastHit hit, 20f, _groundMask))
                    {
                        continue;
                    }
                    candidate = hit.point;

                    if (!Pathfinding.instance.GetPath(transform.position, candidate, _pathScratch, ai.m_pathAgentType, requireFullPath: pass == 0)
                        || _pathScratch.Count == 0)
                    {
                        continue;
                    }

                    // The navmesh may have snapped the goal elsewhere; judge reach from where we will really stand.
                    Vector3 end = _pathScratch[_pathScratch.Count - 1];
                    if (pass == 0 && !InReach(wnt, end))
                    {
                        continue;
                    }

                    if (pass == 1)
                    {
                        Vector3 chest = Chest(end);
                        float gap = Vector3.Distance(chest, ClosestPointOnPiece(wnt, chest));
                        if (gap < bestGap)
                        {
                            bestGap = gap;
                            best = end;
                            found = true;
                        }
                        continue;
                    }

                    float length = PathLength(_pathScratch);
                    if (length < bestLength)
                    {
                        bestLength = length;
                        best = end;
                        found = true;
                    }
                }
            }
            return found;
        }

        private static float PathLength(List<Vector3> path)
        {
            float total = 0f;
            for (int i = 1; i < path.Count; i++)
            {
                total += Vector3.Distance(path[i - 1], path[i]);
            }
            return total;
        }

        // ---------------------------------------------------------------- hammering

        private void FacePiece(MonsterAI ai)
        {
            if (_current == null)
            {
                return;
            }
            Vector3 dir = _current.transform.position - transform.position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f)
            {
                _character.SetLookDir(dir.normalized);
                ai.LookTowards(dir);
            }
        }

        private void Hammer()
        {
            if (!NeedsRepair(_current))
            {
                return;
            }

            if (_current.Repair())
            {
                _repairedThisRound++;
                SetStatus("Repairing (" + _queue.Count + " more queued)");

                Piece piece = _current.GetComponent<Piece>();
                if (piece != null && piece.m_placeEffect != null)
                {
                    piece.m_placeEffect.Create(piece.transform.position, piece.transform.rotation, null, 1f, -1, _character.GetZDOID());
                }
            }

            PlayHammerAnimation();
        }

        private void PlayHammerAnimation()
        {
            Animator animator = GetComponentInChildren<Animator>();
            LogAnimatorOnce(animator);

            string trigger = ResolveTrigger(animator);
            if (string.IsNullOrEmpty(trigger))
            {
                return;
            }
            _character.m_zanim.SetTrigger(trigger);
        }

        /// <summary>Config override, then the hammer's own trigger, then any attack-like trigger the Dvergr's animator has.</summary>
        private string ResolveTrigger(Animator animator)
        {
            string configured = WorkerConfig.RepairAnimationTrigger.Value;
            if (!string.IsNullOrEmpty(configured))
            {
                return configured;
            }

            string hammerTrigger = _hammer?.m_shared.m_attack.m_attackAnimation;
            if (animator == null || (!string.IsNullOrEmpty(hammerTrigger) && HasParameter(animator, hammerTrigger)))
            {
                return hammerTrigger;
            }

            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                if (p.type == AnimatorControllerParameterType.Trigger
                    && (p.name.Contains("attack") || p.name.Contains("swing")))
                {
                    return p.name;
                }
            }
            return null;
        }

        private void LogAnimatorOnce(Animator animator)
        {
            if (_animLogged || animator == null)
            {
                return;
            }
            _animLogged = true;

            var parameters = new List<string>();
            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                parameters.Add(p.name + ":" + p.type);
            }
            var clips = new List<string>();
            if (animator.runtimeAnimatorController != null)
            {
                foreach (AnimationClip c in animator.runtimeAnimatorController.animationClips)
                {
                    clips.Add(c.name);
                }
            }

            DvergrWorkersPlugin.Log.LogInfo("Dvergr animator parameters: " + string.Join(", ", parameters));
            DvergrWorkersPlugin.Log.LogInfo("Dvergr animator clips: " + string.Join(", ", clips));
            DvergrWorkersPlugin.Log.LogInfo("Hammer anim: trigger='" + _hammer?.m_shared.m_attack.m_attackAnimation
                + "' state=" + _hammer?.m_shared.m_animationState + ", chosen trigger='" + ResolveTrigger(animator) + "'");
        }

        private static bool HasParameter(Animator animator, string name)
        {
            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                if (p.name == name)
                {
                    return true;
                }
            }
            return false;
        }

        // ---------------------------------------------------------------- equipment

        private void EquipHammer()
        {
            Inventory inventory = _humanoid.GetInventory();
            _hammer = FindHammer(inventory);
            if (_hammer == null)
            {
                GameObject prefab = ObjectDB.instance.GetItemPrefab(HammerPrefab);
                if (prefab == null)
                {
                    DvergrWorkersPlugin.Log.LogError("Hammer item prefab not found");
                    return;
                }
                inventory.AddItem(prefab, 1);
                _hammer = FindHammer(inventory);
            }

            if (_hammer == null)
            {
                return;
            }

            // The hammer is a tool the worker carries for free and never wears out.
            _hammer.m_durability = _hammer.GetMaxDurability();

            _restoreUntil = 0f;

            // Clothes and armour stay on. Equipping a tool swaps the hands, but the Dvergr's crossbow
            // can sit in another slot, so anything weapon-like is stashed and remembered.
            _stashed.Clear();
            var slots = new[]
            {
                _humanoid.m_leftItem, _humanoid.m_rightItem, _humanoid.m_chestItem, _humanoid.m_legItem,
                _humanoid.m_helmetItem, _humanoid.m_shoulderItem, _humanoid.m_utilityItem, _humanoid.m_trinketItem,
                _humanoid.m_ammoItem
            };
            var dump = new List<string>();
            foreach (ItemDrop.ItemData item in slots)
            {
                if (item == null)
                {
                    continue;
                }
                dump.Add(item.m_shared.m_name + "(" + item.m_shared.m_itemType + ")");
                if (item.IsWeapon() || item.m_shared.m_name.ToLowerInvariant().Contains("crossbow"))
                {
                    _stashed.Add(item);
                }
            }
            DvergrWorkersPlugin.Log.LogInfo("Equipped slots: " + string.Join(", ", dump));
            foreach (ItemDrop.ItemData item in _stashed)
            {
                _humanoid.UnequipItem(item, false);
            }

            bool ok = _humanoid.EquipItem(_hammer, false);
            DvergrWorkersPlugin.Log.LogInfo("Equipped hammer: " + ok + ", stashed " + _stashed.Count + " item(s)");
        }

        private void UnequipHammer()
        {
            if (_hammer == null)
            {
                return;
            }
            _humanoid.UnequipItem(_hammer, false);
            _restoreUntil = Time.time + 5f;
            RestoreWeapon();
        }

        /// <summary>Re-equips everything stashed for the round; retried each frame for a few seconds if the Dvergr is busy.</summary>
        private void RestoreWeapon()
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

        private static ItemDrop.ItemData FindHammer(Inventory inventory)
        {
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                if (item.m_dropPrefab != null && item.m_dropPrefab.name == HammerPrefab)
                {
                    return item;
                }
            }
            return null;
        }

        // ---------------------------------------------------------------- status

        private void SetStatus(string status)
        {
            if (status == _status)
            {
                return;
            }
            _status = status;
            _nview.GetZDO().Set(WorkerBed.StatusKey, status);
        }
    }
}
