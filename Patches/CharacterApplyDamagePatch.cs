using DvergrWorkers.Core;

using HarmonyLib;

namespace DvergrWorkers.Patches;

[HarmonyPatch(typeof(Character), nameof(Character.ApplyDamage))]
internal static class Character_ApplyDamage_Patch
{
    private static void Prefix(Character __instance, out float __state)
    {
        __state = __instance.GetHealth();
    }

    private static void Postfix(Character __instance, HitData hit, float __state)
    {
        if (__instance.GetHealth() < __state - 0.01f)
        {
            EnemyHits.Record(__instance, hit.GetAttacker());
        }
    }
}
