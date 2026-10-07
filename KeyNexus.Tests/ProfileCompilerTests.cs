using KeyNexus.Core;
using KeyNexus.Core.Profiles;

namespace KeyNexus.Tests;

public class ProfileCompilerTests
{
    private const string Group = "HID#TESTE";

    [Fact]
    public void LegacyCtrlAltRule_IsIndexedAsAltGr()
    {
        var snapshot = Compile(new RemapRule
        {
            TriggerVk = 0xBE,
            Modifiers = ModifierFlags.Ctrl | ModifierFlags.Alt,
            OutputType = RemapOutputType.Text,
            OutputText = ":"
        });

        var profile = snapshot.Find(Group);
        Assert.NotNull(profile);
        Assert.True(profile!.Rules.ContainsKey(DeviceProfile.RuleKey(0xBE, ModifierFlags.AltGr)));
    }

    [Fact]
    public void DuplicateTrigger_FirstRuleWins()
    {
        var snapshot = Compile(
            new RemapRule { TriggerVk = 0x41, OutputType = RemapOutputType.Text, OutputText = "primeira" },
            new RemapRule { TriggerVk = 0x41, OutputType = RemapOutputType.Text, OutputText = "segunda" });

        var action = snapshot.Find(Group)!.Rules[DeviceProfile.RuleKey(0x41, 0)];
        Assert.Equal("primeira", action.Text);
    }

    [Fact]
    public void KeyRuleOnBoundLayout_HasScanCodeReady()
    {
        var snapshot = Compile(new RemapRule { TriggerVk = 0x41, OutputType = RemapOutputType.Key, OutputVk = 0x42 });

        var action = snapshot.Find(Group)!.Rules[DeviceProfile.RuleKey(0x41, 0)];
        Assert.Equal(CompiledActionKind.Key, action.Kind);
        Assert.NotEqual(0, action.BoundKey.Scan);
    }

    [Fact]
    public void MacroWithDelay_IsMarkedForTheMacroThread()
    {
        var snapshot = Compile(new RemapRule
        {
            TriggerVk = 0x70,
            OutputType = RemapOutputType.Sequence,
            Sequence = new List<RemapSequenceStep> { new() { Vk = 0x41 }, new() { Vk = 0x42, DelayMs = 50 } }
        });

        var action = snapshot.Find(Group)!.Rules[DeviceProfile.RuleKey(0x70, 0)];
        Assert.True(action.HasDelays);
        Assert.Equal(2, action.Steps.Length);
    }

    private static ProfileSnapshot Compile(params RemapRule[] rules)
    {
        string hkl = KeyboardLayoutCatalog.FormatHkl(NativeMethods.GetKeyboardLayout(0).ToInt64());
        return ProfileCompiler.Compile(
            new Dictionary<string, string> { [Group] = hkl },
            new Dictionary<string, List<RemapRule>> { [Group] = rules.ToList() });
    }
}
