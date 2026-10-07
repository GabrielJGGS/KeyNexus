using System.Diagnostics;
using Xunit.Abstractions;
using KeyNexus.Core;
using KeyNexus.Core.Input;
using KeyNexus.Core.Profiles;

namespace KeyNexus.Tests;

/// <summary>
/// Passa eventos físicos pelo hook real e aplica o que ele injeta num teclado lógico simulado,
/// na mesma ordem do Windows: o evento atual primeiro, depois a fila do SendInput.
/// </summary>
public sealed unsafe class KeyboardHookTests : IDisposable
{
    private const string Group = "HID#TESTE";
    private const int VkPeriod = 0xBE;
    private const int VkComma = 0xBC;
    private const int VkSemicolon = 0xBA;
    private const int VkSlash = 0xBF;
    private const int VkA = 0x41;
    private const int VkX = 0x58;
    private const int VkY = 0x59;
    private const int VkQ = 0x51;
    private const int VkF2 = 0x71;

    private readonly MacroRunner _macros = new();
    private readonly IntPtr _hkl = NativeMethods.GetKeyboardLayout(0);
    private readonly Simulator _sim;
    private readonly ITestOutputHelper _output;

    public KeyboardHookTests(ITestOutputHelper output)
    {
        _output = output;
        var rules = new List<RemapRule>
        {
            new() { TriggerVk = VkPeriod, Modifiers = ModifierFlags.AltGr, OutputType = RemapOutputType.Key, OutputVk = VkSlash },
            new() { TriggerVk = VkComma, Modifiers = ModifierFlags.AltGr, OutputType = RemapOutputType.Key, OutputVk = VkSemicolon },
            new() { TriggerVk = VkSemicolon, Modifiers = ModifierFlags.Shift, OutputType = RemapOutputType.Text, OutputText = "?" },
            new() { TriggerVk = VkX, Modifiers = ModifierFlags.Alt, OutputType = RemapOutputType.Key, OutputVk = VkY },
            new() { TriggerVk = VkA, Modifiers = 0, OutputType = RemapOutputType.Text, OutputText = "b" },
            new() { TriggerVk = VkF2, Modifiers = 0, OutputType = RemapOutputType.Key, OutputVk = VkQ, OutputModifiers = ModifierFlags.AltGr },
            new() { TriggerVk = VkSlash, Modifiers = ModifierFlags.AltGr, OutputType = RemapOutputType.Key, OutputVk = VkQ, OutputModifiers = ModifierFlags.AltGr },
        };

        string hklHex = KeyboardLayoutCatalog.FormatHkl(_hkl.ToInt64());
        var snapshot = ProfileCompiler.Compile(
            new Dictionary<string, string> { [Group] = hklHex },
            new Dictionary<string, List<RemapRule>> { [Group] = rules });

        _sim = new Simulator(_hkl);
        _sim.Hook = new KeyboardHook(() => snapshot, _macros, _sim.Send, _sim.IsDown);
        _sim.Hook.SetActiveGroup(Group);
    }

    public void Dispose() => _macros.Dispose();

    [Fact]
    public void QuickAltGrTap_SendsPlainKeyAndLeavesNoModifierStuck()
    {
        _sim.AltGrDown();
        bool blocked = _sim.Press(VkPeriod);
        _sim.Release(VkPeriod);
        _sim.AltGrUp();

        Assert.True(blocked);
        Assert.Equal(new[] { $"{_sim.ScanName(VkSlash)} ctrl=False alt=False shift=False" }, _sim.Output);
        Assert.Empty(_sim.ModifiersDown());
    }

    [Fact]
    public void HoldingAltGr_RepeatsAndChainsRulesWithoutLosingAltGr()
    {
        _sim.AltGrDown();
        _sim.Press(VkPeriod);
        _sim.Press(VkPeriod); // auto-repeat
        _sim.Release(VkPeriod);
        Assert.Contains(NativeMethods.VK_RMENU, _sim.ModifiersDown());

        _sim.Press(VkComma);
        _sim.Release(VkComma);
        _sim.AltGrUp();

        Assert.Equal(3, _sim.Output.Count);
        Assert.All(_sim.Output, o => Assert.EndsWith("ctrl=False alt=False shift=False", o));
        Assert.Empty(_sim.ModifiersDown());
    }

