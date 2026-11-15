using DvergrWorkers.Core;

using HarmonyLib;

namespace DvergrWorkers.Patches;

[HarmonyPatch(typeof(Character), nameof(Character.OnDeath))]
internal static class Character_OnDeath_Patch
{
    /// <summary>
    /// A worker that dies drops what it was carrying instead of taking it with it.
    /// </summary>
    private static void Prefix(Character __instance)
    {
        if (WorkerBrain.TryGet(__instance.gameObject, out WorkerBrain worker))
        {
            worker.DropCargo();
        }
    }
}
