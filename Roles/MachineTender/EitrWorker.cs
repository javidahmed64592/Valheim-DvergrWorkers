using System.Collections.Generic;

using DvergrWorkers.Core;
using DvergrWorkers.Movement;

using UnityEngine;

namespace DvergrWorkers.Roles.MachineTender;

/// <summary>
/// Feeds sap and soft tissue to eitr refineries and collects the eitr. It also taps the sap extractors in its
/// job area like a player would: the sap drops on the ground, the worker picks it up and the refineries are
/// topped up from it first. Whatever they have no room for goes to a chest.
/// </summary>
public class EitrWorker : MachineTenderWorker
{
    private enum TapStage { Walk = 0, Tap = 1, Collect = 2 }

    private const string SapName = "$item_sap";
    private const float TapWait = 0.8f;
    private const float CollectInterval = 0.3f;
    private const int MaxCollectTries = 12;
    private const float DropRadius = 3.5f;

    private readonly HashSet<SapCollector> _tapped = [];
    private readonly HashSet<SapCollector> _unreachableTaps = [];
    private SapCollector _extractor;
    private TapStage _stage;
    private float _timer;
    private int _collectTries;

    internal override WorkerRole Role => WorkerRoles.Eitr;

    protected override string AllowedMachines => "eitrrefinery";

    protected override string NoMachinesNote => "No eitr refinery nearby";

    protected override bool IsSurplus(ItemDrop.ItemData item)
    {
        return item.m_shared.m_name == SapName;
    }

    protected override void OnRoundEnded(bool finished)
    {
        base.OnRoundEnded(finished);
        _extractor = null;
    }

    // ---------------------------------------------------------------- sap extractors

    private static ZNetView ViewOf(SapCollector extractor)
    {
        return extractor.GetComponent<ZNetView>();
    }

    private static string SpawnedName(SapCollector extractor)
    {
        return extractor.m_spawnItem != null ? extractor.m_spawnItem.m_itemData.m_shared.m_name : SapName;
    }

    private static IEnumerable<ItemDrop> SapDropsNear(SapCollector extractor)
    {
        if (extractor.m_spawnPoint == null)
        {
            yield break;
        }

        string name = SpawnedName(extractor);
        foreach (ItemDrop drop in ItemDrop.s_instances)
        {
            if (drop != null && drop.m_itemData != null && drop.m_itemData.m_shared.m_name == name
                && Vector3.Distance(drop.transform.position, extractor.m_spawnPoint.position) < DropRadius)
            {
                yield return drop;
            }
        }
    }

    private static int SapLevel(SapCollector extractor)
    {
        ZNetView view = ViewOf(extractor);
        return view != null && view.IsValid() ? view.GetZDO().GetInt(ZDOVars.s_level) : 0;
    }

    /// <summary>Whether the extractor holds sap, or an earlier visit left sap lying next to it.</summary>
    private static bool HasSap(SapCollector extractor)
    {
        if (SapLevel(extractor) > 0)
        {
            return true;
        }
        foreach (ItemDrop _ in SapDropsNear(extractor))
        {
            return true;
        }
        return false;
    }

    private SapCollector NextExtractor()
    {
        SapCollector best = null;
        float bestDist = float.MaxValue;
        foreach (SapCollector extractor in FindObjectsByType<SapCollector>(FindObjectsSortMode.None))
        {
            ZNetView view = ViewOf(extractor);
            if (view == null || !view.IsValid() || !InJobArea(extractor.transform.position)
                || _tapped.Contains(extractor) || _unreachableTaps.Contains(extractor) || !HasSap(extractor)
                || (extractor.m_spawnItem != null && !Logistics.Carried.CanAddItem(extractor.m_spawnItem.m_itemData, 1)))
            {
                continue;
            }

            float d = Vector3.Distance(extractor.transform.position, transform.position);
            if (d < bestDist)
            {
                bestDist = d;
                best = extractor;
            }
        }
        return best;
    }

    protected override bool BeginGather()
    {
        _tapped.Clear();
        _unreachableTaps.Clear();
        _extractor = null;
        return NextExtractor() != null;
    }

    protected override bool Gather(float dt)
    {
        if (_extractor == null)
        {
            _extractor = NextExtractor();
            return _extractor != null && BeginWalkToExtractor();
        }

        if (!_extractor || !ViewOf(_extractor).IsValid())
        {
            _extractor = null;
            return true;
        }

        switch (_stage)
        {
            case TapStage.Walk:
                WalkToExtractor(dt);
                break;

            case TapStage.Tap:
                Face();
                if ((_timer -= dt) <= 0f)
                {
                    Tap();
                }
                break;

            case TapStage.Collect:
                Face();
                if ((_timer -= dt) <= 0f)
                {
                    CollectOne();
                }
                break;
        }
        return true;
    }

    private bool BeginWalkToExtractor()
    {
        SapCollector extractor = _extractor;
        if (!Navigator.Begin("sap extractor", from => WorkerNavigator.ClosestPointOnColliders(extractor.gameObject, from)))
        {
            DvergrWorkersPlugin.Log.LogInfo("No reachable spot next to a sap extractor, skipping");
            _ = _unreachableTaps.Add(extractor);
            _extractor = null;
            return true;
        }

        _stage = TapStage.Walk;
        SetStatus("Walking to a sap extractor");
        return true;
    }

    private void WalkToExtractor(float dt)
    {
        switch (Navigator.Step(dt))
        {
            case NavResult.InReach:
            case NavResult.CloseEnough:
                ViewOf(_extractor).ClaimOwnership();
                _stage = TapStage.Tap;
                _timer = 0.2f;
                SetStatus("Tapping the sap extractor");
                break;

            case NavResult.Failed:
                _ = _unreachableTaps.Add(_extractor);
                _extractor = null;
                break;
        }
    }

    /// <summary>Taps the extractor as a player does. The sap drops at its spawn point once the RPC runs.</summary>
    private void Tap()
    {
        if (SapLevel(_extractor) > 0)
        {
            ViewOf(_extractor).InvokeRPC("RPC_Extract");
            Animation.PlaySwing();
            _timer = TapWait;
        }
        else
        {
            _timer = 0f;
        }
        _stage = TapStage.Collect;
        _collectTries = 0;
    }

    private void CollectOne()
    {
        _timer = CollectInterval;

        ItemDrop target = null;
        foreach (ItemDrop drop in SapDropsNear(_extractor))
        {
            target = drop;
            break;
        }

        if (target == null || ++_collectTries > MaxCollectTries)
        {
            _ = _tapped.Add(_extractor);
            _extractor = null;
            return;
        }

        // Drops are owned by whoever last touched them; ask for ownership and retry for a moment.
        target.RequestOwn();
        if (Humanoid.Pickup(target.gameObject, false, false))
        {
            _collectTries = 0;
        }
    }

    private void Face()
    {
        Ai.StopMoving();
        Vector3 dir = _extractor.transform.position - transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.01f)
        {
            Character.SetLookDir(dir.normalized);
            Ai.LookTowards(dir);
        }
    }
}
