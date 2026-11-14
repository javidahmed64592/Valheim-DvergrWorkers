using DvergrWorkers.Core;

using HarmonyLib;

namespace DvergrWorkers.Patches;

[HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.UpdateAI))]
internal static class MonsterAI_UpdateAI_Patch
{
    /// <summary>
    /// While a worker is running a job, skip the vanilla AI for that frame.
    /// </summary>
    private static bool Prefix(MonsterAI __instance, float dt, ref bool __result)
    {
        if (WorkerBrain.TryGet(__instance.gameObject, out WorkerBrain worker) && worker.Tick(__instance, dt))
        {
            __result = true;
            return false;
        }
        return true;
    }
}
