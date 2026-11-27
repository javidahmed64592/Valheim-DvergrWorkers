using DvergrWorkers.Core;

using HarmonyLib;

namespace DvergrWorkers.Patches;

[HarmonyPatch(typeof(TreeBase), nameof(TreeBase.Awake))]
internal static class TreeBase_Awake_Patch
{
    private static void Postfix(TreeBase __instance)
    {
        Scene.Trees.Add(__instance);
    }
}

[HarmonyPatch(typeof(TreeLog), nameof(TreeLog.Awake))]
[Feature(Feature)]
internal static class TreeLog_Awake_Patch
{
    /// <summary>Without it, woodcutters still fell trees but don't pick up the logs or clear the stumps of trees felled from then on.</summary>
    public const string Feature = "Finding logs and stumps";

    private static void Postfix(TreeLog __instance)
    {
        Scene.Logs.Add(__instance);
    }
}

[HarmonyPatch(typeof(Destructible), nameof(Destructible.Awake))]
[Feature(TreeLog_Awake_Patch.Feature)]
internal static class Destructible_Awake_Patch
{
    private static void Postfix(Destructible __instance)
    {
        if (Scene.IsStump(__instance))
        {
            Scene.Stumps.Add(__instance);
        }
    }
}

[HarmonyPatch(typeof(Pickable), nameof(Pickable.Awake))]
internal static class Pickable_Awake_Patch
{
    private static void Postfix(Pickable __instance)
    {
        Scene.Pickables.Add(__instance);
    }
}

[HarmonyPatch(typeof(Plant), nameof(Plant.Awake))]
internal static class Plant_Awake_Patch
{
    private static void Postfix(Plant __instance)
    {
        Scene.Plants.Add(__instance);
    }
}

[HarmonyPatch(typeof(Smelter), nameof(Smelter.Awake))]
[Feature(Feature)]
internal static class Smelter_Awake_Patch
{
    /// <summary>Without it, workers don't find smelters, kilns, windmills, spinning wheels, refineries or foundries loaded from then on.</summary>
    public const string Feature = "Finding smelters";

    private static void Postfix(Smelter __instance)
    {
        Scene.Smelters.Add(__instance);
    }
}

[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Awake))]
[Feature(Feature)]
internal static class Fireplace_Awake_Patch
{
    /// <summary>Without it, the refueler doesn't find torches, fires and braziers loaded from then on.</summary>
    public const string Feature = "Finding fires";

    private static void Postfix(Fireplace __instance)
    {
        Scene.Fireplaces.Add(__instance);
    }
}

[HarmonyPatch(typeof(CookingStation), nameof(CookingStation.Awake))]
[Feature(Feature)]
internal static class CookingStation_Awake_Patch
{
    /// <summary>Without it, workers don't find cooking stations (such as the frost foundry) loaded from then on.</summary>
    public const string Feature = "Finding cooking stations";

    private static void Postfix(CookingStation __instance)
    {
        Scene.CookingStations.Add(__instance);
    }
}

[HarmonyPatch(typeof(SapCollector), nameof(SapCollector.Awake))]
[Feature(Feature)]
internal static class SapCollector_Awake_Patch
{
    /// <summary>Without it, the eitr worker doesn't find sap collectors loaded from then on.</summary>
    public const string Feature = "Finding sap collectors";

    private static void Postfix(SapCollector __instance)
    {
        Scene.SapCollectors.Add(__instance);
    }
}

[HarmonyPatch(typeof(Fermenter), nameof(Fermenter.Awake))]
[Feature(Feature)]
internal static class Fermenter_Awake_Patch
{
    /// <summary>Without it, the mead worker doesn't find fermenters loaded from then on.</summary>
    public const string Feature = "Finding fermenters";

    private static void Postfix(Fermenter __instance)
    {
        Scene.Fermenters.Add(__instance);
    }
}

[HarmonyPatch(typeof(Beehive), nameof(Beehive.Awake))]
[Feature(Feature)]
internal static class Beehive_Awake_Patch
{
    /// <summary>Without it, the beekeeper doesn't find beehives loaded from then on.</summary>
    public const string Feature = "Finding beehives";

    private static void Postfix(Beehive __instance)
    {
        Scene.Beehives.Add(__instance);
    }
}