    [Fact]
    public void ShiftTextRule_TypesCharacterWithoutShift()
    {
        _sim.KeyDown(NativeMethods.VK_LSHIFT, 0x2A);
        _sim.Press(VkSemicolon);
        _sim.Release(VkSemicolon);
        _sim.KeyUp(NativeMethods.VK_LSHIFT, 0x2A);

        Assert.Equal(new[] { "'?' ctrl=False alt=False shift=False" }, _sim.Output);
        Assert.Empty(_sim.ModifiersDown());
    }

    [Fact]
    public void KeyWithoutRule_PassesThrough()
    {
        bool blocked = _sim.Press(VkComma);

        Assert.False(blocked);
        Assert.Empty(_sim.Output);
    }

    [Fact]
    public void WinHeld_RulesDoNotFire()
    {
        _sim.KeyDown(NativeMethods.VK_LWIN, 0x5B, extended: true);
        bool blocked = _sim.Press(VkA);

        Assert.False(blocked);
        Assert.Empty(_sim.Output);
    }

    [Fact]
    public void LeftAltRule_MasksTheMenuOnRelease()
    {
        _sim.KeyDown(NativeMethods.VK_LMENU, 0x38);
        _sim.Press(VkX);
        _sim.Release(VkX);
        bool altUpBlocked = _sim.KeyUp(NativeMethods.VK_LMENU, 0x38);

        Assert.True(altUpBlocked);
        Assert.Equal(new[] { "vkE8 down", "vkE8 up", "LAlt up" }, _sim.LastBatch);
        Assert.Empty(_sim.ModifiersDown());
    }

    [Fact]
    public void ModifierPressedByKeyNexusAndNeverReleased_IsHealed()
    {
        // Simula um Ctrl que o KeyNexus apertou e cuja soltura física se perdeu.
        _sim.InjectFromKeyNexus(NativeMethods.VK_LCONTROL, 0x1D, up: false);
        Assert.Contains(NativeMethods.VK_LCONTROL, _sim.ModifiersDown());

        bool blocked = _sim.Press(VkComma);

        Assert.True(blocked);
        Assert.Equal("LCtrl up", _sim.LastBatch[0]);
        Assert.DoesNotContain(NativeMethods.VK_LCONTROL, _sim.ModifiersDown());
    }

    [Fact]
    public void AltGrOutput_IsBalancedInsideTheBatch()
    {
        _sim.Press(VkF2);
        _sim.Release(VkF2);

        Assert.Equal(new[] { $"{_sim.ScanName(VkQ)} ctrl=True alt=True shift=False" }, _sim.Output);
        Assert.Empty(_sim.ModifiersDown());
    }

    [Fact]
    public void AltGrHeld_WithAltGrOutput_LeavesNothingStuck()
    {
        _sim.AltGrDown();
        _sim.Press(VkSlash);
        _sim.Release(VkSlash);
        _sim.AltGrUp();

        Assert.Equal(new[] { $"{_sim.ScanName(VkQ)} ctrl=True alt=True shift=False" }, _sim.Output);
        Assert.Empty(_sim.ModifiersDown());
    }

    [Fact]
    public void ReleasingAltGrBeforeTheKey_LeavesNothingStuck()
    {
        _sim.AltGrDown();
        _sim.Press(VkPeriod);
        _sim.AltGrUp();
        _sim.Release(VkPeriod);

        Assert.Single(_sim.Output);
        Assert.Empty(_sim.ModifiersDown());
    }

    [Fact]
    public void ReleasingAltGrAfterARule_TriggersTheCtrlSafetyCheck()
    {
        int signals = 0;
        _sim.Hook.AltGrReleasedAfterRestore += () => signals++;

        _sim.AltGrDown();
        _sim.Press(VkPeriod);
        _sim.Release(VkPeriod);
        _sim.AltGrUp();

        Assert.Equal(1, signals);
    }

    [Fact]
    public void CtrlStillDownAfterAltGr_IsReleasedBySafetyCheck()
    {
        _sim.ForceLogicalDown(NativeMethods.VK_LCONTROL);

        _sim.Hook.VerifyNoStuckCtrl();
        _sim.Drain();

        Assert.Equal(new[] { "LCtrl up" }, _sim.LastBatch);
        Assert.DoesNotContain(NativeMethods.VK_LCONTROL, _sim.ModifiersDown());
    }

