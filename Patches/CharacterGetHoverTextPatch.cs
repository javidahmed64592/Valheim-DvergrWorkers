using DvergrWorkers.Core;

using HarmonyLib;

namespace DvergrWorkers.Patches;

[HarmonyPatch(typeof(Character), nameof(Character.GetHoverText))]
internal static class Character_GetHoverText_Patch
{
    /// <summary>
    /// Vanilla shows nothing at the crosshair for an untamed creature; for a worker, show its status and how to
    /// send it on a break.
    /// </summary>
    private static void Postfix(Character __instance, ref string __result)
    {
        if (WorkerBrain.TryGet(__instance.gameObject, out WorkerBrain worker))
        {
            __result = worker.GetHoverText();
        }
    }
}
