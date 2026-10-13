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
        private ItemDrop.ItemData _savedLeft;
        private ItemDrop.ItemData _savedRight;
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
                ai.m_randomMoveRange = WorkerConfig.WanderRadius.Value;
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
            float limit = WorkerConfig.WanderRadius.Value + 3f;
            if (Utils.DistanceXZ(transform.position, _home) <= limit)
            {
                return false;
            }

            SetStatus("Heading back to bed");
            ai.MoveTo(dt, _home, WorkerConfig.WanderRadius.Value * 0.5f, false);
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
            bool arrived = ai.MoveTo(dt, _standPoint, 0.6f, false);

            // Stuck detection: if we barely moved over a few seconds, give up on this piece.
            _stuckTimer += dt;
            if (_stuckTimer >= 1.5f)
            {
                _stuckStrikes = Vector3.Distance(transform.position, _stuckSamplePos) < 0.4f ? _stuckStrikes + 1 : 0;
                _stuckSamplePos = transform.position;
                _stuckTimer = 0f;
            }

            if (arrived || _walkTimer > WalkTimeout || _stuckStrikes >= 2)
            {
                // Could not get close enough along this path: give up on this piece for the round.
                DvergrWorkersPlugin.Log.LogInfo("Could not reach " + _current.name + " (arrived=" + arrived
                    + ", stuck=" + _stuckStrikes + ") at " + transform.position + " -> stand " + _standPoint
                    + " (" + Vector3.Distance(transform.position, _standPoint).ToString("F1") + "m), pathNodes="
                    + ai.m_path.Count + ", onGround=" + _character.IsOnGround() + ", inAttack=" + _character.InAttack()
                    + ", skipping");
                ai.StopMoving();
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

                    if (!Pathfinding.instance.GetPath(transform.position, candidate, _pathScratch, ai.m_pathAgentType, requireFullPath: true)
                        || _pathScratch.Count == 0)
                    {
                        continue;
                    }

                    // The navmesh may have snapped the goal elsewhere; judge reach from where we will really stand.
                    Vector3 end = _pathScratch[_pathScratch.Count - 1];
                    if (!InReach(wnt, end))
                    {
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

            // Remember what the Dvergr was holding so it can be put back exactly afterwards.
            _savedLeft = _humanoid.m_leftItem;
            _savedRight = _humanoid.m_rightItem;
            _restoreUntil = 0f;

            // Equipping a tool already swaps out whatever is in the hands; armour and clothes stay on.
            bool ok = _humanoid.EquipItem(_hammer, false);
            DvergrWorkersPlugin.Log.LogInfo("Equipped hammer: " + ok + " (saved left="
                + _savedLeft?.m_shared.m_name + ", right=" + _savedRight?.m_shared.m_name + ")");
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

        /// <summary>Re-equips the saved weapon; retried each frame for a few seconds in case the Dvergr is mid-animation.</summary>
        private void RestoreWeapon()
        {
            if (_humanoid.m_leftItem != null || _humanoid.m_rightItem != null)
            {
                _restoreUntil = 0f;
                return;
            }

            if (_savedLeft == null && _savedRight == null)
            {
                // The Dvergr wasn't holding anything before the round, so there is nothing to put back.
                _restoreUntil = 0f;
                return;
            }

            bool any = false;
            foreach (ItemDrop.ItemData item in new[] { _savedLeft, _savedRight })
            {
                if (item != null && _humanoid.GetInventory().ContainsItem(item) && _humanoid.EquipItem(item, false))
                {
                    any = true;
                }
            }

            if (!any)
            {
                // Same call the vanilla AI makes on spawn.
                _humanoid.EquipBestWeapon(null, null, null, null);
                any = _humanoid.m_leftItem != null || _humanoid.m_rightItem != null;
            }

            if (any || Time.time > _restoreUntil)
            {
                DvergrWorkersPlugin.Log.LogInfo("Weapon restored: " + any);
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
