using System.Collections.Generic;

using HarmonyLib;

using UnityEngine;

namespace DvergrWorkers.Core;

/// <summary>
/// <para>
/// Every loaded object of a kind workers look for, kept as it loads in. Workers used to ask Unity for all objects of
/// a type (<c>FindObjectsByType</c>) whenever they planned their next move, which walks every object in the scene
/// and allocates a fresh array each time; for common kinds like trees, rocks and pickables that is thousands of
/// objects, many times a minute per worker, and the garbage alone causes stutters.
/// </para>
/// <para>
/// None of these game classes has an OnDestroy to hook, so destroyed objects are dropped the first time a list is
/// read in a frame. Objects on inactive game objects are kept but should be skipped, as Unity's search did.
/// </para>
/// </summary>
/// <typeparam name="T">The kind of object, e.g. <see cref="TreeBase"/>.</typeparam>
internal sealed class SceneObjects<T> where T : Component
{
    private readonly List<T> _items = [];
    private int _prunedFrame = -1;

    public void Add(T item)
    {
        if (item != null)
        {
            _items.Add(item);
        }
    }

    /// <summary>
    /// The loaded objects, destroyed ones already removed. Do not create or destroy objects of this kind while
    /// looping over it.
    /// </summary>
    public List<T> All
    {
        get
        {
            if (_prunedFrame != Time.frameCount)
            {
                _prunedFrame = Time.frameCount;
                _ = _items.RemoveAll(IsGone);
            }
            return _items;
        }
    }

    /// <summary>Not destroyed and in the active scene, i.e. what <c>FindObjectsByType</c> would have returned.</summary>
    public static bool IsLive(T item)
    {
        return item != null && item.gameObject.activeInHierarchy;
    }

    private static bool IsGone(T item)
    {
        return item == null;
    }
}

/// <summary>The registries, one per kind of object some worker looks for.</summary>
internal static class Scene
{
    public static readonly SceneObjects<TreeBase> Trees = new();
    public static readonly SceneObjects<TreeLog> Logs = new();

    /// <summary>Tree stumps only, picked out of every <see cref="Destructible"/> (rocks, bushes...) as they load.</summary>
    public static readonly SceneObjects<Destructible> Stumps = new();

    public static readonly SceneObjects<Pickable> Pickables = new();
    public static readonly SceneObjects<Plant> Plants = new();
    public static readonly SceneObjects<Smelter> Smelters = new();
    public static readonly SceneObjects<Fireplace> Fireplaces = new();
    public static readonly SceneObjects<CookingStation> CookingStations = new();
    public static readonly SceneObjects<SapCollector> SapCollectors = new();

    /// <summary>Tree stumps are Destructibles of type Tree named like "Beech_Stub"; player pieces are never touched.</summary>
    internal static bool IsStump(Destructible destructible)
    {
        if (destructible == null || destructible.m_destructibleType != DestructibleType.Tree
            || destructible.GetComponent<Piece>() != null)
        {
            return false;
        }
        string name = destructible.gameObject.name;
        return name.IndexOf("stub", System.StringComparison.OrdinalIgnoreCase) >= 0
            || name.IndexOf("stump", System.StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

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
