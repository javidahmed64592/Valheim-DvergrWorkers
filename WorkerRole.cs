using BepInEx.Configuration;
using System;
using System.Collections.Generic;

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

        public float DefaultInterval = 60f;
        public float DefaultReach = 6f;

        public ConfigEntry<float> Interval { get; private set; }
        public ConfigEntry<float> Reach { get; private set; }
        public ConfigEntry<string> AnimationTrigger { get; private set; }

        public string WorkerPrefab => "DW_" + Name + "Dvergr";
        public string BedPrefab => "DW_" + Name + "Bed";
        public string BedName => Name + " Bed";
        public string WorkerName => Name + " Dvergr";

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
            BedDescription = "A bed for a Dvergr repairman. Once built, a Dvergr moves in and keeps the surrounding base in good repair.",
            BrainType = typeof(RepairWorker),
            ToolPrefab = "Hammer",
            DefaultReach = 6f,
        };

        public static readonly WorkerRole Woodcutter = new WorkerRole
        {
            Name = "Woodcutter",
            BedDescription = "A bed for a Dvergr woodcutter. Once built, a Dvergr moves in, fells trees around the base and stores the wood in Worker Output Chests.",
            BrainType = typeof(WoodcutterWorker),
            ToolPrefab = "AxeIron",
            Chests = ChestUse.Output,
            DefaultInterval = 90f,
            DefaultReach = 2.5f,
        };

        public static readonly WorkerRole Blacksmith = new WorkerRole
        {
            Name = "Blacksmith",
            BedDescription = "A bed for a Dvergr blacksmith. Once built, a Dvergr moves in and keeps nearby charcoal kilns, smelters and blast furnaces fed from Worker Input Chests, delivering the coal and metal to Worker Output Chests.",
            BrainType = typeof(BlacksmithWorker),
            Chests = ChestUse.Both,
            DefaultInterval = 60f,
            DefaultReach = 3f,
        };

        public static readonly IReadOnlyList<WorkerRole> All = new List<WorkerRole> { Repair, Woodcutter, Blacksmith };

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
