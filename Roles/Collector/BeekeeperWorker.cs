using System.Collections.Generic;

using DvergrWorkers.Core;

using UnityEngine;

namespace DvergrWorkers.Roles.Collector;

/// <summary>Collects honey from the beehives in its job area and delivers it to output chests.</summary>
public class BeekeeperWorker : CollectorWorker<Beehive>
{
    /// <summary>The honey drops at once; this is a moment for it to land.</summary>
    private const float ExtractWait = 0.8f;

    internal override WorkerRole Role => WorkerRoles.Beekeeper;

    protected override List<Beehive> AllStations => Scene.Beehives.All;

    protected override string NoStationsNote => "$dw_status_no_beehive";

    protected override ZNetView ViewOf(Beehive station)
    {
        return station.m_nview;
    }

    protected override bool IsProduct(ItemDrop.ItemData item)
    {
        return WorkerRoles.Beekeeper.Produces.Contains(item.m_shared.m_name);
    }

    protected override bool HasOutput(Beehive station)
    {
        return station.GetHoneyLevel() > 0;
    }

    protected override float Release(Beehive station)
    {
        station.m_nview.InvokeRPC("RPC_Extract");
        return ExtractWait;
    }

    protected override Vector3 OutputPoint(Beehive station)
    {
        return station.m_spawnPoint != null ? station.m_spawnPoint.position : station.transform.position;
    }
}
