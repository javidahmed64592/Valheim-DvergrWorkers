using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

using HarmonyLib;

namespace DvergrWorkers.Core;

/// <summary>
/// <para>
/// Applies the mod's Harmony patches so that a game update breaks as little as possible. Patch classes are grouped
/// into features with <see cref="FeatureAttribute"/>; classes without one are core. Core classes are patched
/// first, and if any of them fails, everything is unpatched and the mod is off. Then each feature is patched, and if
/// one of its classes fails, only that feature is taken back out. Each broken feature gets one line in the log.
/// </para>
/// <para>
/// Before a class is patched, the IL of every method it can reach in this mod is read and each method, field and
/// type it uses is looked up, so one the game no longer has fails here rather than in game. (Mono only compiles a
/// method when it first runs, and <c>RuntimeHelpers.PrepareMethod</c> doesn't change that.) The reading follows
/// direct calls only, so every patched method also gets a finalizer: a <see cref="MissingMemberException"/> or
/// <see cref="TypeLoadException"/> thrown from this mod's code turns off the feature it came from (or the whole
/// mod, for core code) and that one exception is swallowed. Every other exception passes through untouched.
/// </para>
/// <para>This file is the same in every mod apart from its namespace.</para>
/// </summary>
internal static class SafePatcher
{
    private const string DebugSection = "Debug";
    private const string DisabledFeaturesKey = "DisabledFeatures";

    private static readonly Assembly OurAssembly = typeof(SafePatcher).Assembly;
    private static readonly MethodInfo GuardMethod = AccessTools.Method(typeof(SafePatcher), nameof(Guard));

    /// <summary>Each patch class and the feature it belongs to, or null for core.</summary>
    private static readonly Dictionary<Type, string> Owners = [];

    /// <summary>The patch classes of each feature.</summary>
    private static readonly Dictionary<string, List<Type>> Features = [];

    private static readonly HashSet<string> Off = [];

    /// <summary>Methods whose IL has been read, and the methods of this mod each one calls.</summary>
    private static readonly Dictionary<MethodBase, List<MethodBase>> Callees = [];

    /// <summary>Every opcode by its value, for reading raw IL.</summary>
    private static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(field => (OpCode)field.GetValue(null))
        .ToDictionary(code => code.Value);

    private static Harmony _harmony;
    private static ManualLogSource _log;

    /// <summary>False once a core patch has failed: the mod is unpatched and should do nothing else.</summary>
    public static bool ModEnabled { get; private set; }

    /// <summary>Whether a feature is patched and working, for code outside its patches that depends on it.</summary>
    public static bool IsOn(string feature)
    {
        return ModEnabled && Features.ContainsKey(feature) && !Off.Contains(feature);
    }

    /// <summary>
    /// Patches every patch class in this mod. Returns false when the mod is off because a core patch failed, in which
    /// case the plugin should skip the rest of its setup.
    /// </summary>
    public static bool PatchAll(Harmony harmony, ConfigFile config, ManualLogSource log)
    {
        _harmony = harmony;
        _log = log;

        List<Type> core = [];
        foreach (Type type in AccessTools.GetTypesFromAssembly(OurAssembly))
        {
            if (!type.IsDefined(typeof(HarmonyPatch), false))
            {
                continue;
            }

            string feature = type.GetCustomAttribute<FeatureAttribute>()?.Name;
            Owners[type] = feature;
            if (feature == null)
            {
                core.Add(type);
            }
            else if (Features.TryGetValue(feature, out List<Type> types))
            {
                types.Add(type);
            }
            else
            {
                Features[feature] = [type];
            }
        }

        HashSet<string> disabled = ReadDisabledFeatures(config);

        foreach (Type type in core)
        {
            if (!TryPatch(type, out string reason))
            {
                TurnModOff(type.Name + " couldn't be patched (" + reason + ")");
                return false;
            }
        }

        ModEnabled = true;

        foreach (KeyValuePair<string, List<Type>> feature in Features)
        {
            if (disabled.Contains(feature.Key))
            {
                _ = Off.Add(feature.Key);
                _log.LogInfo("Feature \"" + feature.Key + "\" is off: turned off in the config.");
                continue;
            }

            foreach (Type type in feature.Value)
            {
                if (!TryPatch(type, out string reason))
                {
                    _ = Off.Add(feature.Key);
                    Unpatch(feature.Value);
                    _log.LogError("Feature \"" + feature.Key + "\" is off: " + type.Name + " couldn't be patched (" +
                        reason + "). The game may have changed; the rest of the mod still works.");
                    break;
                }
            }
        }

        AddGuards();
        return true;
    }

