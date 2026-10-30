using System;
using System.Collections.Generic;

using DvergrWorkers.Beds;
using DvergrWorkers.Logistics;
using DvergrWorkers.Movement;
using DvergrWorkers.Prefabs;

using UnityEngine;

namespace DvergrWorkers.Core;

/// <summary>
/// <para>
/// Behaviour shared by every worker role: staying near the bed, resting at night, dropping tasks to
/// defend itself, reporting status, and scheduling task rounds. A role subclass only decides what a
/// round consists of (<see cref="BeginRound"/> / <see cref="RunTask"/>).
/// </para>
/// <para>
/// It is driven from a Harmony prefix on MonsterAI.UpdateAI: while a round is running we take over
/// movement, otherwise (idle, resting, fighting) the vanilla AI runs untouched so the Dvergr keeps its
/// normal behaviour and defends itself.
/// </para>
/// </summary>
public abstract class WorkerBrain : MonoBehaviour
{
    private static readonly Dictionary<GameObject, WorkerBrain> Instances = [];

    public static bool TryGet(GameObject go, out WorkerBrain worker)
    {
        return Instances.TryGetValue(go, out worker);
    }

    private const float FirstRoundDelay = 10f;
    private const float LeashTolerance = 3f;

    /// <summary>How long after being hurt a worker stays in the vanilla fighting AI.</summary>
    private const float EngagedHold = 8f;

    /// <summary>Tasks may slightly exceed the job radius by this much.</summary>
    protected const float TaskRadiusTolerance = 3f;

    private float _nextRound;
    private float _retrySoon;
    private float _threatTimer;
    private float _bedCheckTimer;
    private int _bedMissingChecks;
    private bool _threatened;
    private float _lastHealth;
    private float _engagedUntil;
    private bool _hasHome;
    private string _status = "";

    internal abstract WorkerRole Role { get; }

    protected ZNetView Nview { get; private set; }
    protected Humanoid Humanoid { get; private set; }
    protected Character Character { get; private set; }
    protected MonsterAI Ai { get; private set; }
    internal WorkerEquipment Equipment { get; private set; }
    internal WorkerNavigator Navigator { get; private set; }
    internal WorkerLogistics Logistics { get; private set; }
    internal WorkerAnimation Animation { get; private set; }
    internal WorkerSleep Sleep { get; private set; }
    internal WorkerWander Wander { get; private set; }

    /// <summary>True while the worker is lying in its bed.</summary>
    public bool IsAsleep => Sleep != null && Sleep.IsAsleep;

    /// <summary>Asks for the next round to start after this many seconds instead of the usual interval (only once).</summary>
    protected void RetrySoon(float seconds)
    {
        _retrySoon = seconds;
    }

    /// <summary>Optional reason shown on the bed instead of "Idle", e.g. "Waiting for items in the input chest".</summary>
    protected string IdleNote { get; set; }

    /// <summary>Position of the worker's bed; the centre of its wander and job radius.</summary>
    protected Vector3 Home { get; private set; }

    protected bool InRound { get; private set; }

    /// <summary>True while a task round is running and this worker is steering itself.</summary>
    public bool IsWorking => InRound;

    // ------------------------------------------------------------ role hooks

    /// <summary>
    /// Whether this worker's job uses the item (by shared name). Workers that use the same item share a chest's
    /// stock of it equally, so one of them cannot take it all. Roles that consume items override this.
    /// </summary>
    internal virtual bool WantsItem(string sharedName)
    {
        return false;
    }

    /// <summary>How many loaded workers near a point use this item, and how much of it they are holding between them.</summary>
    internal static void CountConsumers(string sharedName, Vector3 near, float radius, out int workers, out int carried)
    {
        workers = 0;
        carried = 0;
        foreach (WorkerBrain worker in Instances.Values)
        {
            if (worker != null && worker.Logistics != null && Utils.DistanceXZ(worker.Home, near) <= radius
                && worker.WantsItem(sharedName))
            {
                workers++;
                carried += worker.Logistics.Carried.CountItems(sharedName);
            }
        }
    }

    /// <summary>Find this round's work. Return true if there is any (and the round should start).</summary>
    protected abstract bool BeginRound();

