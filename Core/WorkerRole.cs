using System;
using System.Collections.Generic;

using BepInEx.Configuration;

using DvergrWorkers.Beds;
using DvergrWorkers.Logistics;
using DvergrWorkers.Prefabs;
using DvergrWorkers.Roles.Farmer;
using DvergrWorkers.Roles.MachineTender;
using DvergrWorkers.Roles.Refueler;
using DvergrWorkers.Roles.Repair;
using DvergrWorkers.Roles.TreeFarmer;
using DvergrWorkers.Roles.Woodcutter;

using UnityEngine;

namespace DvergrWorkers.Core;

/// <summary>
/// Everything that distinguishes one worker role from another. Adding a role means adding an entry to
/// <see cref="WorkerRoles"/> and a <see cref="WorkerBrain"/> subclass; beds, prefabs and config follow from it.
/// </summary>
internal sealed class WorkerRole
{
    /// <summary>Short name used for config sections, prefab names and bed hover text, e.g. "Repair".</summary>
    public string Name;

    /// <summary>Lower-case <see cref="Name"/>, used in translation tokens.</summary>
    private string Key => Name.ToLowerInvariant();

    /// <summary>The bed's description (a translation token).</summary>
    public string BedDescription => "$piece_dw_" + Key + "bed_description";

    /// <summary>The <see cref="WorkerBrain"/> subclass added to the worker prefab.</summary>
    public Type BrainType;

    /// <summary>Item prefab the worker carries and holds while working, or null if it needs none.</summary>
    public string ToolPrefab;

    /// <summary>Which worker chests must exist in range before this role starts its task loop.</summary>
    public ChestUse Chests = ChestUse.None;

    /// <summary>
    /// Items (by shared name) this role puts into output chests. Roles that need one of these items may
    /// take it from output chests, as long as a worker of this role lives nearby, so one worker's output
    /// becomes another's input without the player moving anything.
    /// </summary>
    public HashSet<string> Produces = [];

    public float DefaultInterval = 60f;
    public float DefaultReach = 6f;

    public ConfigEntry<float> Interval { get; private set; }
    public ConfigEntry<float> Reach { get; private set; }

    /// <summary>Prefab name of this role's worker for the given look (see <see cref="WorkerPrefabs.Variants"/>).</summary>
    public string WorkerPrefabFor(int variant)
    {
        return "DW_" + Name + "Dvergr" + WorkerPrefabs.Variants[variant].Suffix;
    }

    public string BedPrefab => "DW_" + Name + "Bed";
    /// <summary>Name shown to players when it differs from <see cref="Name"/> (which is used for config and prefabs).</summary>
    public string DisplayName;

    /// <summary>English name for logs.</summary>
    public string Title => DisplayName ?? Name;

    /// <summary>The bed's name (a translation token), e.g. "Repair Bed".</summary>
    public string BedName => "$piece_dw_" + Key + "bed";

    /// <summary>The worker's name (a translation token), e.g. "Repair Dvergr".</summary>
    public string WorkerName => "$dw_worker_" + Key;

    public void BindConfig(ConfigFile config)
    {
        Interval = config.Bind(Name, "TaskInterval", DefaultInterval,
            WorkerConfig.Synced("Seconds between task rounds.", new AcceptableValueRange<float>(5f, 1800f)));
        Reach = config.Bind(Name, "ReachDistance", DefaultReach,
            WorkerConfig.Synced("How close (metres) the worker must get to a target before working on it.", new AcceptableValueRange<float>(1.5f, 12f)));
    }
}

internal static class WorkerRoles
{
    public static readonly WorkerRole Repair = new()
    {
        Name = "Repair",
        BrainType = typeof(RepairWorker),
        ToolPrefab = "Hammer",
        DefaultReach = 6f,
    };

    public static readonly WorkerRole Woodcutter = new()
    {
        Name = "Woodcutter",
        BrainType = typeof(WoodcutterWorker),
        ToolPrefab = "AxeIron",
        Chests = ChestUse.Output,
        Produces =
        [
            "$item_wood", "$item_finewood", "$item_corewood", "$item_elderbark", "$item_yggdrasilwood",
            "$item_resin", "$item_feathers",
        ],
        DefaultInterval = 90f,
        DefaultReach = 2.5f,
    };

    public static readonly WorkerRole Blacksmith = new()
    {
        Name = "Blacksmith",
        BrainType = typeof(BlacksmithWorker),
        Chests = ChestUse.Both,
        Produces =
        [
            "$item_coal", "$item_copper", "$item_tin", "$item_bronze", "$item_iron", "$item_silver",
            "$item_blackmetal", "$item_flametal",
        ],
        DefaultInterval = 60f,
        DefaultReach = 3f,
    };

    public static readonly WorkerRole Refueler = new()
    {
        Name = "Refueler",
        BrainType = typeof(RefuelerWorker),
        ToolPrefab = "Lantern",
        Chests = ChestUse.Input,
        DefaultInterval = 60f,
        DefaultReach = 3f,
    };

    public static readonly WorkerRole Farmer = new()
    {
        Name = "Farmer",
        BrainType = typeof(FarmerWorker),
        ToolPrefab = "Cultivator",
        Chests = ChestUse.Both,
        Produces =
        [
            "$item_carrot", "$item_turnip", "$item_onion", "$item_flax", "$item_barley",
            "$item_carrotseeds", "$item_turnipseeds", "$item_onionseeds", "$item_jotunpuffs", "$item_magecap",
        ],
        DefaultInterval = 120f,
        DefaultReach = 2.5f,
    };

    public static readonly WorkerRole TreeFarmer = new()
    {
        Name = "TreeFarmer",
        DisplayName = "Tree Farmer",
        BrainType = typeof(TreeFarmerWorker),
        ToolPrefab = "Cultivator",
        Chests = ChestUse.Input,
        DefaultInterval = 180f,
        DefaultReach = 2.5f,
    };

    public static readonly WorkerRole Barley = new()
    {
        Name = "Barley",
        BrainType = typeof(BarleyWorker),
        Chests = ChestUse.Both,
        Produces = ["$item_barleyflour"],
        DefaultInterval = 60f,
        DefaultReach = 3f,
    };

    public static readonly WorkerRole Flax = new()
    {
        Name = "Flax",
        BrainType = typeof(FlaxWorker),
        Chests = ChestUse.Both,
        Produces = ["$item_linenthread"],
        DefaultInterval = 60f,
        DefaultReach = 3f,
    };

    public static readonly WorkerRole Eitr = new()
    {
        Name = "Eitr",
        BrainType = typeof(EitrWorker),
        Chests = ChestUse.Both,
        Produces = ["$item_eitr"],
        DefaultInterval = 60f,
        DefaultReach = 3f,
    };

    public static readonly IReadOnlyList<WorkerRole> All = [Repair, Woodcutter, Blacksmith, Refueler, Farmer, TreeFarmer, Barley, Flax, Eitr];

    /// <summary>True if a worker role that makes this item has a bed within <paramref name="radius"/> of the point.</summary>
    public static bool IsProducedNearby(string sharedName, Vector3 point, float radius)
    {
        foreach (WorkerRole role in All)
        {
            if (role.Produces.Contains(sharedName) && WorkerBed.HasBedNearby(role, point, radius))
            {
                return true;
            }
        }
        return false;
    }

    public static WorkerRole Get(string name)
    {
        foreach (WorkerRole role in All)
        {
            if (role.Name == name)
            {
                return role;
            }
        }
        return null;
    }
}
