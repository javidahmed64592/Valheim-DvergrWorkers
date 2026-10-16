using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DvergrWorkers
{
    /// <summary>
    /// Everything that distinguishes one worker role from another. Adding a role means adding an entry to
    /// <see cref="WorkerRoles"/> and a <see cref="WorkerBrain"/> subclass; beds, prefabs and config follow from it.
    /// </summary>
    internal sealed class WorkerRole
    {
        /// <summary>Short name used for config sections, prefab names and bed hover text, e.g. "Repair".</summary>
        public string Name;
        public string BedDescription;

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
        public HashSet<string> Produces = new HashSet<string>();

        public float DefaultInterval = 60f;
        public float DefaultReach = 6f;

        public ConfigEntry<float> Interval { get; private set; }
        public ConfigEntry<float> Reach { get; private set; }
        public ConfigEntry<string> AnimationTrigger { get; private set; }

        public string WorkerPrefab => "DW_" + Name + "Dvergr";
        public string BedPrefab => "DW_" + Name + "Bed";
        /// <summary>Name shown to players when it differs from <see cref="Name"/> (which is used for config and prefabs).</summary>
        public string DisplayName;

        public string Title => DisplayName ?? Name;
        public string BedName => Title + " Bed";
        public string WorkerName => Title + " Dvergr";

        public void BindConfig(ConfigFile config)
        {
            Interval = config.Bind(Name, "TaskInterval", DefaultInterval,
                new ConfigDescription("Seconds between task rounds.", new AcceptableValueRange<float>(5f, 1800f)));
            Reach = config.Bind(Name, "ReachDistance", DefaultReach,
                new ConfigDescription("How close (metres) the worker must get to a target before working on it.",
                    new AcceptableValueRange<float>(1.5f, 12f)));
            AnimationTrigger = config.Bind(Name, "AnimationTrigger", "",
                "Animator trigger played when using the tool. Leave empty to pick one automatically.");
        }
    }

    internal static class WorkerRoles
    {
        public static readonly WorkerRole Repair = new WorkerRole
        {
            Name = "Repair",
            BedDescription = "A bed for a Dvergr repairman that fixes and maintains the base.",
            BrainType = typeof(RepairWorker),
            ToolPrefab = "Hammer",
            DefaultReach = 6f,
        };

        public static readonly WorkerRole Woodcutter = new WorkerRole
        {
            Name = "Woodcutter",
            BedDescription = "A bed for a Dvergr woodcutter that fells trees and stores the wood in chests.",
            BrainType = typeof(WoodcutterWorker),
            ToolPrefab = "AxeIron",
            Chests = ChestUse.Output,
            Produces = new HashSet<string>
            {
                "$item_wood", "$item_finewood", "$item_corewood", "$item_elderbark", "$item_yggdrasilwood",
            },
            DefaultInterval = 90f,
            DefaultReach = 2.5f,
        };

        public static readonly WorkerRole Blacksmith = new WorkerRole
        {
            Name = "Blacksmith",
            BedDescription = "A bed for a Dvergr blacksmith that tends to nearby charcoal kilns, smelters and blast furnaces.",
            BrainType = typeof(BlacksmithWorker),
            Chests = ChestUse.Both,
            Produces = new HashSet<string>
            {
                "$item_coal", "$item_copper", "$item_tin", "$item_bronze", "$item_iron", "$item_silver",
                "$item_blackmetal", "$item_flametal",
            },
            DefaultInterval = 60f,
            DefaultReach = 3f,
        };

        public static readonly WorkerRole Refueler = new WorkerRole
        {
            Name = "Refueler",
            BedDescription = "A bed for a Dvergr refueler that keeps nearby torches, fires and braziers burning.",
            BrainType = typeof(RefuelerWorker),
            ToolPrefab = "Lantern",
            Chests = ChestUse.Input,
            DefaultInterval = 60f,
            DefaultReach = 3f,
        };

        public static readonly WorkerRole Farmer = new WorkerRole
        {
            Name = "Farmer",
            BedDescription = "A bed for a Dvergr farmer that plants and harvests crops.",
            BrainType = typeof(FarmerWorker),
            ToolPrefab = "Cultivator",
            Chests = ChestUse.Both,
            Produces = new HashSet<string>
            {
                "$item_carrot", "$item_turnip", "$item_onion", "$item_flax", "$item_barley",
                "$item_carrotseeds", "$item_turnipseeds", "$item_onionseeds", "$item_jotunpuffs", "$item_magecap",
            },
            DefaultInterval = 120f,
            DefaultReach = 2.5f,
        };

        public static readonly WorkerRole TreeFarmer = new WorkerRole
        {
            Name = "TreeFarmer",
            DisplayName = "Tree Farmer",
            BedDescription = "A bed for a Dvergr tree farmer that plants tree saplings.",
            BrainType = typeof(TreeFarmerWorker),
            ToolPrefab = "Cultivator",
            Chests = ChestUse.Input,
            DefaultInterval = 180f,
            DefaultReach = 2.5f,
        };

        public static readonly WorkerRole Barley = new WorkerRole
        {
            Name = "Barley",
            BedDescription = "A bed for a Dvergr miller that keeps nearby windmills fed with barley and delivers the flour.",
            BrainType = typeof(BarleyWorker),
            Chests = ChestUse.Both,
            Produces = new HashSet<string> { "$item_barleyflour" },
            DefaultInterval = 60f,
            DefaultReach = 3f,
        };

        public static readonly WorkerRole Flax = new WorkerRole
        {
            Name = "Flax",
            BedDescription = "A bed for a Dvergr spinner that keeps nearby spinning wheels fed with flax and delivers the linen thread.",
            BrainType = typeof(FlaxWorker),
            Chests = ChestUse.Both,
            Produces = new HashSet<string> { "$item_linenthread" },
            DefaultInterval = 60f,
            DefaultReach = 3f,
        };

        public static readonly WorkerRole Eitr = new WorkerRole
        {
            Name = "Eitr",
            BedDescription = "A bed for a Dvergr eitr refiner that keeps nearby eitr refineries fed with sap and soft tissue and delivers the eitr.",
            BrainType = typeof(EitrWorker),
            Chests = ChestUse.Both,
            Produces = new HashSet<string> { "$item_eitr" },
            DefaultInterval = 60f,
            DefaultReach = 3f,
        };

        public static readonly IReadOnlyList<WorkerRole> All = new List<WorkerRole> { Repair, Woodcutter, Blacksmith, Refueler, Farmer, TreeFarmer, Barley, Flax, Eitr };

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
}