    /// <summary>
    /// Advance the current round. Return true if this frame was handled. Call <see cref="EndRound"/>
    /// when there is nothing left to do.
    /// </summary>
    protected abstract bool RunTask(float dt);

    /// <summary>Status shown on the bed while between rounds.</summary>
    protected virtual string IdleStatus()
    {
        return "Idle";
    }

    /// <summary>Called once a round ends, whether it finished or was interrupted.</summary>
    protected virtual void OnRoundEnded(bool finished)
    {
    }

    // ------------------------------------------------------------ lifecycle

    protected virtual void Awake()
    {
        Nview = GetComponent<ZNetView>();
        Humanoid = GetComponent<Humanoid>();
        Character = GetComponent<Character>();
        Ai = GetComponent<MonsterAI>();
        Equipment = new WorkerEquipment(Humanoid, Role.ToolPrefab);
        Navigator = new WorkerNavigator(Ai, Character, Role);
        Logistics = new WorkerLogistics(Humanoid, Navigator, () => Home, () => WorkerConfig.JobRadius.Value + TaskRadiusTolerance);
        Animation = new WorkerAnimation(Character, Role);
        Sleep = new WorkerSleep(Nview, Character, Ai);
        Wander = new WorkerWander(transform, Ai, Navigator, DistanceToNearestOther);
        Humanoid.GetInventory().m_width = WorkerConfig.InventoryWidth.Value;
        Humanoid.GetInventory().m_height = WorkerConfig.InventoryHeight.Value;
        _nextRound = Time.time + FirstRoundDelay;
        Instances[gameObject] = this;
    }

    protected virtual void Start()
    {
        ApplyHome();
        _lastHealth = Character.GetHealth();
    }

    private void LateUpdate()
    {
        Sleep.ApplyPose();
    }

    private void OnDestroy()
    {
        _ = Instances.Remove(gameObject);
    }

    /// <summary>Called by the bed right after it spawns this worker and writes the ZDO fields.</summary>
    public void OnBound()
    {
        ApplyHome();
    }

    private void ApplyHome()
    {
        if (Nview == null || !Nview.IsValid())
        {
            return;
        }

        Vector3 home = Nview.GetZDO().GetVec3(WorkerBed.HomeKey, Vector3.zero);
        if (home == Vector3.zero)
        {
            return;
        }

        Home = home;
        _hasHome = true;

        // Vanilla idle wandering is centred on m_spawnPoint within m_randomMoveRange.
        Ai.m_spawnPoint = home;
        Ai.m_randomMoveRange = WorkerConfig.JobRadius.Value;
    }

    // ------------------------------------------------------------ main loop

    /// <summary>Returns true if this frame's AI update was handled and the vanilla AI should be skipped.</summary>
    public bool Tick(MonsterAI ai, float dt)
    {
        if (Nview == null || !Nview.IsValid() || !Nview.IsOwner())
        {
            _wasOwner = false;
            return false;
        }

        // Whoever owns the worker holds its cargo: pick it up from the saved copy when ownership is gained
        // (including on every world load), and keep that copy current while it is carried.
        if (!_wasOwner)
        {
            _wasOwner = true;
            LoadCargo();
        }
        _cargoTimer -= dt;
        if (_cargoTimer <= 0f)
        {
            _cargoTimer = CargoSaveInterval;
            SaveCargo();
        }

        if (!_hasHome)
        {
            ApplyHome();
            if (!_hasHome)
            {
                return false;
            }
        }

        if (!CheckBelongsToBed(dt))
        {
            return false;
        }

        UpdateThreat(dt);
        bool tookDamage = Character.GetHealth() < _lastHealth - 0.01f;
        _lastHealth = Character.GetHealth();
        bool restTime = EnvMan.IsNight() && !WorkerConfig.WorkAtNight.Value;
        if (tookDamage)
        {
            _engagedUntil = Time.time + EngagedHold;
        }
        bool engaged = _threatened || Time.time < _engagedUntil;
        if (!engaged && !InRound)
        {
            ForgetDistantTargets(ai);
        }
        bool hasTarget = ai.GetTargetCreature() != null || ai.GetStaticTarget() != null;

        Equipment.Update(restTime || Sleep.IsAsleep);

        if (InRound)
        {
            if (_threatened || tookDamage || restTime)
            {
                EndRound(finished: false);
                // Hand control straight back to the vanilla AI so it can defend or go to bed.
                return false;
            }
            return RunTask(dt);
        }

        SetStatus(restTime ? "Resting" : IdleNote ?? IdleStatus());

        // At night go to bed and lie there until morning; anything dangerous (or daylight) gets us up again.
        bool asleep = Sleep.IsAsleep;
        bool disturbed = _threatened || tookDamage || (!asleep && (hasTarget || ai.IsAlerted()));
        if (restTime && !disturbed)
        {
            if (Sleep.Rest(dt, BedId, out string sleepStatus))
            {
                SetStatus(sleepStatus);
                return true;
            }
        }
        else
        {
            WorkerBed bed = WorkerBed.FindLoaded(BedId);
            Sleep.WakeUp(bed != null ? bed.transform.position : Vector3.zero);
        }

        if (_threatened || tookDamage || hasTarget || ai.IsAlerted())
        {
            return false;
        }

        if (!restTime && Time.time >= _nextRound)
        {
            if (!ChestsReady())
            {
                return Idle(dt);
            }

            IdleNote = null;
            InRound = true;
            if (BeginRound())
            {
                // Roles with a tool have already taken it out; this makes sure every role starts with free hands.
                Equipment.EquipTool();
                return RunTask(dt);
            }
            InRound = false;
            _nextRound = Time.time + (_retrySoon > 0f ? _retrySoon : Role.Interval.Value);
            _retrySoon = 0f;
            return false;
        }

        return Idle(dt);
    }

