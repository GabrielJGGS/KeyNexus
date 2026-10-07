using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using KeyNexus.Core.Input;

namespace KeyNexus.Core.Profiles;

internal enum CompiledActionKind
{
    Key,
    Text,
    Macro
}

internal readonly record struct CompiledMacroStep(int Vk, int Modifiers, KeyStroke BoundKey, string Text, int DelayMs);

internal sealed class CompiledAction
{
    public CompiledActionKind Kind { get; init; }
    public int OutputVk { get; init; }
    public int OutputModifiers { get; init; }
    /// <summary>Scan code já calculado no layout vinculado; vazio quando o teclado não tem layout.</summary>
    public KeyStroke BoundKey { get; init; }
    public string Text { get; init; } = string.Empty;
    public CompiledMacroStep[] Steps { get; init; } = Array.Empty<CompiledMacroStep>();
    public bool HasDelays { get; init; }
    public int MaxInputs { get; init; }
}

internal sealed class DeviceProfile
{
    public DeviceProfile(
        string groupKey,
        string? layoutHklHex,
        IntPtr hkl,
        FrozenDictionary<int, CompiledAction> rules,
        ushort[]? scanToVk)
    {
        GroupKey = groupKey;
        LayoutHklHex = layoutHklHex;
        Hkl = hkl;
        Rules = rules;
        ScanToVk = scanToVk;
    }

    public string GroupKey { get; }
    public string? LayoutHklHex { get; }
    /// <summary>Layout vinculado; IntPtr.Zero quando o teclado usa o layout do Windows.</summary>
    public IntPtr Hkl { get; }
    public FrozenDictionary<int, CompiledAction> Rules { get; }
    /// <summary>Tabela scan code → tecla virtual do layout vinculado.</summary>
    public ushort[]? ScanToVk { get; }
    public bool HasRules => Rules.Count > 0;

    public static int RuleKey(int vk, int modifiers) => (vk & 0xFFFF) | (modifiers << 16);
}

internal sealed class ProfileSnapshot
{
    public static readonly ProfileSnapshot Empty =
        new(new Dictionary<string, DeviceProfile>().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));

    public ProfileSnapshot(FrozenDictionary<string, DeviceProfile> profiles) => Profiles = profiles;

    public FrozenDictionary<string, DeviceProfile> Profiles { get; }

    public DeviceProfile? Find(string? groupKey) =>
        !string.IsNullOrEmpty(groupKey) && Profiles.TryGetValue(groupKey, out var profile) ? profile : null;
}

internal static class ProfileCompiler
{
    public static ProfileSnapshot Compile(
        IEnumerable<KeyValuePair<string, string>> layouts,
        IEnumerable<KeyValuePair<string, List<RemapRule>>> remaps)
    {
        var hklByGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (group, hkl) in layouts)
        {
            if (!string.IsNullOrEmpty(group) && !string.IsNullOrEmpty(hkl))
                hklByGroup[group] = hkl;
        }

