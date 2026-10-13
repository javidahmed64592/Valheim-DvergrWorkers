using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;
using System.Linq;
using UnityEngine;

namespace DvergrWorkers
{
    internal static class WorkerPrefabs
    {
        private const string VanillaDvergr = "Dverger";
        private const string VanillaBed = "bed";

        public static void RegisterAll()
        {
            foreach (WorkerRole role in WorkerRoles.All)
            {
                RegisterWorker(role);
                RegisterBed(role);
            }
        }

        private static void RegisterWorker(WorkerRole role)
        {
            GameObject clone = PrefabManager.Instance.CreateClonedPrefab(role.WorkerPrefab, VanillaDvergr);
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

            clone.GetComponent<Character>().m_name = role.WorkerName;
            clone.AddComponent(role.BrainType);

            PrefabManager.Instance.AddPrefab(clone);
        }

        private static void RegisterBed(WorkerRole role)
        {
            GameObject source = PrefabManager.Instance.GetPrefab(VanillaBed);
            if (source == null)
            {
                DvergrWorkersPlugin.Log.LogError("Vanilla prefab not found: " + VanillaBed);
                return;
            }

            Piece sourcePiece = source.GetComponent<Piece>();
            GameObject clone = PrefabManager.Instance.CreateClonedPrefab(role.BedPrefab, source);

            // Removing the vanilla Bed means players can neither claim nor sleep in it.
            Bed bed = clone.GetComponent<Bed>();
            if (bed != null)
            {
                Object.DestroyImmediate(bed);
            }

            clone.AddComponent<WorkerBed>().RoleName = role.Name;

            var config = new PieceConfig
            {
                Name = role.BedName,
                Description = role.BedDescription,
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
            DvergrWorkersPlugin.Log.LogInfo("Registered " + role.BedPrefab);
        }
    }
}