    protected void EndRound(bool finished)
    {
        InRound = false;
        _nextRound = Time.time + Role.Interval.Value;
        Ai.StopMoving();
        Equipment.ReleaseTool();
        OnRoundEnded(finished);
        DvergrWorkersPlugin.Log.LogInfo(Role.Name + " round " + (finished ? "finished" : "interrupted"));
    }

    /// <summary>
    /// A role that needs input and/or output chests does nothing until they are built within its job radius.
    /// </summary>
    private bool ChestsReady()
    {
        ChestUse needs = Role.Chests;
        if ((needs & ChestUse.Input) != 0 && !Logistics.HasChest(ChestKind.Input))
        {
            IdleNote = "Needs an input chest nearby";
            return false;
        }
        if ((needs & ChestUse.Output) != 0 && !Logistics.HasChest(ChestKind.Output))
        {
            IdleNote = "Needs an output chest nearby";
            return false;
        }
        return true;
    }

    /// <summary>
    /// The vanilla AI targets anything it senses within its view range, well beyond <see cref="WorkerConfig.ThreatRadius"/>,
    /// and then runs its combat logic, which sends a mage with a support staff off to "heal" the nearest friend
    /// (the player). Unless something is really threatening this worker, such targets and the lingering alert
    /// are dropped so it carries on with its idle behaviour.
    /// </summary>
    private static void ForgetDistantTargets(MonsterAI ai)
    {
        if (ai.GetTargetCreature() != null || ai.GetStaticTarget() != null || ai.IsAlerted())
        {
            ai.m_targetCreature = null;
            ai.m_targetStatic = null;
            ai.SetAlerted(alert: false);
        }
    }

    /// <summary>Between rounds: head back if pushed out of the radius, otherwise stroll about. The vanilla idle AI never runs.</summary>
    private bool Idle(float dt)
    {
        return Leash(dt) || Wander.Update(dt, Home);
    }

    /// <summary>Distance from a point to the closest other loaded worker (so idle workers do not huddle).</summary>
    private float DistanceToNearestOther(Vector3 point)
    {
        float nearest = float.MaxValue;
        foreach (WorkerBrain other in Instances.Values)
        {
            if (other != null && other != this)
            {
                nearest = Mathf.Min(nearest, Vector3.Distance(other.transform.position, point));
            }
        }
        return nearest;
    }

    /// <summary>Keeps the idle worker inside its radius even if something pushed it out.</summary>
    private bool Leash(float dt)
    {
        float radius = WorkerConfig.JobRadius.Value;
        if (Utils.DistanceXZ(transform.position, Home) <= radius + LeashTolerance)
        {
            return false;
        }

        SetStatus("Heading back to bed");
        _ = Ai.MoveTo(dt, Home, radius * 0.5f, false);
        return true;
    }

