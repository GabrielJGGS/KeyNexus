using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using KeyNexus.Core.Profiles;

namespace KeyNexus.Core.Input;

/// <summary>
/// Thread dedicada de entrada: janela message-only (Raw Input), hook WH_KEYBOARD_LL,
/// WinEvent de foco e loop GetMessage. A UI nunca fica no caminho das teclas.
/// </summary>
internal sealed class InputThread : IDisposable
{
    private const string WindowClassName = "KeyNexus.InputWindow";
    private const uint WM_APP_INVOKE = NativeMethods.WM_APP + 1;
    private const uint WM_APP_QUIT = NativeMethods.WM_APP + 2;
    private static readonly IntPtr CtrlCheckTimerId = new(1);
    private const uint CtrlCheckDelayMs = 60;

    private readonly ConfigManager _config;
    private readonly MacroRunner _macros = new();
    private readonly LayoutSwitcher _layouts = new();
    private readonly RawInputListener _rawInput = new();
    private readonly KeyboardHook _hook;
    private readonly ConcurrentQueue<Action> _work = new();

    private Thread? _thread;
    private IntPtr _hwnd;
    private IntPtr _hInstance;
    private IntPtr _winEventHook;
    private NativeMethods.WndProc? _wndProc;
    private NativeMethods.WinEventDelegate? _winEventProc;
    private volatile bool _paused;
    private volatile string? _activeGroupKey;
    private volatile string? _activeDevicePath;

    public InputThread(ConfigManager config)
    {
        _config = config;
        _hook = new KeyboardHook(() => _config.Snapshot, _macros);
        _hook.AltGrReleasedAfterRestore += ScheduleCtrlCheck;
        _rawInput.ActiveChanged += OnActiveChanged;
        _rawInput.DevicesChanged += () => DevicesChanged?.Invoke();
        _config.SnapshotChanged += OnSnapshotChanged;
    }

    /// <summary>Teclado ativo mudou: (groupKey, caminho do dispositivo). Disparado na thread de entrada.</summary>
    public event Action<string, string>? ActiveKeyboardChanged;

    /// <summary>Dispositivos entraram ou saíram. Disparado na thread de entrada.</summary>
    public event Action? DevicesChanged;

