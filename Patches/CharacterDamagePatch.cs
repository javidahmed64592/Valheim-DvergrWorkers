using DvergrWorkers.Core;

using HarmonyLib;

namespace DvergrWorkers.Patches;

[HarmonyPatch(typeof(Character), nameof(Character.Damage))]
internal static class Character_Damage_Patch
{
    /// <summary>
    /// Workers are invincible to players but still take damage from everything else.
    /// </summary>
    private static bool Prefix(Character __instance, HitData hit)
    {
        return !WorkerBrain.TryGet(__instance.gameObject, out _) || hit.GetAttacker() is not Player;
    }
}