    /// <summary>True if a point is inside the job radius (plus the task tolerance).</summary>
    protected bool InJobArea(Vector3 point)
    {
        return Utils.DistanceXZ(point, Home) <= WorkerConfig.JobRadius.Value + TaskRadiusTolerance;
    }

    private void UpdateThreat(float dt)
    {
        _threatTimer -= dt;
        if (_threatTimer > 0f)
        {
            return;
        }
        _threatTimer = 0.5f;

        _threatened = false;
        float range = WorkerConfig.ThreatRadius.Value;
        foreach (Character other in Character.GetAllCharacters())
        {
            if (other == Character || other.IsDead() || !BaseAI.IsEnemy(Character, other))
            {
                continue;
            }
            if (Vector3.Distance(other.transform.position, transform.position) < range)
            {
                _threatened = true;
                return;
            }
        }
    }

    /// <summary>
    /// Despawns this worker if it is a rogue: it belongs to no bed, its bed has been removed, or another
    /// worker for the same bed is older. Returns false if the worker was removed.
    /// </summary>
    private bool CheckBelongsToBed(float dt)
    {
        _bedCheckTimer -= dt;
        if (_bedCheckTimer > 0f)
        {
            return true;
        }
        _bedCheckTimer = 10f;

        string bedId = BedId;
        if (bedId.Length == 0)
        {
            return Despawn("worker without a bed");
        }

        // Two workers for one bed: the newer one goes.
        foreach (WorkerBrain other in Instances.Values)
        {
            if (other != this && other.BedId == bedId && other.IsOlderThan(this))
            {
                return Despawn("duplicate worker");
            }
        }

        // The bed lives where the worker's home is, so once that area is loaded the bed must be there.
        // Require it to be missing for several checks in a row so a bed that is still loading in is not mistaken for a removed one.
        if (!WorkerBed.IsLoaded(bedId) && ZoneSystem.instance.IsZoneLoaded(Home))
        {
            if (++_bedMissingChecks >= 3)
            {
                return Despawn("bed removed");
            }
        }
        else
        {
            _bedMissingChecks = 0;
        }
        return true;
    }

    private bool Despawn(string reason)
    {
        DvergrWorkersPlugin.Log.LogInfo("Despawning " + Role.Name + " Dvergr: " + reason);
        DropCargo();
        Equipment.Discard();
        ZNetScene.instance.Destroy(gameObject);
        return false;
    }

    // ------------------------------------------------------------ cargo

    private const string CargoKey = "DW_Cargo";
    private const float CargoSaveInterval = 1f;

    private bool _wasOwner;
    private float _cargoTimer;
    private string _savedCargo = "";
    private HashSet<string> _defaultNames;

    /// <summary>
    /// Items the Dvergr was given at spawn (its armour, its crossbow...) are not cargo. Everything else it picks
    /// up or fetches, other than its tool, is.
    /// </summary>
    private bool IsCargo(ItemDrop.ItemData item)
    {
        return item != null && !item.m_equipped && item.m_dropPrefab != null
            && (string.IsNullOrEmpty(Role.ToolPrefab) || item.m_dropPrefab.name != Role.ToolPrefab) && !DefaultItemNames().Contains(item.m_shared.m_name);
    }