    public string? ActiveGroupKey => _activeGroupKey;
    public string? ActiveDevicePath => _activeDevicePath;

    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            _hook.Paused = value;
            if (!value)
                Post(ApplyLayoutToForeground);
        }
    }

    public void Suspend() => _hook.Suspend();
    public void Resume() => _hook.Resume();

    public void ReleaseStuckKeys() => Post(_hook.ReleaseStuckModifiers);

    public void Start()
    {
        if (_thread != null)
            return;

        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = "KeyNexus.Input",
            Priority = ThreadPriority.Highest
        };
        _thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(5)))
            Logger.Error("InputThread: a thread de entrada demorou para iniciar");
    }

    public void Post(Action action)
    {
        _work.Enqueue(action);
        IntPtr hwnd = Volatile.Read(ref _hwnd);
        if (hwnd != IntPtr.Zero)
            NativeMethods.PostMessage(hwnd, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);
    }

    private void Run(ManualResetEventSlim ready)
    {
        try
        {
            CreateMessageWindow();
            PrewarmLayouts();

            if (_hwnd != IntPtr.Zero)
                _rawInput.Register(_hwnd);

            _hook.Install();

            _winEventProc = OnWinEvent;
            _winEventHook = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, _winEventProc, 0, 0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
            if (_winEventHook == IntPtr.Zero)
                Logger.Error("InputThread: falha ao acompanhar a janela em foco");

            Logger.Info("InputThread: entrada monitorada na thread dedicada");
        }
        catch (Exception ex)
        {
            Logger.Error("InputThread: falha ao iniciar", ex);
        }
        finally
        {
            ready.Set();
        }

        // Tarefas agendadas antes da janela existir.
        if (!_work.IsEmpty && _hwnd != IntPtr.Zero)
            NativeMethods.PostMessage(_hwnd, WM_APP_INVOKE, IntPtr.Zero, IntPtr.Zero);

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            NativeMethods.DispatchMessage(ref msg);

        Cleanup();
    }

    private void CreateMessageWindow()
    {
        _hInstance = NativeMethods.GetModuleHandle(null);
        _wndProc = WndProc;

        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = WindowClassName
        };
        NativeMethods.RegisterClassEx(ref wc);

        IntPtr hwnd = NativeMethods.CreateWindowEx(
            0, WindowClassName, "KeyNexus Input", 0, 0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE, IntPtr.Zero, _hInstance, IntPtr.Zero);
        Volatile.Write(ref _hwnd, hwnd);

        if (hwnd == IntPtr.Zero)
            Logger.Error($"InputThread: falha ao criar a janela de mensagens (erro {Marshal.GetLastWin32Error()})");
    }

    private static void PrewarmLayouts()
    {
        int count = NativeMethods.GetKeyboardLayoutList(0, null!);
        if (count <= 0)
            return;

        var layouts = new IntPtr[count];
        NativeMethods.GetKeyboardLayoutList(count, layouts);
        foreach (var hkl in layouts)
            ScanCodeTable.For(hkl);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case NativeMethods.WM_INPUT:
                    _rawInput.OnInput(lParam);
                    break; // DefWindowProc limpa o buffer do Raw Input

                case NativeMethods.WM_INPUT_DEVICE_CHANGE:
                    _rawInput.OnDeviceChange(wParam, lParam);
                    return IntPtr.Zero;

                case WM_APP_INVOKE:
                    DrainWork();
                    return IntPtr.Zero;

                case WM_APP_QUIT:
                    NativeMethods.PostQuitMessage(0);
                    return IntPtr.Zero;

                case NativeMethods.WM_TIMER when wParam == CtrlCheckTimerId:
                    NativeMethods.KillTimer(hwnd, CtrlCheckTimerId);
                    _hook.VerifyNoStuckCtrl();
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Logger.Error("InputThread: falha ao processar mensagem", ex);
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void DrainWork()
    {
        while (_work.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Logger.Error("InputThread: falha em tarefa agendada", ex);
            }
        }
    }

    /// <summary>
    /// Chamado de dentro do hook: só agenda. A conferência roda depois que o Windows aplicou a soltura do AltGr.
    /// </summary>
    private void ScheduleCtrlCheck()
    {
        if (_hwnd != IntPtr.Zero)
            NativeMethods.SetTimer(_hwnd, CtrlCheckTimerId, CtrlCheckDelayMs, IntPtr.Zero);
    }

    private void OnActiveChanged(DeviceContext context)
    {
        _activeGroupKey = context.GroupKey;
        _activeDevicePath = context.Path;
        _hook.SetActiveGroup(context.GroupKey);
        ApplyLayoutToForeground();
        ActiveKeyboardChanged?.Invoke(context.GroupKey, context.Path);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND && hwnd != IntPtr.Zero)
            ApplyLayout(hwnd);
    }

    private void OnSnapshotChanged() => Post(() =>
    {
        _hook.InvalidateProfile();
        ApplyLayoutToForeground();
    });

    private void ApplyLayoutToForeground() => ApplyLayout(NativeMethods.GetForegroundWindow());

    private void ApplyLayout(IntPtr hwnd)
    {
        if (_paused)
            return;

        var profile = _config.Snapshot.Find(_activeGroupKey);
        if (profile == null || profile.Hkl == IntPtr.Zero)
            return;

        _layouts.Apply(profile.Hkl, hwnd);
    }

    private void Cleanup()
    {
        try
        {
            if (_winEventHook != IntPtr.Zero)
                NativeMethods.UnhookWinEvent(_winEventHook);
            _hook.Uninstall();
            if (_hwnd != IntPtr.Zero)
                NativeMethods.DestroyWindow(_hwnd);
            NativeMethods.UnregisterClass(WindowClassName, _hInstance);
        }
        catch (Exception ex)
        {
            Logger.Error("InputThread: falha ao encerrar", ex);
        }
    }

    public void Stop()
    {
        if (_thread == null)
            return;

        _config.SnapshotChanged -= OnSnapshotChanged;
        if (_hwnd != IntPtr.Zero)
            NativeMethods.PostMessage(_hwnd, WM_APP_QUIT, IntPtr.Zero, IntPtr.Zero);

        if (!_thread.Join(TimeSpan.FromSeconds(2)))
            Logger.Error("InputThread: a thread de entrada não encerrou a tempo");
        _thread = null;

        _macros.Dispose();
        _layouts.Dispose();
    }

    public void Dispose() => Stop();
}
