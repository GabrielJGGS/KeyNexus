using System;
using System.Diagnostics;
using System.Threading;
using KeyNexus.Core.Profiles;

namespace KeyNexus.Core.Input;

/// <summary>
/// Hook WH_KEYBOARD_LL. Roda na thread de entrada e injeta a saída de forma síncrona,
/// num único SendInput dentro do callback: o estado físico não muda no meio do lote.
/// </summary>
internal sealed unsafe class KeyboardHook : IDisposable
{
    internal delegate uint SendInputFn(uint count, NativeMethods.INPUT[] inputs);

    private readonly Func<ProfileSnapshot> _snapshotProvider;
    private readonly MacroRunner _macros;
    private readonly ModifierTracker _tracker = new();
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private readonly SendInputFn _sendInput;
    private readonly Func<int, bool> _isLogicallyDown;

    private static readonly PhysicalModifiers[] LogicalKeys =
    {
        PhysicalModifiers.LShift, PhysicalModifiers.RShift, PhysicalModifiers.LCtrl, PhysicalModifiers.RCtrl,
        PhysicalModifiers.LAlt, PhysicalModifiers.RAlt, PhysicalModifiers.LWin, PhysicalModifiers.RWin
    };

    private IntPtr _handle;
    private NativeMethods.INPUT[] _buffer = new NativeMethods.INPUT[128];

    private ProfileSnapshot? _snapshot;
    private string? _activeGroupKey;
    private DeviceProfile? _activeProfile;

    private volatile bool _paused;
    private int _suspendCount;
    private bool _menuMaskPending;
    /// <summary>Modificadores cuja última descida veio do KeyNexus (para a auto-correção).</summary>
    private PhysicalModifiers _injectedDowns;
    /// <summary>O KeyNexus devolveu o AltGr num lote desde que ele foi apertado.</summary>
    private bool _restoredAltGr;

#if DEBUG
    private readonly HookTiming _timing = new();
#endif

    /// <param name="sendInput">Só testes substituem; o padrão é o SendInput real.</param>
    /// <param name="isLogicallyDown">Só testes substituem; o padrão é GetAsyncKeyState.</param>
    public KeyboardHook(
        Func<ProfileSnapshot> snapshotProvider,
        MacroRunner macros,
        SendInputFn? sendInput = null,
        Func<int, bool>? isLogicallyDown = null)
    {
        _snapshotProvider = snapshotProvider;
        _macros = macros;
        _sendInput = sendInput ?? ((count, inputs) => NativeMethods.SendInput(count, inputs, NativeMethods.InputStructSize));
        _isLogicallyDown = isLogicallyDown ?? (vk => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0);
        _proc = Callback;
    }

    /// <summary>
    /// O AltGr físico foi solto depois de o KeyNexus devolvê-lo. A thread de entrada confere em seguida
    /// se o Ctrl esquerdo não ficou preso (<see cref="VerifyNoStuckCtrl"/>).
    /// </summary>
    public event Action? AltGrReleasedAfterRestore;

    public bool IsInstalled => _handle != IntPtr.Zero;

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    public bool IsSuspended => Volatile.Read(ref _suspendCount) > 0;

    public void Suspend() => Interlocked.Increment(ref _suspendCount);

    public void Resume()
    {
        if (Interlocked.Decrement(ref _suspendCount) < 0)
            Interlocked.Exchange(ref _suspendCount, 0);
    }

    public void Install()
    {
        if (_handle != IntPtr.Zero)
            return;

        _handle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);

        if (_handle == IntPtr.Zero)
            Logger.Error("KeyboardHook: falha ao instalar o hook de teclado");
        else
            Logger.Info("KeyboardHook: hook instalado na thread de entrada");
    }

    public void Uninstall()
    {
        if (_handle == IntPtr.Zero)
            return;

        NativeMethods.UnhookWindowsHookEx(_handle);
        _handle = IntPtr.Zero;
        Logger.Info("KeyboardHook: hook removido");
    }

    /// <summary>Chamado pela thread de entrada quando o teclado ativo muda.</summary>
    public void SetActiveGroup(string? groupKey)
    {
        _activeGroupKey = groupKey;
        _snapshot = null;
        _activeProfile = null;
    }

    public void InvalidateProfile() => _snapshot = null;

    /// <summary>Solta todo modificador logicamente preso. Ação manual da bandeja.</summary>
    public void ReleaseStuckModifiers()
    {
        PhysicalModifiers logical = PhysicalModifiers.None;
        foreach (var bit in LogicalKeys)
        {
            if (_isLogicallyDown(ModifierTracker.VkOf(bit)))
                logical |= bit;
        }

        _tracker.Reset();
        _injectedDowns = PhysicalModifiers.None;
        _menuMaskPending = false;

        if (logical == PhysicalModifiers.None)
            return;

        int n = InputBatchBuilder.ReleaseHeld(_buffer, 0, logical);
        Send(n);
        Logger.Info($"KeyboardHook: modificadores soltos manualmente ({logical})");
    }

    private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
