using System.Collections.Generic;

using DvergrWorkers.Core;

using UnityEngine;

namespace DvergrWorkers.Roles.Collector;

/// <summary>
/// Looks after fermenters: taps the ones that are ready, collects the mead and delivers it to output chests, and
/// puts a mead base from the input chest into each empty one. Like a player, it only fills a fermenter that is
/// under a roof, since one out in the open never ferments.
/// </summary>
public class MeadWorker : CollectorWorker<Fermenter>
{
    /// <summary>Spare time after the fermenter's own delay before the mead has dropped.</summary>
    private const float TapMargin = 0.8f;

    private static readonly HashSet<string> Bases = [];
    private static readonly HashSet<string> Meads = [];

    internal override WorkerRole Role => WorkerRoles.Mead;

    protected override List<Fermenter> AllStations
    {
        get
        {
            List<Fermenter> all = Scene.Fermenters.All;
            if (Bases.Count == 0 && all.Count > 0)
            {
                Load(all[0]);
            }
            return all;
        }
    }

    protected override string NoStationsNote => "$dw_status_no_fermenter";

    /// <summary>The mead bases and meads, read from the fermenter so modded recipes count too.</summary>
    private static void Load(Fermenter fermenter)
    {
        foreach (Fermenter.ItemConversion conversion in fermenter.m_conversion)
        {
            if (conversion.m_from != null && conversion.m_to != null)
            {
                _ = Bases.Add(conversion.m_from.m_itemData.m_shared.m_name);
                _ = Meads.Add(conversion.m_to.m_itemData.m_shared.m_name);
            }
        }
    }

    internal override bool WantsItem(string sharedName)
    {
        return Bases.Contains(sharedName);
    }

    protected override ZNetView ViewOf(Fermenter station)
    {
        return station.m_nview;
    }

    protected override bool IsProduct(ItemDrop.ItemData item)
    {
        return Meads.Contains(item.m_shared.m_name);
    }

    protected override bool IsInput(ItemDrop.ItemData item)
    {
        return Bases.Contains(item.m_shared.m_name);
    }

    private static bool IsEmpty(Fermenter fermenter)
    {
        return fermenter.GetContent() == 0;
    }

    private static bool IsCovered(Fermenter fermenter)
    {
        return fermenter.m_hasRoof && !fermenter.m_exposed;
    }

    protected override bool HasOutput(Fermenter station)
    {
        return !IsEmpty(station) && station.GetFermentationTime() > station.m_fermentationDuration;
    }

    protected override float Release(Fermenter station)
    {
        station.m_nview.InvokeRPC("RPC_Tap");
        return station.m_tapDelay + TapMargin;
    }

    protected override Vector3 OutputPoint(Fermenter station)
    {
        return station.m_outputPoint != null ? station.m_outputPoint.position : station.transform.position;
    }

    protected override bool WillTakeInput(Fermenter station)
    {
        return (IsEmpty(station) || HasOutput(station)) && IsCovered(station);
    }

    protected override bool Feed(Fermenter station, ItemDrop.ItemData input)
    {
        if (!IsEmpty(station) || !IsCovered(station) || input.m_dropPrefab == null || !station.IsItemAllowed(input))
        {
            return false;
        }

        bool cheated = input.m_cheated;
        int hash = input.m_dropPrefab.name.GetStableHashCode();
        _ = Logistics.Carried.RemoveItem(input, 1);
        station.m_nview.InvokeRPC("RPC_AddItem", hash, cheated);
        return true;
    }

    protected override string NothingToDoNote(List<Fermenter> stations)
    {
        bool emptyInTheOpen = false;
        bool emptyCovered = false;
        foreach (Fermenter fermenter in stations)
        {
            if (IsEmpty(fermenter))
            {
                emptyCovered |= IsCovered(fermenter);
                emptyInTheOpen |= !IsCovered(fermenter);
            }
        }
        return (emptyCovered, emptyInTheOpen) switch
        {
            (true, _) => Text.Of("$dw_status_waiting_for", "$dw_mead_bases"),
            (false, true) => "$dw_status_fermenter_needs_roof",
            _ => null,
        };
    }
}
