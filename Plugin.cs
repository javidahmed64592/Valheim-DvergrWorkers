using System;

using BepInEx;
using BepInEx.Configuration;

using HarmonyLib;

using Jotunn.Managers;
using Jotunn.Utils;

namespace DvergrWorkers;

[BepInPlugin(PluginGUID, PluginName, PluginVersion)]
[BepInDependency(Jotunn.Main.ModGuid)]
[NetworkCompatibility(CompatibilityLevel.EveryoneMustHaveMod, VersionStrictness.Minor)]
public class DvergrWorkersPlugin : BaseUnityPlugin
{
    public const string PluginGUID = "javidahmed64592.dvergrworkers";
    public const string PluginName = "Dvergr Workers";
    public const string PluginVersion = "0.8.1";

    internal static BepInEx.Logging.ManualLogSource Log { get; private set; }

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
    public static ConfigEntry<bool> TakeFromOutputChests;
    public static ConfigEntry<float> RefuelBelow;
    public static ConfigEntry<int> SeedReserve;
    public static ConfigEntry<int> PlantsPerTrip;
    public static ConfigEntry<int> MaxTrees;
    public static ConfigEntry<int> TreesPerTrip;
    public static ConfigEntry<float> TreeBuildingClearance;
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

        TakeFromOutputChests = config.Bind("Workers", "TakeFromOutputChests", true,
            "Let workers take items from output chests when another worker nearby produces them, e.g. the blacksmith taking the woodcutter's wood. Other items in output chests are never touched.");
        InventoryWidth = config.Bind("Workers", "InventoryWidth", 8,
            new ConfigDescription("Columns in a worker's own inventory.", new AcceptableValueRange<int>(1, 12)));
        InventoryHeight = config.Bind("Workers", "InventoryHeight", 4,
            new ConfigDescription("Rows in a worker's own inventory.", new AcceptableValueRange<int>(1, 8)));

        foreach (WorkerRole role in WorkerRoles.All)
        {
            role.BindConfig(config);
        }

        RefuelBelow = config.Bind(WorkerRoles.Refueler.Name, "RefuelBelow", 0.6f,
            new ConfigDescription("A fire is topped up once its fuel falls to this fraction of its maximum.", new AcceptableValueRange<float>(0.1f, 1f)));
        SeedReserve = config.Bind(WorkerRoles.Farmer.Name, "SeedReserve", 4,
            new ConfigDescription("The farmer plants raw ingredients (a carrot, a turnip...) to make more seeds once a crop's seeds fall to this many or fewer.", new AcceptableValueRange<int>(0, 50)));
        PlantsPerTrip = config.Bind(WorkerRoles.Farmer.Name, "PlantsPerTrip", 8,
            new ConfigDescription("How many of one crop the farmer plants before moving on to the next, which keeps the crops evenly mixed.", new AcceptableValueRange<int>(1, 50)));
        MaxTrees = config.Bind(WorkerRoles.TreeFarmer.Name, "MaxTrees", 40,
            new ConfigDescription("The tree farmer stops planting once the job area holds this many trees and saplings, wild ones included.", new AcceptableValueRange<int>(1, 500)));
        TreesPerTrip = config.Bind(WorkerRoles.TreeFarmer.Name, "TreesPerTrip", 5,
            new ConfigDescription("How many of one species the tree farmer plants before moving on to the next.", new AcceptableValueRange<int>(1, 50)));
        TreeBuildingClearance = config.Bind(WorkerRoles.TreeFarmer.Name, "BuildingClearance", 8f,
            new ConfigDescription("Trees are never planted closer than this (metres) to any building piece.", new AcceptableValueRange<float>(1f, 40f)));
        TreesPerRound = config.Bind(WorkerRoles.Woodcutter.Name, "TreesPerRound", 3,
            new ConfigDescription("How many standing trees a woodcutter fells per round.", new AcceptableValueRange<int>(1, 20)));
        StumpsPerRound = config.Bind(WorkerRoles.Woodcutter.Name, "StumpsPerRound", 10,
            new ConfigDescription("How many leftover tree stumps a woodcutter clears per round. 0 leaves stumps alone.", new AcceptableValueRange<int>(0, 50)));
    }
}