using HarmonyLib;

namespace DvergrWorkers
{
    [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.UpdateAI))]
    internal static class MonsterAI_UpdateAI_Patch
    {
        // While a worker is running a job, skip the vanilla AI for that frame.
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

    [HarmonyPatch(typeof(Character), nameof(Character.Damage))]
    internal static class Character_Damage_Patch
    {
        // Workers are invincible to players but still take damage from everything else.
        private static bool Prefix(Character __instance, HitData hit)
        {
            if (WorkerBrain.TryGet(__instance.gameObject, out _) && hit.GetAttacker() is Player)
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(NpcTalk), "Update")]
    internal static class NpcTalk_Update_Patch
    {
        // Vanilla Dvergr turn to face the nearest player whenever they are nearly stationary. That fights
        // the worker's own steering (it makes them dither or wiggle at a chest or machine), so while a
        // round is running only the speech queue is kept going.
        private static bool Prefix(NpcTalk __instance)
        {
            if (WorkerBrain.TryGet(__instance.gameObject, out WorkerBrain worker) && worker.IsWorking)
            {
                __instance.UpdateSayQueue();
                return false;
            }
            return true;
        }
    }
}
