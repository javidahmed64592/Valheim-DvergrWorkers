using System.Collections.Generic;
using UnityEngine;

namespace DvergrWorkers
{
    /// <summary>
    /// Behaviour shared by every worker role: staying near the bed, resting at night, dropping tasks to
    /// defend itself, reporting status, and scheduling task rounds. A role subclass only decides what a
    /// round consists of (<see cref="BeginRound"/> / <see cref="RunTask"/>).
    ///
    /// It is driven from a Harmony prefix on MonsterAI.UpdateAI: while a round is running we take over
    /// movement, otherwise (idle, resting, fighting) the vanilla AI runs untouched so the Dvergr keeps its
    /// normal behaviour and defends itself.
    /// </summary>
    public abstract class WorkerBrain : MonoBehaviour
    {
        private static readonly Dictionary<GameObject, WorkerBrain> Instances = new Dictionary<GameObject, WorkerBrain>();

        public static bool TryGet(GameObject go, out WorkerBrain worker)
        {
            return Instances.TryGetValue(go, out worker);
        }

        private const float FirstRoundDelay = 10f;
        private const float LeashTolerance = 3f;

        /// <summary>Tasks may slightly exceed the job radius by this much.</summary>
        protected const float TaskRadiusTolerance = 3f;

        private float _nextRound;
        private float _threatTimer;
        private float _bedCheckTimer;
        private bool _threatened;
        private float _lastHealth;
        private bool _hasHome;
        private string _status = "";

        internal abstract WorkerRole Role { get; }

        protected ZNetView Nview { get; private set; }
        protected Humanoid Humanoid { get; private set; }
        protected Character Character { get; private set; }
        protected MonsterAI Ai { get; private set; }
        internal WorkerEquipment Equipment { get; private set; }
        internal WorkerNavigator Navigator { get; private set; }

        /// <summary>Position of the worker's bed; the centre of its wander and job radius.</summary>
        protected Vector3 Home { get; private set; }

        protected bool InRound { get; private set; }

        // ------------------------------------------------------------ role hooks

        /// <summary>Find this round's work. Return true if there is any (and the round should start).</summary>
        protected abstract bool BeginRound();

        /// <summary>
        /// Advance the current round. Return true if this frame was handled. Call <see cref="EndRound"/>
        /// when there is nothing left to do.
        /// </summary>
        protected abstract bool RunTask(float dt);

        /// <summary>Status shown on the bed while between rounds.</summary>
        protected virtual string IdleStatus()
        {
            return "Idle";
        }

        /// <summary>Called once a round ends, whether it finished or was interrupted.</summary>
        protected virtual void OnRoundEnded(bool finished)
        {
        }

        // ------------------------------------------------------------ lifecycle

        protected virtual void Awake()
        {
            Nview = GetComponent<ZNetView>();
            Humanoid = GetComponent<Humanoid>();
            Character = GetComponent<Character>();
            Ai = GetComponent<MonsterAI>();
            Equipment = new WorkerEquipment(Humanoid, Role.ToolPrefab);
            Navigator = new WorkerNavigator(Ai, Character, Role);
            _nextRound = Time.time + FirstRoundDelay;
            Instances[gameObject] = this;
        }

        protected virtual void Start()
        {
            ApplyHome();
            _lastHealth = Character.GetHealth();
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
            if (Nview == null || !Nview.IsValid())
            {
                return;
            }

            Vector3 home = Nview.GetZDO().GetVec3(WorkerBed.HomeKey, Vector3.zero);
            if (home == Vector3.zero)
            {
                return;
            }

            Home = home;
            _hasHome = true;

            // Vanilla idle wandering is centred on m_spawnPoint within m_randomMoveRange.
            Ai.m_spawnPoint = home;
            Ai.m_randomMoveRange = WorkerConfig.JobRadius.Value;
        }

        // ------------------------------------------------------------ main loop

        /// <summary>Returns true if this frame's AI update was handled and the vanilla AI should be skipped.</summary>
        public bool Tick(MonsterAI ai, float dt)
        {
            if (Nview == null || !Nview.IsValid() || !Nview.IsOwner())
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
            bool tookDamage = Character.GetHealth() < _lastHealth - 0.01f;
            _lastHealth = Character.GetHealth();
            bool restTime = EnvMan.IsNight() && !WorkerConfig.WorkAtNight.Value;
            bool hasTarget = ai.GetTargetCreature() != null || ai.GetStaticTarget() != null;

            Equipment.Update();

            if (InRound)
            {
                if (_threatened || tookDamage || restTime)
                {
                    EndRound(finished: false);
                    // Hand control straight back to the vanilla AI so it can defend or go to bed.
                    return false;
                }
                return RunTask(dt);
            }

            SetStatus(restTime ? "Resting" : IdleStatus());

            if (_threatened || tookDamage || hasTarget || ai.IsAlerted())
            {
                return false;
            }

            if (!restTime && Time.time >= _nextRound)
            {
                InRound = true;
                if (BeginRound())
                {
                    return RunTask(dt);
                }
                InRound = false;
                _nextRound = Time.time + Role.Interval.Value;
                return false;
            }

            return Leash(dt);
        }

        protected void EndRound(bool finished)
        {
            InRound = false;
            _nextRound = Time.time + Role.Interval.Value;
            Ai.StopMoving();
            Equipment.ReleaseTool();
            OnRoundEnded(finished);
            DvergrWorkersPlugin.Log.LogInfo(Role.Name + " round " + (finished ? "finished" : "interrupted"));
        }

        /// <summary>Keeps the idle worker inside its radius even if something pushed it out.</summary>
        private bool Leash(float dt)
        {
            float radius = WorkerConfig.JobRadius.Value;
            if (Utils.DistanceXZ(transform.position, Home) <= radius + LeashTolerance)
            {
                return false;
            }

            SetStatus("Heading back to bed");
            Ai.MoveTo(dt, Home, radius * 0.5f, false);
            return true;
        }

        /// <summary>True if a point is inside the job radius (plus the task tolerance).</summary>
        protected bool InJobArea(Vector3 point)
        {
            return Utils.DistanceXZ(point, Home) <= WorkerConfig.JobRadius.Value + TaskRadiusTolerance;
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
                if (other == Character || other.IsDead() || !BaseAI.IsEnemy(Character, other))
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

            ZDOID bedId = Nview.GetZDO().GetZDOID(WorkerBed.BedKey);
            if (bedId == ZDOID.None || ZDOMan.instance.GetZDO(bedId) != null)
            {
                return true;
            }

            // The bed was deconstructed: the worker leaves with it.
            DvergrWorkersPlugin.Log.LogInfo("Bed removed, despawning worker");
            Equipment.Discard();
            ZNetScene.instance.Destroy(gameObject);
            return false;
        }

        protected void SetStatus(string status)
        {
            if (status == _status)
            {
                return;
            }
            _status = status;
            Nview.GetZDO().Set(WorkerBed.StatusKey, status);
        }
    }
}
