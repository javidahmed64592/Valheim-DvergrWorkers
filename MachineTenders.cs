namespace DvergrWorkers
{
    /// <summary>Tends charcoal kilns, smelters and blast furnaces.</summary>
    public class BlacksmithWorker : MachineTenderWorker
    {
        internal override WorkerRole Role => WorkerRoles.Blacksmith;

        protected override string AllowedMachines => WorkerConfig.BlacksmithMachines.Value;

        protected override string NoMachinesNote => "No kiln, smelter or furnace nearby";
    }

    /// <summary>Feeds barley to windmills and collects the flour.</summary>
    public class BarleyWorker : MachineTenderWorker
    {
        internal override WorkerRole Role => WorkerRoles.Barley;

        protected override string AllowedMachines => WorkerConfig.BarleyMachines.Value;

        protected override string NoMachinesNote => "No windmill nearby";
    }

    /// <summary>Feeds flax to spinning wheels and collects the linen thread.</summary>
    public class FlaxWorker : MachineTenderWorker
    {
        internal override WorkerRole Role => WorkerRoles.Flax;

        protected override string AllowedMachines => WorkerConfig.FlaxMachines.Value;

        protected override string NoMachinesNote => "No spinning wheel nearby";
    }
}
