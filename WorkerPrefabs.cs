using System.Linq;

using Jotunn.Configs;
using Jotunn.Entities;
using Jotunn.Managers;

using UnityEngine;

namespace DvergrWorkers;

internal static class WorkerPrefabs
{
    /// <summary>
    /// The vanilla Dvergr looks a worker can be born with. The first keeps the original prefab names, so
    /// workers from earlier versions still load.
    /// </summary>
    public static readonly (string Source, string Suffix)[] Variants =
    [
        ("Dverger", ""),
        ("DvergerMage", "Mage"),
        ("DvergerMageFire", "Fire"),
        ("DvergerMageIce", "Ice"),
        ("DvergerMageSupport", "Support"),
    ];

    private const string VanillaBed = "bed";

    /// <summary>
    /// The reinforced ("iron") chest. Worker chests copy its requirements.
    /// </summary>
    private const string VanillaChest = "piece_chest";

    public static void RegisterAll()
    {
        foreach (WorkerRole role in WorkerRoles.All)
        {
            for (int variant = 0; variant < Variants.Length; variant++)
            {
                RegisterWorker(role, variant);
            }
            RegisterBed(role);
        }

        RegisterChest(ChestKind.Input, "Worker Input Chest", new Color(0.6f, 0.8f, 1f),
            "Stores the materials your Dvergr workers need for their tasks. Workers within range use every input chest in their job radius, closest first.");
        RegisterChest(ChestKind.Output, "Worker Output Chest", new Color(0.65f, 1f, 0.65f),
            "Collects what your Dvergr workers produce. Workers within range use every output chest in their job radius, closest first.");
    }

    private static void RegisterChest(ChestKind kind, string displayName, Color tint, string description)
    {
        GameObject source = PrefabManager.Instance.GetPrefab(VanillaChest);
        if (source == null)
        {
            DvergrWorkersPlugin.Log.LogError("Vanilla prefab not found: " + VanillaChest);
            return;
        }

        string prefabName = "DW_" + kind + "Chest";
        Piece sourcePiece = source.GetComponent<Piece>();
        GameObject clone = PrefabManager.Instance.CreateClonedPrefab(prefabName, source);

        clone.GetComponent<Container>().m_name = displayName;
        clone.AddComponent<WorkerChest>().KindName = kind.ToString();
        Tint(clone, tint);

        PieceConfig config = new()
        {
            Name = displayName,
            Description = description,
            PieceTable = "Hammer",
            Category = sourcePiece.m_category.ToString(),
            CraftingStation = sourcePiece.m_craftingStation != null ? sourcePiece.m_craftingStation.name : "",
            Requirements = [.. sourcePiece.m_resources
                .Select(r => new RequirementConfig
                {
                    Item = r.m_resItem.name,
                    Amount = r.m_amount,
                    Recover = r.m_recover
                })]
        };

        _ = PieceManager.Instance.AddPiece(new CustomPiece(clone, false, config));
        DvergrWorkersPlugin.Log.LogInfo("Registered " + prefabName);
    }

    /// <summary>Colours the chest so input and output chests can be told apart at a glance.</summary>
    private static void Tint(GameObject chest, Color tint)
    {
        foreach (Renderer renderer in chest.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer.GetType().Name == "ParticleSystemRenderer")
            {
                continue;
            }
            foreach (Material material in renderer.materials)
            {
                if (material.HasProperty("_Color"))
                {
                    material.color *= tint;
                }
            }
        }
    }

    private static void RegisterWorker(WorkerRole role, int variant)
    {
        string source = Variants[variant].Source;
        GameObject clone = PrefabManager.Instance.CreateClonedPrefab(role.WorkerPrefabFor(variant), source);
        if (clone == null)
        {
            DvergrWorkersPlugin.Log.LogError("Vanilla prefab not found: " + source);
            return;
        }

        // Workers never drop loot.
        CharacterDrop drop = clone.GetComponent<CharacterDrop>();
        if (drop != null)
        {
            Object.DestroyImmediate(drop);
        }

        clone.GetComponent<Character>().m_name = role.WorkerName;
        _ = clone.AddComponent(role.BrainType);

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

        PieceConfig config = new()
        {
            Name = role.BedName,
            Description = role.BedDescription,
            PieceTable = "Hammer",
            Category = sourcePiece.m_category.ToString(),
            CraftingStation = sourcePiece.m_craftingStation != null ? sourcePiece.m_craftingStation.name : "",
            Requirements = [.. sourcePiece.m_resources
                .Select(r => new RequirementConfig
                {
                    Item = r.m_resItem.name,
                    Amount = r.m_amount,
                    Recover = r.m_recover
                })]
        };

        _ = PieceManager.Instance.AddPiece(new CustomPiece(clone, false, config));
        DvergrWorkersPlugin.Log.LogInfo("Registered " + role.BedPrefab);
    }
}