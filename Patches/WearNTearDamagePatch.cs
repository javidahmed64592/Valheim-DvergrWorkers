using DvergrWorkers.Core;

using HarmonyLib;

namespace DvergrWorkers.Patches;

[HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Damage))]
internal static class WearNTear_Damage_Patch
{
    /// <summary>
    /// Workers fight enemies with area attacks that would otherwise chip away at player-built pieces.
    /// </summary>
    private static bool Prefix(HitData hit)
    {
        return !(hit.GetAttacker() is Character attacker && WorkerBrain.TryGet(attacker.gameObject, out _));
    }
}
