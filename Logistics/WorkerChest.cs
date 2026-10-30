using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using UnityEngine;

namespace DvergrWorkers.Logistics;

public enum ChestKind { Input = 0, Output = 1 }

/// <summary>Which worker chests a role needs before it will start its task loop.</summary>
[System.Flags]
public enum ChestUse
{
    None = 0,
    Input = 1,
    Output = 2,
    Both = Input | Output,
}

/// <summary>
/// Marks a chest as a worker input or output chest. There is no manual assignment: a worker uses every
/// chest of the right kind inside its job radius, and prefers the closest one.
/// </summary>
public class WorkerChest : MonoBehaviour
{
    private static readonly List<WorkerChest> Instances = [];

    /// <summary>Serialized so it survives the prefab being instantiated; see <see cref="Kind"/>.</summary>
    [SuppressMessage("Design", "CA1051", Justification = "Unity only serializes public fields")]
    public string KindName;

    public ChestKind Kind => KindName == nameof(ChestKind.Input) ? ChestKind.Input : ChestKind.Output;

    public Container Container { get; private set; }

    private ZNetView _nview;

    private void Awake()
    {
        _nview = GetComponent<ZNetView>();
        Container = GetComponent<Container>();

        // The placement ghost has no ZDO and must not be registered.
        if (_nview != null && _nview.GetZDO() != null)
        {
            Instances.Add(this);
        }
    }

    private void OnDestroy()
    {
        _ = Instances.Remove(this);
    }

    public bool IsUsable => _nview != null && _nview.IsValid() && Container != null && Container.GetInventory() != null;

    /// <summary>Chests of one kind inside the area around <paramref name="home"/>, closest to <paramref name="from"/> first.</summary>
    public static List<WorkerChest> Find(ChestKind kind, Vector3 home, float radius, Vector3 from)
    {
        List<WorkerChest> found = [];
        foreach (WorkerChest chest in Instances)
        {
            if (chest.Kind == kind && chest.IsUsable && Utils.DistanceXZ(chest.transform.position, home) <= radius)
            {
                found.Add(chest);
            }
        }
        found.Sort((a, b) => Vector3.Distance(a.transform.position, from).CompareTo(Vector3.Distance(b.transform.position, from)));
        return found;
    }
}
