using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Jotunn.Managers;
using Jotunn.Utils;
using System;

namespace DvergrWorkers
{
    [BepInPlugin(PluginGUID, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
    public class DvergrWorkersPlugin : BaseUnityPlugin
    {
        public const string PluginGUID = "javidahmed64592.dvergrworkers";
        public const string PluginName = "Dvergr Workers";
        public const string PluginVersion = "0.3.0";

        internal static BepInEx.Logging.ManualLogSource Log;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;
            WorkerConfig.Bind(Config);

            _harmony = new Harmony(PluginGUID);
            _harmony.PatchAll();

            // Fires at the main menu once vanilla prefabs can be cloned.
            PrefabManager.OnVanillaPrefabsAvailable += RegisterPrefabs;
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        private void RegisterPrefabs()
        {
            // The event fires on every menu start; only build once.
            PrefabManager.OnVanillaPrefabsAvailable -= RegisterPrefabs;

            try
            {
                WorkerPrefabs.RegisterAll();
            }
            catch (Exception e)
            {
                Logger.LogError("Failed to register Dvergr worker prefabs: " + e);
            }
        }
    }

    internal static class WorkerConfig
    {
        public static ConfigEntry<float> JobRadius;
        public static ConfigEntry<float> RespawnDelay;
        public static ConfigEntry<float> ThreatRadius;
        public static ConfigEntry<int> TreesPerRound;
        public static ConfigEntry<int> StumpsPerRound;
        public static ConfigEntry<string> BlacksmithMachines;
        public static ConfigEntry<bool> FuelFromOutputChests;
        public static ConfigEntry<string> RefuelerFuels;
        public static ConfigEntry<float> RefuelBelow;
        public static ConfigEntry<bool> WorkAtNight;
        public static ConfigEntry<int> InventoryWidth;
        public static ConfigEntry<int> InventoryHeight;

        public static void Bind(ConfigFile config)
        {
            JobRadius = config.Bind("Workers", "JobRadius", 30f,
                new ConfigDescription("Workers wander and perform tasks within this distance (metres) of their bed. A small tolerance is added for tasks.",
                    new AcceptableValueRange<float>(5f, 100f)));
            RespawnDelay = config.Bind("Workers", "RespawnDelay", 30f,
                new ConfigDescription("Seconds after a worker is lost before its bed spawns a replacement.",
                    new AcceptableValueRange<float>(1f, 600f)));
            ThreatRadius = config.Bind("Workers", "ThreatRadius", 25f,
                new ConfigDescription("Workers drop their task and defend themselves if an enemy comes within this distance (metres).",
                    new AcceptableValueRange<float>(5f, 80f)));
            WorkAtNight = config.Bind("Workers", "WorkAtNight", false,
                "If false, workers rest at night and only work in daylight.");

            InventoryWidth = config.Bind("Workers", "InventoryWidth", 8,
                new ConfigDescription("Columns in a worker's own inventory.", new AcceptableValueRange<int>(1, 12)));
            InventoryHeight = config.Bind("Workers", "InventoryHeight", 4,
                new ConfigDescription("Rows in a worker's own inventory.", new AcceptableValueRange<int>(1, 8)));

            foreach (WorkerRole role in WorkerRoles.All)
            {
                role.BindConfig(config);
            }

            BlacksmithMachines = config.Bind(WorkerRoles.Blacksmith.Name, "Machines", "charcoal_kiln,smelter,blastfurnace",
                "Comma-separated prefab names of the kilns, smelters and furnaces the blacksmith tends.");
            FuelFromOutputChests = config.Bind(WorkerRoles.Blacksmith.Name, "FuelFromOutputChests", true,
                "Let the blacksmith take coal from output chests as fuel, so charcoal it makes can fuel the smelters.");
            RefuelerFuels = config.Bind(WorkerRoles.Refueler.Name, "Fuels", "Resin,Wood,Coal",
                "Comma-separated item prefab names the refueler may use. Fires that burn anything else are left alone.");
            RefuelBelow = config.Bind(WorkerRoles.Refueler.Name, "RefuelBelow", 0.6f,
                new ConfigDescription("A fire is topped up once its fuel falls to this fraction of its maximum.", new AcceptableValueRange<float>(0.1f, 1f)));
            TreesPerRound = config.Bind(WorkerRoles.Woodcutter.Name, "TreesPerRound", 3,
                new ConfigDescription("How many standing trees a woodcutter fells per round.", new AcceptableValueRange<int>(1, 20)));
            StumpsPerRound = config.Bind(WorkerRoles.Woodcutter.Name, "StumpsPerRound", 10,
                new ConfigDescription("How many leftover tree stumps a woodcutter clears per round. 0 leaves stumps alone.", new AcceptableValueRange<int>(0, 50)));
        }
    }
}