    [Fact]
    public void SafetyCheck_KeepsCtrlTheUserIsHolding()
    {
        _sim.KeyDown(NativeMethods.VK_LCONTROL, 0x1D);

        _sim.Hook.VerifyNoStuckCtrl();
        _sim.Drain();

        Assert.Contains(NativeMethods.VK_LCONTROL, _sim.ModifiersDown());
    }

    [Fact]
    public void HookCallback_StaysWellUnderTheBudget()
    {
        _sim.AltGrDown();
        var samples = new long[20_000];
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            _sim.PressWithoutDraining(VkPeriod);
            samples[i] = Stopwatch.GetTimestamp() - start;
            _sim.Drain();
            _sim.Release(VkPeriod);
            _sim.Output.Clear();
        }
        _sim.AltGrUp();

        Array.Sort(samples);
        double toMs = 1000.0 / Stopwatch.Frequency;
        double p50Ms = samples[samples.Length / 2] * toMs;
        double p99Ms = samples[(int)(samples.Length * 0.99)] * toMs;
        _output.WriteLine($"callback com regra AltGr+.: p50={p50Ms:0.0000} ms p99={p99Ms:0.0000} ms");
        Assert.True(p99Ms < 0.2, $"p99 do callback = {p99Ms:0.0000} ms");
    }

    private sealed class Simulator
    {
        private readonly HashSet<int> _logical = new();
        private readonly Queue<NativeMethods.INPUT> _pending = new();
        private readonly IntPtr _hkl;

        /// <summary>
        /// Windows (layout com AltGr): quando o Alt direito desce com o Ctrl esquerdo solto, ele injeta um
        /// Ctrl esquerdo falso e só solta esse Ctrl quando o Alt direito sobe. Se o Ctrl já estava descido,
        /// não injeta nada e também não solta depois.
        /// </summary>
        private bool _systemFakeCtrl;

        public Simulator(IntPtr hkl) => _hkl = hkl;

        public KeyboardHook Hook { get; set; } = null!;
        public List<string> Output { get; } = new();
        public List<string> LastBatch { get; private set; } = new();

        public bool IsDown(int vk) => _logical.Contains(vk);

        /// <summary>Simula um modificador preso por fora (sem evento que o KeyNexus veja).</summary>
        public void ForceLogicalDown(int vk) => _logical.Add(vk);

        public IReadOnlyCollection<int> ModifiersDown() => _logical.Where(IsModifier).ToList();

        public uint Send(uint count, NativeMethods.INPUT[] inputs)
        {
            var batch = inputs.Take((int)count).ToArray();
            LastBatch = InputDescriber.Describe(batch, batch.Length);
            foreach (var input in batch)
                _pending.Enqueue(input);
            return count;
        }

        public void AltGrDown()
        {
            if (!IsDown(NativeMethods.VK_LCONTROL))
            {
                Physical(NativeMethods.VK_LCONTROL, ModifierTracker.AltGrFakeCtrlScanCode, extended: false, up: false, drain: false);
                _systemFakeCtrl = true;
            }
            KeyDown(NativeMethods.VK_RMENU, 0x38, extended: true);
        }

        public void AltGrUp()
        {
            Physical(NativeMethods.VK_RMENU, 0x38, extended: true, up: true, drain: false);
            if (_systemFakeCtrl)
            {
                Physical(NativeMethods.VK_LCONTROL, ModifierTracker.AltGrFakeCtrlScanCode, extended: false, up: true, drain: false);
                _systemFakeCtrl = false;
            }
            Drain();
        }

        public bool Press(int vk) => KeyDown(vk, Scan(vk));
        public bool Release(int vk) => KeyUp(vk, Scan(vk));

        public void PressWithoutDraining(int vk) => Physical(vk, Scan(vk), extended: false, up: false, drain: false);

        public bool KeyDown(int vk, uint scan, bool extended = false) => Physical(vk, scan, extended, up: false, drain: true);
        public bool KeyUp(int vk, uint scan, bool extended = false) => Physical(vk, scan, extended, up: true, drain: true);

        public void InjectFromKeyNexus(int vk, ushort scan, bool up)
        {
            var buffer = new NativeMethods.INPUT[1];
            InputBatchBuilder.AppendKey(buffer, 0, new KeyStroke((ushort)vk, scan, false), up);
            Send(1, buffer);
            Drain();
        }

        private bool Physical(int vk, uint scan, bool extended, bool up, bool drain)
        {
            var e = new NativeMethods.KBDLLHOOKSTRUCT
            {
                vkCode = (uint)vk,
                scanCode = scan,
                flags = (uint)((up ? NativeMethods.LLKHF_UP : 0) | (extended ? NativeMethods.LLKHF_EXTENDED : 0))
            };

            bool blocked = Hook.Process(&e);
            if (!blocked)
                Apply(vk, up);
            if (drain)
                Drain();
            return blocked;
        }

        public void Drain()
        {
            while (_pending.Count > 0)
                Deliver(_pending.Dequeue());
        }

        private void Deliver(NativeMethods.INPUT input)
        {
            var ki = input.ki;
            bool up = (ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP) != 0;
            bool extended = (ki.dwFlags & NativeMethods.KEYEVENTF_EXTENDEDKEY) != 0;

            if ((ki.dwFlags & NativeMethods.KEYEVENTF_UNICODE) != 0)
            {
                if (!up)
                    Output.Add($"'{(char)ki.wScan}' {State()}");
                return;
            }

            bool byScan = (ki.dwFlags & NativeMethods.KEYEVENTF_SCANCODE) != 0;
            int vk = byScan ? InputDescriber.ModifierVk(ki.wScan, extended) : ki.wVk;
            if (vk == 0)
                vk = (int)NativeMethods.MapVirtualKeyEx(ki.wScan, NativeMethods.MAPVK_VSC_TO_VK, _hkl);

            // Alt direito injetado também faz o Windows injetar (ou soltar) o Ctrl falso do AltGr.
            if (vk == NativeMethods.VK_RMENU && !up && !IsDown(NativeMethods.VK_LCONTROL))
            {
                DeliverSystemFakeCtrl(up: false);
                _systemFakeCtrl = true;
            }

            // O Windows também passa a entrada injetada pelo hook, marcada como injetada.
            var e = new NativeMethods.KBDLLHOOKSTRUCT
            {
                vkCode = (uint)vk,
                scanCode = ki.wScan,
                flags = (uint)(NativeMethods.LLKHF_INJECTED | (up ? NativeMethods.LLKHF_UP : 0)),
                dwExtraInfo = ki.dwExtraInfo
            };
            Hook.Process(&e);
            Apply(vk, up);

            if (vk == NativeMethods.VK_RMENU && up && _systemFakeCtrl)
            {
                DeliverSystemFakeCtrl(up: true);
                _systemFakeCtrl = false;
            }

            if (!up && !IsModifier(vk) && vk != KeyStroke.MenuMask.Vk)
                Output.Add($"{(byScan ? InputDescriber.ScanName(ki.wScan, extended) : $"vk{vk:X2}")} {State()}");
        }

        private void DeliverSystemFakeCtrl(bool up)
        {
            var e = new NativeMethods.KBDLLHOOKSTRUCT
            {
                vkCode = NativeMethods.VK_LCONTROL,
                scanCode = ModifierTracker.AltGrFakeCtrlScanCode,
                flags = (uint)(NativeMethods.LLKHF_INJECTED | (up ? NativeMethods.LLKHF_UP : 0))
            };
            Hook.Process(&e);
            Apply(NativeMethods.VK_LCONTROL, up);
        }

        private void Apply(int vk, bool up)
        {
            if (up)
                _logical.Remove(vk);
            else
                _logical.Add(vk);
        }

        private string State() =>
            $"ctrl={IsDown(NativeMethods.VK_LCONTROL) || IsDown(NativeMethods.VK_RCONTROL)} " +
            $"alt={IsDown(NativeMethods.VK_LMENU) || IsDown(NativeMethods.VK_RMENU)} " +
            $"shift={IsDown(NativeMethods.VK_LSHIFT) || IsDown(NativeMethods.VK_RSHIFT)}";

        public string ScanName(int vk) => InputDescriber.ScanName((ushort)KeyStroke.ForVk(vk, _hkl).Scan, KeyStroke.ForVk(vk, _hkl).Extended);

        private uint Scan(int vk) => NativeMethods.MapVirtualKeyEx((uint)vk, NativeMethods.MAPVK_VK_TO_VSC, _hkl);

        private static bool IsModifier(int vk) => ModifierTracker.IsModifierVk((uint)vk);
    }
}
