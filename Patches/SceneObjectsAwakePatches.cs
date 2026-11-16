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
internal static class TreeLog_Awake_Patch
{
    private static void Postfix(TreeLog __instance)
    {
        Scene.Logs.Add(__instance);
    }
}

[HarmonyPatch(typeof(Destructible), nameof(Destructible.Awake))]
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
internal static class Smelter_Awake_Patch
{
    private static void Postfix(Smelter __instance)
    {
        Scene.Smelters.Add(__instance);
    }
}

[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Awake))]
internal static class Fireplace_Awake_Patch
{
    private static void Postfix(Fireplace __instance)
    {
        Scene.Fireplaces.Add(__instance);
    }
}

[HarmonyPatch(typeof(CookingStation), nameof(CookingStation.Awake))]
internal static class CookingStation_Awake_Patch
{
    private static void Postfix(CookingStation __instance)
    {
        Scene.CookingStations.Add(__instance);
    }
}

[HarmonyPatch(typeof(SapCollector), nameof(SapCollector.Awake))]
internal static class SapCollector_Awake_Patch
{
    private static void Postfix(SapCollector __instance)
    {
        Scene.SapCollectors.Add(__instance);
    }
}

[HarmonyPatch(typeof(Fermenter), nameof(Fermenter.Awake))]
internal static class Fermenter_Awake_Patch
{
    private static void Postfix(Fermenter __instance)
    {
        Scene.Fermenters.Add(__instance);
    }
}

[HarmonyPatch(typeof(Beehive), nameof(Beehive.Awake))]
internal static class Beehive_Awake_Patch
{
    private static void Postfix(Beehive __instance)
    {
        Scene.Beehives.Add(__instance);
    }
}