    private static HashSet<string> ReadDisabledFeatures(ConfigFile config)
    {
        HashSet<string> disabled = [];
        if (Features.Count == 0)
        {
            return disabled;
        }

        string names = string.Join(", ", Features.Keys);
        ConfigEntry<string> entry = config.Bind(DebugSection, DisabledFeaturesKey, "",
            "For testing: features to turn off, separated by commas. Leave empty to play normally. " +
            "Features: " + names + ".");

        foreach (string part in entry.Value.Split(','))
        {
            string name = part.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            // Typed by hand, so the case doesn't have to match.
            string feature = Features.Keys.FirstOrDefault(key =>
                string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
            if (feature != null)
            {
                _ = disabled.Add(feature);
            }
            else
            {
                _log.LogWarning(DisabledFeaturesKey + ": there is no feature called \"" + name + "\". Features: " +
                    names + ".");
            }
        }

        return disabled;
    }

    private static bool TryPatch(Type type, out string reason)
    {
        try
        {
            // Patched first so a class that skips itself through Prepare() isn't checked. Nothing in the game runs
            // during startup, so a broken patch is taken back out before it is ever called.
            List<MethodInfo> patched = _harmony.CreateClassProcessor(type).Patch();
            reason = patched is { Count: > 0 } ? FindMissingMember(type) : null;
            if (reason != null)
            {
                Unpatch([type]);
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            reason = Describe(e);
            return false;
        }
    }

    /// <summary>
    /// Reads every method of the class (and its nested classes, which hold its lambdas) and every method of this mod
    /// they call, looking up each member they use. Returns why one couldn't be found, or null if all were.
    /// </summary>
    private static string FindMissingMember(Type type)
    {
        Stack<MethodBase> todo = [];
        foreach (Type declaring in AccessTools.GetTypesFromAssembly(OurAssembly).Where(t => Outermost(t) == type))
        {
            foreach (MethodBase method in declaring.GetMethods(AccessTools.allDeclared))
            {
                todo.Push(method);
            }

            foreach (MethodBase constructor in declaring.GetConstructors(AccessTools.allDeclared))
            {
                todo.Push(constructor);
            }
        }

        HashSet<MethodBase> seen = [];
        while (todo.Count > 0)
        {
            MethodBase method = todo.Pop();
            if (!seen.Add(method))
            {
                continue;
            }

            if (!Callees.TryGetValue(method, out List<MethodBase> callees))
            {
                callees = ReadCallees(method, out string reason);
                if (reason != null)
                {
                    return method.DeclaringType?.Name + "." + method.Name + " uses something the game no longer has (" +
                        reason + ")";
                }

                Callees[method] = callees;
            }

            foreach (MethodBase callee in callees)
            {
                todo.Push(callee);
            }
        }

        return null;
    }

    /// <summary>
    /// The methods of this mod that the method calls or makes delegates of, and the static constructors of the
    /// classes it uses; or null and the reason when a member it uses can't be found.
    /// </summary>
    private static List<MethodBase> ReadCallees(MethodBase method, out string reason)
    {
        reason = null;
        List<MethodBase> callees = [];
        byte[] il = method.IsAbstract ? null : method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            return callees;
        }

        Type[] typeArguments = method.DeclaringType is { IsGenericType: true } declaring
            ? declaring.GetGenericArguments()
            : null;
        Type[] methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

        try
        {
            foreach (int token in MemberTokens(il))
            {
                // Resolving the token is the same lookup the runtime makes when it compiles the method, so it throws
                // for a member the game no longer has. (Harmony's own IL reader leaves those unresolved instead.)
                MemberInfo member = method.Module.ResolveMember(token, typeArguments, methodArguments);
                if (member is MethodBase callee && callee.DeclaringType?.Assembly == OurAssembly)
                {
                    callees.Add(callee is MethodInfo { IsGenericMethod: true } generic
                        ? generic.GetGenericMethodDefinition()
                        : callee);
                }

                // Using a class runs its static constructor (field initializers) first.
                Type used = member as Type ?? member.DeclaringType;
                if (used?.Assembly == OurAssembly && used.TypeInitializer != null)
                {
                    callees.Add(used.TypeInitializer);
                }
            }
        }
        catch (Exception e)
        {
            reason = Describe(e);
            return null;
        }

        return callees;
    }

    /// <summary>The metadata tokens of the methods, fields and types the IL uses.</summary>
    private static IEnumerable<int> MemberTokens(byte[] il)
    {
        int position = 0;
        while (position < il.Length)
        {
            short value = il[position++];
            if (value == 0xFE)
            {
                value = (short)(0xFE00 | il[position++]);
            }

            OpCode code = OpCodesByValue[value];
            switch (code.OperandType)
            {
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                    yield return BitConverter.ToInt32(il, position);
                    position += 4;
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar:
                    position++;
                    break;
                case OperandType.InlineVar:
                    position += 2;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    position += 8;
                    break;
                case OperandType.InlineSwitch:
                    position += 4 + (BitConverter.ToInt32(il, position) * 4);
                    break;
                case OperandType.InlineNone:
                    break;
                default:
                    // InlineBrTarget, InlineI, InlineSig, InlineString, ShortInlineR.
                    position += 4;
                    break;
            }
        }
    }

    /// <summary>Removes the patches these classes made, and any guard that no longer covers a patch.</summary>
    private static void Unpatch(List<Type> types)
    {
        foreach (MethodBase original in _harmony.GetPatchedMethods().ToList())
        {
            foreach (MethodInfo patch in OurPatches(original).ToList())
            {
                if (types.Contains(Outermost(patch.DeclaringType)))
                {
                    _harmony.Unpatch(original, patch);
                }
            }

            if (OurPatches(original).All(patch => patch == GuardMethod))
            {
                _harmony.Unpatch(original, GuardMethod);
            }
        }
    }

    private static IEnumerable<MethodInfo> OurPatches(MethodBase original)
    {
        HarmonyLib.Patches info = Harmony.GetPatchInfo(original);
        return info == null
            ? []
            : info.Prefixes.Concat(info.Postfixes).Concat(info.Transpilers).Concat(info.Finalizers)
                .Where(patch => patch.owner == _harmony.Id)
                .Select(patch => patch.PatchMethod);
    }

    private static void AddGuards()
    {
        foreach (MethodBase original in _harmony.GetPatchedMethods().ToList())
        {
            try
            {
                _ = _harmony.Patch(original, finalizer: new HarmonyMethod(GuardMethod));
            }
            catch (Exception e)
            {
                _log.LogWarning("Couldn't guard " + original.DeclaringType?.Name + "." + original.Name + " (" +
                    Describe(e) + "); a game update that breaks it will show up as errors instead.");
            }
        }
    }

    /// <summary>Harmony finalizer on every patched method. Returning null swallows the exception.</summary>
    private static Exception Guard(Exception __exception, MethodBase __originalMethod)
    {
        if (!ModEnabled || Innermost(__exception) is not (MissingMemberException or TypeLoadException))
        {
            return __exception;
        }

        HashSet<string> owners = FindOwners(__exception, __originalMethod, out bool core);
        if (core)
        {
            TurnModOff(Describe(__exception));
            return null;
        }

        if (owners.Count == 0)
        {
            // Not from this mod's code, e.g. another mod's patch on the same method.
            return __exception;
        }

        foreach (string feature in owners)
        {
            if (Off.Add(feature))
            {
                _log.LogError("Feature \"" + feature + "\" is off: " + Describe(__exception) +
                    ". The game may have changed; the rest of the mod still works.");
                List<Type> types = Features[feature];
                ThreadingHelper.Instance.StartSyncInvoke(() => Unpatch(types));
            }
        }

        return null;
    }

    /// <summary>
    /// The features the exception came from. A patch class on the stack decides it; if the stack only has other code
    /// of this mod (a helper a transpiler calls), every feature patching this method is blamed.
    /// </summary>
    private static HashSet<string> FindOwners(Exception exception, MethodBase original, out bool core)
    {
        HashSet<string> owners = [];
        core = false;
        bool ours = false;

        foreach (StackFrame frame in new StackTrace(exception, false).GetFrames() ?? [])
        {
            Type type = Outermost(frame.GetMethod()?.DeclaringType);
            if (type == null || type.Assembly != OurAssembly)
            {
                continue;
            }

            ours = true;
            if (Owners.TryGetValue(type, out string feature))
            {
                core = feature == null;
                if (!core)
                {
                    _ = owners.Add(feature);
                }

                return owners;
            }
        }

        if (!ours)
        {
            return owners;
        }

        foreach (MethodInfo patch in OurPatches(original))
        {
            if (patch != GuardMethod && Owners.TryGetValue(Outermost(patch.DeclaringType), out string feature))
            {
                if (feature == null)
                {
                    core = true;
                }
                else
                {
                    _ = owners.Add(feature);
                }
            }
        }

        return owners;
    }

    private static void TurnModOff(string reason)
    {
        bool wasEnabled = ModEnabled;
        ModEnabled = false;
        _log.LogError("The mod is off: " + reason + ". The game may have changed; wait for an update of this mod.");

        if (wasEnabled)
        {
            // Called from inside a patched method: unpatch once it has returned.
            ThreadingHelper.Instance.StartSyncInvoke(_harmony.UnpatchSelf);
        }
        else
        {
            _harmony.UnpatchSelf();
        }
    }

    /// <summary>The top-level class a nested or compiler-generated class belongs to.</summary>
    private static Type Outermost(Type type)
    {
        while (type?.DeclaringType != null)
        {
            type = type.DeclaringType;
        }

        return type;
    }

    /// <summary>The exception at the bottom of a chain, e.g. the one inside a TypeInitializationException.</summary>
    private static Exception Innermost(Exception exception)
    {
        while (exception?.InnerException != null)
        {
            exception = exception.InnerException;
        }

        return exception;
    }

    /// <summary>The innermost exception's type and the first line of its message.</summary>
    private static string Describe(Exception exception)
    {
        exception = Innermost(exception);
        string message = exception.Message.Split('\n')[0].Trim();
        return exception.GetType().Name + ": " + message;
    }
}
