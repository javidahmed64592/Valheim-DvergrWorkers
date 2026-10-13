using UnityEngine;

namespace DvergrWorkers
{
    /// <summary>
    /// Replaces the vanilla Bed behaviour on worker beds: spawns and respawns the bed's worker and
    /// shows the worker's role and status on hover. Only the ZDO owner of the bed runs the spawn logic.
    /// </summary>
    public class WorkerBed : MonoBehaviour, Hoverable, Interactable
    {
        public const string WorkerKey = "DW_Worker";
        public const string BedKey = "DW_Bed";
        public const string HomeKey = "DW_Home";
        public const string StatusKey = "DW_Status";

        /// <summary>Serialized so it survives the prefab being instantiated; see <see cref="Role"/>.</summary>
        public string RoleName;

        internal WorkerRole Role => _role ?? (_role = WorkerRoles.Get(RoleName));

        private WorkerRole _role;

        private const float CheckInterval = 3f;

        private ZNetView _nview;
        private float _checkTimer;
        private float _missingSince = -1f;

        private void Awake()
        {
            _nview = GetComponent<ZNetView>();
        }

        private void Update()
        {
            if (_nview == null || !_nview.IsValid() || !_nview.IsOwner())
            {
                return;
            }

            _checkTimer -= Time.deltaTime;
            if (_checkTimer > 0f)
            {
                return;
            }
            _checkTimer = CheckInterval;

            if (WorkerExists())
            {
                _missingSince = -1f;
                return;
            }

            ZDO zdo = _nview.GetZDO();
            bool everSpawned = zdo.GetZDOID(WorkerKey) != ZDOID.None;
            if (_missingSince < 0f)
            {
                _missingSince = Time.time;
            }

            // First spawn is immediate; replacements wait out the respawn delay.
            if (everSpawned && Time.time - _missingSince < WorkerConfig.RespawnDelay.Value)
            {
                return;
            }

            SpawnWorker();
            _missingSince = -1f;
        }

        private bool WorkerExists()
        {
            ZDOID id = _nview.GetZDO().GetZDOID(WorkerKey);
            return id != ZDOID.None && ZDOMan.instance.GetZDO(id) != null;
        }

        private void SpawnWorker()
        {
            GameObject prefab = ZNetScene.instance.GetPrefab(Role.WorkerPrefab);
            if (prefab == null)
            {
                DvergrWorkersPlugin.Log.LogError("Worker prefab not registered: " + Role.WorkerPrefab);
                return;
            }

            Vector3 pos = transform.position + transform.forward * 1.5f + Vector3.up * 0.5f;
            if (ZoneSystem.instance.GetSolidHeight(pos, out float height))
            {
                pos.y = Mathf.Max(height, transform.position.y);
            }

            GameObject worker = Instantiate(prefab, pos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            ZNetView workerView = worker.GetComponent<ZNetView>();
            ZDO workerZdo = workerView.GetZDO();
            workerZdo.Set(BedKey, _nview.GetZDO().m_uid);
            workerZdo.Set(HomeKey, transform.position);
            workerZdo.Set(ZDOVars.s_spawnPoint, transform.position);

            _nview.GetZDO().Set(WorkerKey, workerZdo.m_uid);
            worker.GetComponent<WorkerBrain>()?.OnBound();

            DvergrWorkersPlugin.Log.LogInfo(Role.Name + " Dvergr spawned at " + pos);
        }

        public string GetHoverText()
        {
            string status = "No worker";
            if (_nview != null && _nview.IsValid())
            {
                ZDOID id = _nview.GetZDO().GetZDOID(WorkerKey);
                ZDO workerZdo = id != ZDOID.None ? ZDOMan.instance.GetZDO(id) : null;
                status = workerZdo != null ? workerZdo.GetString(StatusKey, "Idle") : "Waiting for a new worker";
            }
            return Role.Name + " Dvergr's bed\n<color=#cccccc>" + status + "</color>" + ChestLines();
        }

        /// <summary>For roles that need chests: how many are in range, in red when a required kind is missing.</summary>
        private string ChestLines()
        {
            string lines = "";
            float radius = WorkerConfig.JobRadius.Value + 3f;
            foreach (ChestKind kind in new[] { ChestKind.Input, ChestKind.Output })
            {
                ChestUse use = kind == ChestKind.Input ? ChestUse.Input : ChestUse.Output;
                if ((Role.Chests & use) == 0)
                {
                    continue;
                }
                int count = WorkerChest.Find(kind, transform.position, radius, transform.position).Count;
                string colour = count > 0 ? "#cccccc" : "#ff6a5c";
                lines += "\n<color=" + colour + ">" + kind + " chests in range: " + count + (count > 0 ? "" : " (required)") + "</color>";
            }
            return lines;
        }

        public float GetHoverOffset()
        {
            return 0f;
        }

        public string GetHoverName()
        {
            return Role.BedName;
        }

        public bool Interact(Humanoid user, bool hold, bool alt)
        {
            if (!hold)
            {
                user.Message(MessageHud.MessageType.Center, "This bed belongs to a Dvergr.");
            }
            return false;
        }

        public bool UseItem(Humanoid user, ItemDrop.ItemData item)
        {
            return false;
        }
    }
}
