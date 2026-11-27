using System;

namespace DvergrWorkers.Core;

/// <summary>
/// Marks a Harmony patch class as part of a feature that can be turned off on its own, falling back to vanilla
/// behaviour. Patch classes without it are core: if one of them breaks, the whole mod turns off. See
/// <see cref="SafePatcher"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
internal sealed class FeatureAttribute(string name) : Attribute
{
    /// <summary>The name shown in the log and accepted by the DisabledFeatures debug setting.</summary>
    public string Name { get; } = name;
}
