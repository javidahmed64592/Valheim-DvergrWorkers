using System.Collections.Generic;
using UnityEngine;

namespace DvergrWorkers
{
    /// <summary>
    /// Replaces the vanilla Bed behaviour on worker beds: spawns and respawns the bed's worker and
    /// shows the worker's role and status on hover. Only the ZDO owner of the bed runs the spawn logic.
    /// </summary>
    public class WorkerBed : MonoBehaviour, Hoverable, Interactable
    {
        // ZDOIDs are reassigned every time a world loads, so beds and workers find each other through a
        // persistent GUID instead of through each other's ZDOID.
        public const string BedIdKey = "DW_BedId";
        public const string BornKey = "DW_Born";
        public const string SpawnedKey = "DW_Spawned";
        public const string VariantKey = "DW_Variant";
        public const string HomeKey = "DW_Home";
        public const string StatusKey = "DW_Status";

        private static readonly List<WorkerBed> Instances = new List<WorkerBed>();

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

            // The placement ghost has no ZDO and must not be registered.
            if (_nview != null && _nview.GetZDO() != null)
            {
                Instances.Add(this);
            }
        }

        private void OnDestroy()
        {
            Instances.Remove(this);
        }

        /// <summary>The persistent id shared by this bed and its worker, or "" if the bed has not been given one yet.</summary>
        public string BedId => _nview != null && _nview.IsValid() ? _nview.GetZDO().GetString(BedIdKey, "") : "";

