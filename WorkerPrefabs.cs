using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using System.Linq;
using UnityEngine;

namespace DvergrWorkers
{
    internal static class WorkerPrefabs
    {
        public const string RepairWorkerPrefab = "DW_RepairDvergr";
        public const string RepairBedPrefab = "DW_RepairBed";

        private const string VanillaDvergr = "Dverger";
        private const string VanillaBed = "bed";

        public static void RegisterRepairWorker()
        {
            GameObject clone = PrefabManager.Instance.CreateClonedPrefab(RepairWorkerPrefab, VanillaDvergr);
            if (clone == null)
            {
                DvergrWorkersPlugin.Log.LogError("Vanilla prefab not found: " + VanillaDvergr);
                return;
            }

            // Workers never drop loot.
            CharacterDrop drop = clone.GetComponent<CharacterDrop>();
            if (drop != null)
            {
                Object.DestroyImmediate(drop);
            }

            Character character = clone.GetComponent<Character>();
            character.m_name = "Repair Dvergr";

            clone.AddComponent<RepairWorker>();

            PrefabManager.Instance.AddPrefab(clone);
        }

        public static void RegisterRepairBed()
        {
            GameObject source = PrefabManager.Instance.GetPrefab(VanillaBed);
            if (source == null)
            {
                DvergrWorkersPlugin.Log.LogError("Vanilla prefab not found: " + VanillaBed);
                return;
            }

            Piece sourcePiece = source.GetComponent<Piece>();
            GameObject clone = PrefabManager.Instance.CreateClonedPrefab(RepairBedPrefab, source);

            // Removing the vanilla Bed means players can neither claim nor sleep in it.
            Bed bed = clone.GetComponent<Bed>();
            if (bed != null)
            {
                Object.DestroyImmediate(bed);
            }

            WorkerBed workerBed = clone.AddComponent<WorkerBed>();
            workerBed.WorkerPrefab = RepairWorkerPrefab;
            workerBed.RoleName = "Repair";

            var config = new PieceConfig
            {
                Name = "Repair Bed",
                Description = "A bed for a Dvergr repairman. Once built, a Dvergr moves in and keeps the surrounding base in good repair.",
                PieceTable = "Hammer",
                Category = sourcePiece.m_category.ToString(),
                CraftingStation = sourcePiece.m_craftingStation != null ? sourcePiece.m_craftingStation.name : "",
                Requirements = sourcePiece.m_resources
                    .Select(r => new RequirementConfig
                    {
                        Item = r.m_resItem.name,
                        Amount = r.m_amount,
                        Recover = r.m_recover
                    })
                    .ToArray()
            };

            PieceManager.Instance.AddPiece(new CustomPiece(clone, false, config));
            DvergrWorkersPlugin.Log.LogInfo("Registered " + RepairBedPrefab);
        }
    }
}