#if DEBUG
            long start = Stopwatch.GetTimestamp();
#endif
            try
            {
                if (Process((NativeMethods.KBDLLHOOKSTRUCT*)lParam))
                    return (IntPtr)1;
            }
            catch (Exception ex)
            {
                Logger.Error("KeyboardHook: falha no callback", ex);
            }
#if DEBUG
            finally
            {
                _timing.Record(Stopwatch.GetTimestamp() - start);
            }
#endif
        }

        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>Decide um evento do hook. Retorna true quando o evento original deve ser bloqueado.</summary>
    internal bool Process(NativeMethods.KBDLLHOOKSTRUCT* e)
    {
        uint flags = e->flags;
        uint vk = e->vkCode;
        bool isUp = (flags & NativeMethods.LLKHF_UP) != 0;

        if ((flags & NativeMethods.LLKHF_INJECTED) != 0)
        {
            if (e->dwExtraInfo == InputBatchBuilder.Signature && ModifierTracker.IsModifierVk(vk))
                TrackInjectedModifier(vk, e->scanCode, isUp);
            return false;
        }

        if (ModifierTracker.IsModifierVk(vk))
        {
            _tracker.Update(vk, e->scanCode, isUp);
            _injectedDowns &= ~ModifierTracker.FoldCtrl(ModifierTracker.ToModifier(vk, e->scanCode));

            if (isUp && vk == NativeMethods.VK_RMENU && _restoredAltGr)
            {
                _restoredAltGr = false;
                AltGrReleasedAfterRestore?.Invoke();
            }

            if (isUp && _menuMaskPending && vk is NativeMethods.VK_LMENU or NativeMethods.VK_RMENU
                    or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN)
                return ReleaseWithMenuMask(vk, e->scanCode);

            return false;
        }

        if (isUp)
            return false;

        _menuMaskPending = false;
        int n = HealStuckModifiers();

        bool extended = (flags & NativeMethods.LLKHF_EXTENDED) != 0;
        if (!_paused && !IsSuspended && TryGetAction(e->scanCode, extended, vk, out var action, out var profile))
        {
            Execute(action, profile, n);
            return true;
        }

        if (n == 0)
            return false;

        // Corrigiu tecla presa: reenvia a tecla atual depois da soltura, para manter a ordem.
        n = InputBatchBuilder.AppendKey(_buffer, n, new KeyStroke((ushort)vk, (ushort)e->scanCode, extended), up: false);
        Send(n);
        return true;
    }

    private bool TryGetAction(uint scanCode, bool extended, uint hookVk, out CompiledAction action, out DeviceProfile profile)
    {
        action = null!;
        profile = ResolveProfile()!;
        if (profile is null || !profile.HasRules)
            return false;

        _tracker.Resync(_isLogicallyDown);
        int mods = _tracker.CurrentFlags;

        ushort[] table = profile.ScanToVk ?? ScanCodeTable.For(ForegroundLayout());
        int triggerVk = ScanCodeTable.Lookup(table, scanCode, extended);
        if (triggerVk == 0)
            triggerVk = (int)hookVk;

        return profile.Rules.TryGetValue(DeviceProfile.RuleKey(triggerVk, mods), out action!);
    }

    private DeviceProfile? ResolveProfile()
    {
        var snapshot = _snapshotProvider();
        if (!ReferenceEquals(snapshot, _snapshot))
        {
            _snapshot = snapshot;
            _activeProfile = snapshot.Find(_activeGroupKey);
        }
        return _activeProfile;
    }

    private void Execute(CompiledAction action, DeviceProfile profile, int n)
    {
        PhysicalModifiers held = _tracker.Held;
        IntPtr hkl = profile.Hkl != IntPtr.Zero ? profile.Hkl : ForegroundLayout();
        EnsureCapacity(n + InputBatchBuilder.RequiredCapacity(action));

        if (action.Kind == CompiledActionKind.Macro && action.HasDelays)
        {
            // Macro com atraso não cabe no callback: solta os modificadores agora e enfileira.
            n = InputBatchBuilder.ReleaseHeld(_buffer, n, held);
            Send(n);
            _macros.Enqueue(action, hkl);
            return;
        }

        n = InputBatchBuilder.BuildRemap(_buffer, n, held, action, hkl);
        Send(n);
        _menuMaskPending = InputBatchBuilder.NeedsMenuMask(held);
        if ((held & PhysicalModifiers.RAlt) != 0)
            _restoredAltGr = true;
    }

    /// <summary>
    /// Depois de soltar o AltGr: se o Ctrl esquerdo continua descido sem nenhum Ctrl físico segurado, solta.
    /// Roda na thread de entrada, alguns milissegundos depois da soltura.
    /// </summary>
    public void VerifyNoStuckCtrl()
    {
        var held = _tracker.Held;
        if ((held & (PhysicalModifiers.AnyCtrl | PhysicalModifiers.RAlt)) != 0)
            return;
        if (!_isLogicallyDown(NativeMethods.VK_LCONTROL))
            return;

        int n = InputBatchBuilder.AppendKey(_buffer, 0, KeyStroke.LCtrl, up: true);
        Send(n);
        _injectedDowns &= ~PhysicalModifiers.LCtrl;
        Logger.Info("KeyboardHook: o Ctrl esquerdo continuava preso depois do AltGr; solto automaticamente");
    }

    /// <summary>
    /// O KeyNexus devolveu Alt/Win pressionado; na soltura física, envia a tecla neutra antes
    /// para o Windows não tratar como "Alt/Win tocado sozinho".
    /// </summary>
    private bool ReleaseWithMenuMask(uint vk, uint scanCode)
    {
        if ((_tracker.Held & (PhysicalModifiers.AnyAlt | PhysicalModifiers.AnyWin)) == 0)
            _menuMaskPending = false;

        var key = KeyStroke.ForModifier(ModifierTracker.ToModifier(vk, scanCode));
        if (key.IsEmpty)
            return false;

        int n = InputBatchBuilder.AppendPress(_buffer, 0, KeyStroke.MenuMask);
        n = InputBatchBuilder.AppendKey(_buffer, n, key, up: true);
        Send(n);
        return true;
    }

    /// <summary>
    /// Rede de segurança: solta Ctrl/Alt/Shift/Win que o KeyNexus apertou e que continuam
    /// logicamente presos sem a tecla física. Retorna quantos eventos entraram no buffer.
    /// </summary>
    private int HealStuckModifiers()
    {
        if (_injectedDowns == PhysicalModifiers.None)
            return 0;

        PhysicalModifiers candidates = _injectedDowns & ~ModifierTracker.FoldCtrl(_tracker.Held);
        _injectedDowns &= ~candidates;
        if (candidates == PhysicalModifiers.None)
            return 0;

        PhysicalModifiers stuck = PhysicalModifiers.None;
        foreach (var bit in LogicalKeys)
        {
            if ((candidates & bit) != 0 && _isLogicallyDown(ModifierTracker.VkOf(bit)))
                stuck |= bit;
        }

        if (stuck == PhysicalModifiers.None)
            return 0;

        Logger.Info($"KeyboardHook: auto-correção soltou modificadores presos ({stuck})");
        return InputBatchBuilder.ReleaseHeld(_buffer, 0, stuck);
    }

    private void TrackInjectedModifier(uint vk, uint scanCode, bool isUp)
    {
        var bit = ModifierTracker.FoldCtrl(ModifierTracker.ToModifier(vk, scanCode));
        if (isUp)
            _injectedDowns &= ~bit;
        else
            _injectedDowns |= bit;
    }

    private void Send(int count)
    {
        if (count > 0)
            _sendInput((uint)count, _buffer);
    }

    private void EnsureCapacity(int required)
    {
        if (_buffer.Length >= required)
            return;

        var bigger = new NativeMethods.INPUT[Math.Max(required, _buffer.Length * 2)];
        Array.Copy(_buffer, bigger, _buffer.Length);
        _buffer = bigger;
    }

    internal static IntPtr ForegroundLayout()
    {
        IntPtr fg = NativeMethods.GetForegroundWindow();
        uint thread = fg != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(fg, out _) : 0;
        return NativeMethods.GetKeyboardLayout(thread);
    }

    public void Dispose() => Uninstall();

#if DEBUG
    /// <summary>Mede a duração do callback e registra p50/p99/máximo a cada 2000 eventos.</summary>
    private sealed class HookTiming
    {
        private readonly long[] _samples = new long[2000];
        private int _count;

        public void Record(long ticks)
        {
            _samples[_count++] = ticks;
            if (_count < _samples.Length)
                return;

            Array.Sort(_samples);
            double toMs = 1000.0 / Stopwatch.Frequency;
            Logger.Info(
                $"KeyboardHook: callback p50={_samples[_count / 2] * toMs:0.000}ms " +
                $"p99={_samples[(int)(_count * 0.99)] * toMs:0.000}ms max={_samples[_count - 1] * toMs:0.000}ms");
            _count = 0;
        }
    }
#endif
}
