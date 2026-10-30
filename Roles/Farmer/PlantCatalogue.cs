using System;
using System.Collections.Generic;
using System.Linq;

using UnityEngine;

namespace DvergrWorkers;

/// <summary>One plantable thing from the cultivator's build menu: a crop or a tree sapling.</summary>
internal sealed class PlantType
{
    public GameObject Prefab;
    public Piece Piece;
    public Plant Plant;
    public string RequiredShared;
    public int Amount;

    /// <summary>A raw ingredient planted to grow seeds (a carrot, a turnip...).</summary>
    public bool Raw;

    public bool IsTree;

    /// <summary>What counts as "more of the same" when planting evenly.</summary>
    public string Family;

    /// <summary>The item a crop gives when harvested.</summary>
    public string YieldShared;

    /// <summary>For trees, the prefab names of the full-grown trees this sapling can become.</summary>
    public HashSet<string> GrownNames = [];
}

/// <summary>A planted spot and how much room that plant needs around itself to grow.</summary>
internal struct Occupied
{
    public Vector3 Position;
    public float Radius;
}

/// <summary>The plants a cultivator can place, read from the game once and shared by the farmer roles.</summary>
internal static class PlantCatalogue
{
    private const string CultivatorPrefab = "Cultivator";

    private static List<PlantType> _all;

    /// <summary>
    /// Extra gap between plants, on top of the room each one needs. The game measures a plant's room against
    /// its neighbours' colliders, which have size, not against their centres.
    /// </summary>
    public const float SpacingMargin = 0.4f;

    public static List<PlantType> All
    {
        get
        {
            if (_all == null)
            {
                Load();
            }
            return _all;
        }
    }

    public static IEnumerable<PlantType> Crops => All.Where(t => !t.IsTree);

    public static IEnumerable<PlantType> Trees => All.Where(t => t.IsTree);

    /// <summary>The item names of every tree seed, cone and acorn, e.g. what felled trees drop.</summary>
    public static HashSet<string> TreeSeedNames => [.. Trees.Select(t => t.RequiredShared)];

    public static bool IsSeedName(string sharedName)
    {
        return sharedName.IndexOf("seed", StringComparison.InvariantCultureIgnoreCase) >= 0;
    }

    /// <summary>The type a plant or tree object belongs to, or null.</summary>
    public static PlantType Of(GameObject go)
    {
        string name = Utils.GetPrefabName(go);
        return All.FirstOrDefault(t => t.Prefab.name == name || (t.IsTree && t.GrownNames.Contains(name)));
    }

    /// <summary>
    /// A plant may only go in biomes the game allows. The game checks two things: the build piece's own biome
    /// restriction when a player places it (the strict one) and the plant's growth biomes. Both must hold,
    /// or it is planted somewhere it cannot grow.
    /// </summary>
    public static bool BiomeAllows(PlantType type, Vector3 p)
    {
        Heightmap.Biome biome = Heightmap.FindBiome(p);
        return (type.Piece.m_onlyInBiome == Heightmap.Biome.None || (biome & type.Piece.m_onlyInBiome) != 0)
            && (biome & type.Plant.m_biome) != 0;
    }

    private static void Load()
    {
        _all = [];
        GameObject cultivator = ObjectDB.instance.GetItemPrefab(CultivatorPrefab);
        PieceTable table = cultivator != null ? cultivator.GetComponent<ItemDrop>().m_itemData.m_shared.m_buildPieces : null;
        if (table == null)
        {
            DvergrWorkersPlugin.Log.LogError("Cultivator piece table not found");
            return;
        }

        foreach (GameObject prefab in table.m_pieces)
        {
            Piece piece = prefab.GetComponent<Piece>();
            Plant plant = prefab.GetComponent<Plant>();
            if (piece == null || plant == null || plant.m_grownPrefabs.Length == 0 || plant.m_attachDistance > 0f
                || piece.m_resources.Length == 0 || piece.m_resources[0].m_resItem == null)
            {
                continue;
            }

            GameObject grown = plant.m_grownPrefabs[0];
            string required = piece.m_resources[0].m_resItem.m_itemData.m_shared.m_name;
            int amount = Math.Max(1, piece.m_resources[0].m_amount);

            if (grown.GetComponent<TreeBase>() != null)
            {
                _all.Add(new PlantType
                {
                    Prefab = prefab,
                    Piece = piece,
                    Plant = plant,
                    RequiredShared = required,
                    Amount = amount,
                    IsTree = true,
                    Family = required,
                    GrownNames = [.. plant.m_grownPrefabs.Select(g => g.name)],
                });
                DvergrWorkersPlugin.Log.LogInfo("Tree: " + prefab.name + " (needs " + required + ", room "
                    + plant.m_growRadius + "m, grows " + string.Join("/", plant.m_grownPrefabs.Select(g => g.name)) + ")");
                continue;
            }

            Pickable pickable = grown.GetComponent<Pickable>();
            if (pickable == null || pickable.m_itemPrefab == null)
            {
                continue;
            }

            string yield = pickable.m_itemPrefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_name;

            // A plant whose harvest is seeds is a raw ingredient being planted to make more seeds.
            PlantType type = new()
            {
                Prefab = prefab,
                Piece = piece,
                Plant = plant,
                RequiredShared = required,
                Amount = amount,
                Raw = IsSeedName(yield) && !IsSeedName(required),
                YieldShared = yield,
                Family = IsSeedName(required) ? yield : required,
            };
            _all.Add(type);
            DvergrWorkersPlugin.Log.LogInfo("Farmer crop: " + prefab.name + " (needs " + required + ", room "
                + plant.m_growRadius + "m, yields " + yield + (type.Raw ? ", raw" : "") + ", family " + type.Family + ")");
        }
    }
}