    private HashSet<string> DefaultItemNames()
    {
        if (_defaultNames != null)
        {
            return _defaultNames;
        }

        _defaultNames = [];
        List<GameObject> prefabs =
        [
            .. Humanoid.m_defaultItems,
            .. Humanoid.m_randomWeapon,
            .. Humanoid.m_randomArmor,
            .. Humanoid.m_randomShield,
        ];
        foreach (Humanoid.ItemSet set in Humanoid.m_randomSets)
        {
            prefabs.AddRange(set.m_items);
        }
        foreach (Humanoid.RandomItem random in Humanoid.m_randomItems)
        {
            prefabs.Add(random.m_prefab);
        }

        foreach (GameObject prefab in prefabs)
        {
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop != null)
            {
                _ = _defaultNames.Add(drop.m_itemData.m_shared.m_name);
            }
        }
        return _defaultNames;
    }

    private List<ItemDrop.ItemData> CargoItems()
    {
        List<ItemDrop.ItemData> cargo = [];
        foreach (ItemDrop.ItemData item in Humanoid.GetInventory().GetAllItems())
        {
            if (IsCargo(item))
            {
                cargo.Add(item);
            }
        }
        return cargo;
    }

    /// <summary>Writes what the worker is carrying into its saved data, so it survives the worker being unloaded.</summary>
    private void SaveCargo()
    {
        List<string> parts = [];
        foreach (ItemDrop.ItemData item in CargoItems())
        {
            parts.Add(item.m_dropPrefab.name + "|" + item.m_stack + "|" + item.m_quality);
        }

        string cargo = string.Join(";", parts);
        if (cargo != _savedCargo)
        {
            _savedCargo = cargo;
            Nview.GetZDO().Set(CargoKey, cargo);
        }
    }

    private void LoadCargo()
    {
        Inventory inventory = Humanoid.GetInventory();
        foreach (ItemDrop.ItemData stale in CargoItems())
        {
            _ = inventory.RemoveItem(stale);
        }

        _savedCargo = Nview.GetZDO().GetString(CargoKey, "");
        if (_savedCargo.Length == 0)
        {
            return;
        }

        foreach (string entry in _savedCargo.Split([';'], StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = entry.Split('|');
            GameObject prefab = fields.Length >= 2 ? ObjectDB.instance.GetItemPrefab(fields[0]) : null;
            if (prefab == null || !int.TryParse(fields[1], out int amount))
            {
                continue;
            }

            int max = prefab.GetComponent<ItemDrop>().m_itemData.m_shared.m_maxStackSize;
            while (amount > 0)
            {
                int stack = Math.Min(amount, max);
                _ = inventory.AddItem(prefab, stack);
                amount -= stack;
            }
        }
        DvergrWorkersPlugin.Log.LogInfo(Role.Name + " Dvergr is carrying its saved items again");
    }

    /// <summary>Puts everything the worker is carrying on the ground where it stands (it died, or its bed was removed).</summary>
    internal void DropCargo()
    {
        if (Nview == null || !Nview.IsValid() || !Nview.IsOwner())
        {
            return;
        }

        Inventory inventory = Humanoid.GetInventory();
        int dropped = 0;
        foreach (ItemDrop.ItemData item in CargoItems())
        {
            Vector3 position = transform.position + (Vector3.up * 0.5f) + (UnityEngine.Random.insideUnitSphere * 0.3f);
            _ = ItemDrop.DropItem(item, item.m_stack, position, Quaternion.Euler(0f, UnityEngine.Random.Range(0, 360), 0f));
            _ = inventory.RemoveItem(item);
            dropped++;
        }

        _savedCargo = "";
        Nview.GetZDO().Set(CargoKey, "");
        if (dropped > 0)
        {
            DvergrWorkersPlugin.Log.LogInfo(Role.Name + " Dvergr dropped " + dropped + " carried stack(s)");
        }
    }

    internal string BedId => Nview != null && Nview.IsValid() ? Nview.GetZDO().GetString(WorkerBed.BedIdKey, "") : "";

    private long Born => Nview.GetZDO().GetLong(WorkerBed.BornKey, long.MaxValue);

    private bool IsOlderThan(WorkerBrain other)
    {
        long mine = Born;
        long theirs = other.Born;
        return mine != theirs ? mine < theirs : GetInstanceID() < other.GetInstanceID();
    }

    /// <summary>True if a worker for this bed is currently loaded.</summary>
    public static bool IsLoaded(string bedId)
    {
        return FindLoaded(bedId) != null;
    }

    public static WorkerBrain FindLoaded(string bedId)
    {
        if (bedId.Length == 0)
        {
            return null;
        }
        foreach (WorkerBrain worker in Instances.Values)
        {
            if (worker != null && worker.BedId == bedId)
            {
                return worker;
            }
        }
        return null;
    }

    public string GetStatus()
    {
        return Nview != null && Nview.IsValid() ? Nview.GetZDO().GetString(WorkerBed.StatusKey, "Idle") : "Idle";
    }

    protected void SetStatus(string status)
    {
        if (status == _status)
        {
            return;
        }
        _status = status;
        Nview.GetZDO().Set(WorkerBed.StatusKey, status);
    }
}
