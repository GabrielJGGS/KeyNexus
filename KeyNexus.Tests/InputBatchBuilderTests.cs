using KeyNexus.Core;
using KeyNexus.Core.Input;
using KeyNexus.Core.Profiles;

namespace KeyNexus.Tests;

public class InputBatchBuilderTests
{
    private static readonly KeyStroke Period = new(0xBE, 0x34, false);

    private static CompiledAction KeyAction(KeyStroke key, int modifiers = 0) => new()
    {
        Kind = CompiledActionKind.Key,
        OutputVk = key.Vk,
        OutputModifiers = modifiers,
        BoundKey = key,
        MaxInputs = 12
    };

    private static List<string> Build(PhysicalModifiers held, CompiledAction action)
    {
        var buffer = new NativeMethods.INPUT[64];
        int n = InputBatchBuilder.BuildRemap(buffer, 0, held, action, IntPtr.Zero);
        return InputDescriber.Describe(buffer, n);
    }

    [Fact]
    public void AltGrHeld_KeyOutput_ReleasesAndRestoresOnlyRightAlt()
    {
        var events = Build(PhysicalModifiers.AltGrCtrl | PhysicalModifiers.RAlt, KeyAction(Period));

        // Sem "LCtrl down" na volta: o Windows recria o Ctrl do AltGr e o solta com o AltGr físico.
        Assert.Equal(new[]
        {
            "RAlt up", "LCtrl up",
            "sc34 down", "sc34 up",
            "RAlt down"
        }, events);
    }

    [Fact]
    public void RealCtrlHeld_IsRestoredBeforeRightAlt()
    {
        var events = Build(PhysicalModifiers.LCtrl | PhysicalModifiers.RAlt, KeyAction(Period));

        Assert.Equal(new[]
        {
            "RAlt up", "LCtrl up",
            "sc34 down", "sc34 up",
            "LCtrl down", "RAlt down"
        }, events);
    }

    [Fact]
    public void AltGrHeld_DoesNotNeedMenuMask()
    {
        Assert.False(InputBatchBuilder.NeedsMenuMask(PhysicalModifiers.AltGrCtrl | PhysicalModifiers.RAlt));
        Assert.DoesNotContain(Build(PhysicalModifiers.AltGrCtrl | PhysicalModifiers.RAlt, KeyAction(Period)),
            e => e.StartsWith("vkE8", StringComparison.Ordinal));
    }

    [Fact]
    public void ShiftHeld_TextOutput_TypesWithoutShift()
    {
        var text = new CompiledAction { Kind = CompiledActionKind.Text, Text = "?", MaxInputs = 2 };

        Assert.Equal(new[] { "LShift up", "'?' down", "'?' up", "LShift down" },
            Build(PhysicalModifiers.LShift, text));
    }

    [Fact]
    public void PlainAltHeld_SendsMenuMaskBeforeReleasingAlt()
    {
        var events = Build(PhysicalModifiers.LAlt, KeyAction(Period));

        Assert.Equal(new[]
        {
            "vkE8 down", "vkE8 up", "LAlt up",
            "sc34 down", "sc34 up",
            "LAlt down"
        }, events);
        Assert.True(InputBatchBuilder.NeedsMenuMask(PhysicalModifiers.LAlt));
    }

    [Fact]
    public void AltGrOutput_PressesCtrlAndRightAltAroundKey()
    {
        var events = Build(PhysicalModifiers.None, KeyAction(Period, ModifierFlags.AltGr));

        Assert.Equal(new[]
        {
            "LCtrl down", "RAlt down",
            "sc34 down", "sc34 up",
            "RAlt up", "LCtrl up"
        }, events);
    }

    [Fact]
    public void EveryEventCarriesTheKeyNexusSignature()
    {
        var buffer = new NativeMethods.INPUT[64];
        int n = InputBatchBuilder.BuildRemap(buffer, 0,
            PhysicalModifiers.LShift | PhysicalModifiers.LAlt, KeyAction(Period, ModifierFlags.Shift), IntPtr.Zero);

        Assert.True(n > 0);
        Assert.All(buffer.Take(n), input => Assert.True(InputDescriber.IsSignedByKeyNexus(input)));
    }
}