        /// <summary>True if a loaded bed of this role is within <paramref name="radius"/> (horizontally) of the point.</summary>
        internal static bool HasBedNearby(WorkerRole role, Vector3 point, float radius)
        {
            foreach (WorkerBed bed in Instances)
            {
                if (bed != null && bed.Role == role && Utils.DistanceXZ(bed.transform.position, point) <= radius)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>True if a bed with this id is currently loaded.</summary>
        public static bool IsLoaded(string bedId)
        {
            return FindLoaded(bedId) != null;
        }

        public static WorkerBed FindLoaded(string bedId)
        {
            if (bedId.Length == 0)
            {
                return null;
            }
            foreach (WorkerBed bed in Instances)
            {
                if (bed != null && bed.BedId == bedId)
                {
                    return bed;
                }
            }
            return null;
        }

        /// <summary>
        /// The spot on the bed's edge closest to a point, nudged slightly outward: where someone standing at
        /// <paramref name="from"/> would walk to in order to get into the bed. Returns the horizontal distance
        /// from <paramref name="from"/> to the bed's edge.
        /// </summary>
        internal float NearestEdge(Vector3 from, out Vector3 edge)
        {
            if (!TryGetSleepSpot(out Vector3 centre, out Vector3 axis, out float halfLength))
            {
                edge = transform.position;
                return Utils.DistanceXZ(from, edge);
            }

            Vector3 side = Vector3.Cross(Vector3.up, axis);
            float halfWidth = _halfWidth;
            Vector3 offset = Vector3.ProjectOnPlane(from - centre, Vector3.up);
            float along = Mathf.Clamp(Vector3.Dot(offset, axis), -halfLength, halfLength);
            float across = Mathf.Clamp(Vector3.Dot(offset, side), -halfWidth, halfWidth);
            Vector3 nearest = centre + axis * along + side * across;
            nearest.y = from.y;

            Vector3 outward = Vector3.ProjectOnPlane(from - nearest, Vector3.up);
            float distance = outward.magnitude;
            edge = distance > 0.01f ? nearest + outward / distance * 0.4f : nearest;
            return distance;
        }

        private float _halfWidth = 0.5f;

        /// <summary>
        /// Where a worker lies down: the middle of the mattress's top surface, and the direction along the bed
        /// (the way the bed's length runs, which is the longer horizontal side of its model).
        /// </summary>
        internal bool TryGetSleepSpot(out Vector3 centre, out Vector3 axis, out float halfLength)
        {
            centre = transform.position;
            axis = transform.forward;
            halfLength = 1f;

            bool found = false;
            Bounds local = new Bounds();
            foreach (MeshRenderer renderer in GetComponentsInChildren<MeshRenderer>())
            {
                Bounds world = renderer.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = world.center + Vector3.Scale(world.extents,
                        new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 point = transform.InverseTransformPoint(corner);
                    if (found)
                    {
                        local.Encapsulate(point);
                    }
                    else
                    {
                        local = new Bounds(point, Vector3.zero);
                        found = true;
                    }
                }
            }

            if (!found)
            {
                centre += Vector3.up * 0.5f;
                return true;
            }

            Vector3 top = local.center;
            top.y = local.max.y;
            centre = transform.TransformPoint(top);
            axis = transform.TransformDirection(local.size.x > local.size.z ? Vector3.right : Vector3.forward);
            axis = Vector3.ProjectOnPlane(axis, Vector3.up).normalized;
            float scale = Mathf.Abs(transform.lossyScale.x);
            halfLength = Mathf.Max(local.size.x, local.size.z) * 0.5f * scale;
            _halfWidth = Mathf.Min(local.size.x, local.size.z) * 0.5f * scale;
            return true;
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

            ZDO zdo = _nview.GetZDO();
            string id = zdo.GetString(BedIdKey, "");
            if (id.Length == 0)
            {
                id = System.Guid.NewGuid().ToString("N");
                zdo.Set(BedIdKey, id);
            }

            // Cheap check first: is this bed's worker in the loaded world?
            if (WorkerBrain.IsLoaded(id))
            {
                _missingSince = -1f;
                return;
            }

            if (_missingSince < 0f)
            {
                _missingSince = Time.time;
            }

            // First spawn is immediate; replacements (and anything after a world load, while the worker
            // may still be loading in) wait out the respawn delay.
            bool everSpawned = zdo.GetBool(SpawnedKey, false);
            if (everSpawned && Time.time - _missingSince < WorkerConfig.RespawnDelay.Value)
            {
                return;
            }

            // Authoritative check before spawning: look through every ZDO, including ones not loaded as
            // objects (a worker standing in a distant zone is still a worker).
            if (WorkerZdoExists(id))
            {
                _missingSince = Time.time;
                return;
            }

            SpawnWorker(id);
            _missingSince = -1f;
        }

        private bool WorkerZdoExists(string bedId)
        {
            var found = new List<ZDO>();
            int index = 0;
            for (int variant = 0; variant < WorkerPrefabs.Variants.Length; variant++)
            {
                index = 0;
                while (!ZDOMan.instance.GetAllZDOsWithPrefabIterative(Role.WorkerPrefabFor(variant), found, ref index))
                {
                }
            }

            foreach (ZDO zdo in found)
            {
                if (zdo.GetString(BedIdKey, "") == bedId)
                {
                    return true;
                }
            }
            return false;
        }

        private void SpawnWorker(string bedId)
        {
            // The look is rolled once, when the bed first spawns its worker, and kept for every respawn.
            ZDO bedZdo = _nview.GetZDO();
            int variant = bedZdo.GetInt(VariantKey, -1);
            if (variant < 0 || variant >= WorkerPrefabs.Variants.Length)
            {
                variant = Random.Range(0, WorkerPrefabs.Variants.Length);
                bedZdo.Set(VariantKey, variant);
            }

            string prefabName = Role.WorkerPrefabFor(variant);
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null)
            {
                DvergrWorkersPlugin.Log.LogError("Worker prefab not registered: " + prefabName);
                return;
            }

            Vector3 pos = FindSpawnPoint();

            GameObject worker = Instantiate(prefab, pos, Quaternion.Euler(0f, Random.Range(0f, 360f), 0f));
            ZDO workerZdo = worker.GetComponent<ZNetView>().GetZDO();
            workerZdo.Set(BedIdKey, bedId);
            workerZdo.Set(BornKey, ZNet.instance.GetTime().Ticks);
            workerZdo.Set(HomeKey, transform.position);
            workerZdo.Set(ZDOVars.s_spawnPoint, transform.position);

            _nview.GetZDO().Set(SpawnedKey, true);
            worker.GetComponent<WorkerBrain>()?.OnBound();

            DvergrWorkersPlugin.Log.LogInfo(Role.Title + " Dvergr spawned at " + pos);
        }

        /// <summary>
        /// Somewhere beside the bed at the bed's own level. The terrain height at that spot is no good: under a
        /// roof or beside a cliff it is the top of whatever is above, which would drop the worker out of sight.
        /// </summary>
        private Vector3 FindSpawnPoint()
        {
            int mask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece", "Default_small");
            float bedY = transform.position.y;

            for (int i = 0; i < 8; i++)
            {
                float angle = i * Mathf.PI * 0.25f;
                Vector3 offset = Quaternion.Euler(0f, transform.eulerAngles.y, 0f) * new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * 1.5f;
                Vector3 from = transform.position + offset;
                from.y = bedY + 1.5f;

                if (Physics.Raycast(from, Vector3.down, out RaycastHit hit, 4f, mask)
                    && Mathf.Abs(hit.point.y - bedY) <= 1.5f
                    && !Physics.CheckCapsule(hit.point + Vector3.up * 0.5f, hit.point + Vector3.up * 1.4f, 0.4f, mask))
                {
                    return hit.point + Vector3.up * 0.1f;
                }
            }

            // Boxed in on every side: start on the bed itself.
            return transform.position + Vector3.up * 0.5f;
        }

        public string GetHoverText()
        {
            string status = "No worker";
            if (_nview != null && _nview.IsValid())
            {
                WorkerBrain worker = WorkerBrain.FindLoaded(BedId);
                status = worker != null ? worker.GetStatus() : "Waiting for a worker";
            }
            return Role.Title + " Dvergr's bed\n<color=#cccccc>" + status + "</color>" + ChestLines();
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
