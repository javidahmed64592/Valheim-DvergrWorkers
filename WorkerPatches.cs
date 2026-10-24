using HarmonyLib;

namespace DvergrWorkers;

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

[HarmonyPatch(typeof(NpcTalk), "Update")]
internal static class NpcTalk_Update_Patch
{
    /// <summary>
    /// Vanilla Dvergr turn to face the nearest player whenever they are nearly stationary. That fights
    /// the worker's own steering (it makes them dither or wiggle at a chest or machine), so while a
    /// round is running only the speech queue is kept going.
    /// </summary>
    private static bool Prefix(NpcTalk __instance)
    {
        if (WorkerBrain.TryGet(__instance.gameObject, out WorkerBrain worker) && (worker.IsWorking || worker.IsAsleep))
        {
            __instance.UpdateSayQueue();
            return false;
        }
        return true;
    }
}

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