using System;

using HarmonyLib;

namespace DvergrWorkers.Core;

/// <summary>
/// Remembers when an enemy last hurt a player or a worker. The time is stored on the victim's ZDO, so every
/// worker nearby sees it, whichever machine owns the victim and whichever owns the worker.
/// </summary>
internal static class EnemyHits
{
    private const string TimeKey = "DW_EnemyHitTime";
    private const string AttackerKey = "DW_EnemyHitBy";

    /// <summary>Called on the victim's owner after it lost health to a hit.</summary>
    public static void Record(Character victim, Character attacker)
    {
        ZNetView nview = victim.GetComponent<ZNetView>();
        if (attacker == null || attacker.IsPlayer() || nview == null || !nview.IsValid() || !nview.IsOwner()
            || !(victim.IsPlayer() || WorkerBrain.TryGet(victim.gameObject, out _))
            || !BaseAI.IsEnemy(victim, attacker))
        {
            return;
        }

        ZDO zdo = nview.GetZDO();
        zdo.Set(TimeKey, ZNet.instance.GetTime().Ticks);
        zdo.Set(AttackerKey, attacker.m_name);
    }

    /// <summary>True if an enemy hurt this character within the last <paramref name="seconds"/>.</summary>
    public static bool HurtRecently(Character victim, float seconds, out float secondsAgo, out string attacker)
    {
        secondsAgo = 0f;
        attacker = "";
        ZNetView nview = victim.GetComponent<ZNetView>();
        if (nview == null || !nview.IsValid())
        {
            return false;
        }

        ZDO zdo = nview.GetZDO();
        long ticks = zdo.GetLong(TimeKey);
        if (ticks == 0L)
        {
            return false;
        }

        secondsAgo = (float)TimeSpan.FromTicks(ZNet.instance.GetTime().Ticks - ticks).TotalSeconds;
        attacker = zdo.GetString(AttackerKey);
        return secondsAgo <= seconds;
    }
}

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