        var rulesByGroup = new Dictionary<string, List<RemapRule>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (group, rules) in remaps)
        {
            if (!string.IsNullOrEmpty(group) && rules is { Count: > 0 })
                rulesByGroup[group] = rules;
        }

        var groups = new HashSet<string>(hklByGroup.Keys, StringComparer.OrdinalIgnoreCase);
        groups.UnionWith(rulesByGroup.Keys);

        var profiles = new Dictionary<string, DeviceProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            hklByGroup.TryGetValue(group, out var hkl);
            rulesByGroup.TryGetValue(group, out var rules);
            profiles[group] = CompileProfile(group, hkl, rules);
        }

        return new ProfileSnapshot(profiles.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }

    public static DeviceProfile CompileProfile(string groupKey, string? hklHex, IReadOnlyList<RemapRule>? rules)
    {
        IntPtr hkl = LayoutKeyHelper.ParseHkl(hklHex);
        var compiled = new Dictionary<int, CompiledAction>();

        if (rules != null)
        {
            foreach (var rule in rules)
            {
                if (rule.TriggerVk <= 0)
                    continue;

                int key = DeviceProfile.RuleKey(rule.TriggerVk, ModifierFlags.Normalize(rule.Modifiers));
                if (compiled.ContainsKey(key))
                    continue;

                var action = CompileAction(rule, hkl);
                if (action != null)
                    compiled[key] = action;
            }
        }

        return new DeviceProfile(
            groupKey,
            string.IsNullOrEmpty(hklHex) ? null : hklHex,
            hkl,
            compiled.ToFrozenDictionary(),
            hkl != IntPtr.Zero ? ScanCodeTable.For(hkl) : null);
    }

    private static CompiledAction? CompileAction(RemapRule rule, IntPtr hkl)
    {
        switch (rule.OutputType)
        {
            case RemapOutputType.Key when rule.OutputVk > 0:
                int mods = ModifierFlags.Normalize(rule.OutputModifiers);
                return new CompiledAction
                {
                    Kind = CompiledActionKind.Key,
                    OutputVk = rule.OutputVk,
                    OutputModifiers = mods,
                    BoundKey = hkl != IntPtr.Zero ? KeyStroke.ForVk(rule.OutputVk, hkl) : default,
                    MaxInputs = 2 + 2 * ModifierKeyCount(mods)
                };

            case RemapOutputType.Text when !string.IsNullOrEmpty(rule.OutputText):
                return new CompiledAction
                {
                    Kind = CompiledActionKind.Text,
                    Text = rule.OutputText,
                    MaxInputs = 2 * rule.OutputText.Length
                };

            case RemapOutputType.Sequence when rule.Sequence is { Count: > 0 }:
                var steps = new List<CompiledMacroStep>(rule.Sequence.Count);
                int maxInputs = 0;
                bool hasDelays = false;
                foreach (var step in rule.Sequence)
                {
                    int stepMods = ModifierFlags.Normalize(step.Modifiers);
                    string text = step.Text ?? string.Empty;
                    if (string.IsNullOrEmpty(text) && step.Vk <= 0)
                        continue;

                    var bound = step.Vk > 0 && hkl != IntPtr.Zero ? KeyStroke.ForVk(step.Vk, hkl) : default;
                    steps.Add(new CompiledMacroStep(step.Vk, stepMods, bound, text, Math.Max(0, step.DelayMs)));
                    maxInputs += text.Length > 0 ? 2 * text.Length : 2 + 2 * ModifierKeyCount(stepMods);
                    hasDelays |= step.DelayMs > 0;
                }

                if (steps.Count == 0)
                    return null;

                return new CompiledAction
                {
                    Kind = CompiledActionKind.Macro,
                    Steps = steps.ToArray(),
                    HasDelays = hasDelays,
                    MaxInputs = maxInputs
                };
        }

        return null;
    }

    private static int ModifierKeyCount(int mods)
    {
        int count = 0;
        if (ModifierFlags.IsAltGr(mods)) count += 2;
        if ((mods & ModifierFlags.Ctrl) != 0) count++;
        if ((mods & ModifierFlags.Alt) != 0) count++;
        if ((mods & ModifierFlags.Shift) != 0) count++;
        if ((mods & ModifierFlags.Win) != 0) count++;
        return count;
    }
}

/// <summary>Tabela scan code → tecla virtual por layout, com a mesma regra da captura no editor.</summary>
internal static class ScanCodeTable
{
    private static readonly ConcurrentDictionary<IntPtr, ushort[]> Cache = new();

    public static ushort[] For(IntPtr hkl) => Cache.GetOrAdd(hkl, Build);

    public static int Lookup(ushort[] table, uint scanCode, bool extended) =>
        scanCode is > 0 and < 256 ? table[(extended ? 256 : 0) + (int)scanCode] : 0;

    private static ushort[] Build(IntPtr hkl)
    {
        var table = new ushort[512];
        for (uint scan = 1; scan < 256; scan++)
        {
            table[scan] = (ushort)Resolve(scan, extended: false, hkl);
            table[256 + scan] = (ushort)Resolve(scan, extended: true, hkl);
        }
        return table;
    }

    private static uint Resolve(uint scanCode, bool extended, IntPtr hkl)
    {
        if (extended)
        {
            uint vk = NativeMethods.MapVirtualKeyEx(scanCode | 0xE000, NativeMethods.MAPVK_VSC_TO_VK, hkl);
            if (vk != 0) return vk;
        }

        uint vkNormal = NativeMethods.MapVirtualKeyEx(scanCode, NativeMethods.MAPVK_VSC_TO_VK, hkl);
        if (vkNormal != 0)
            return vkNormal;

        if (!extended)
        {
            uint vkExt = NativeMethods.MapVirtualKeyEx(scanCode | 0xE000, NativeMethods.MAPVK_VSC_TO_VK, hkl);
            if (vkExt != 0) return vkExt;
        }

        return 0;
    }
}
