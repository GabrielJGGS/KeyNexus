using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Threading;
using KeyNexus.Core.Input;

namespace KeyNexus.Core;

/// <summary>
/// Fachada para a UI. Hook e Raw Input vivem na <see cref="InputThread"/>; aqui os eventos
/// chegam já na thread da UI e só quando algo muda.
/// </summary>
public sealed class DeviceMonitor : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _devicesDebounce;
    private InputThread? _input;

    public DeviceMonitor()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Config = new ConfigManager();

        _devicesDebounce = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _devicesDebounce.Tick += (_, _) =>
        {
            _devicesDebounce.Stop();
            DevicesChanged?.Invoke();
        };

        Logger.Info("DeviceMonitor inicializado");
    }

    public ConfigManager Config { get; }

    /// <summary>Chave do teclado físico em uso agora (null até a primeira tecla).</summary>
    public string? ActiveGroupKey { get; private set; }

    /// <summary>Caminho Raw Input do teclado em uso agora.</summary>
    public string? ActiveDevicePath { get; private set; }

    public bool IsPaused { get; private set; }

    /// <summary>Teclado em uso mudou. Disparado na thread da UI.</summary>
    public event Action<string>? ActiveKeyboardChanged;

    /// <summary>Teclados conectados mudaram (com debounce). Disparado na thread da UI.</summary>
    public event Action? DevicesChanged;

    public event Action<bool>? PausedChanged;

    public void Start()
    {
        if (_input != null)
            return;

        _input = new InputThread(Config);
        _input.ActiveKeyboardChanged += (groupKey, path) => _dispatcher.BeginInvoke(() =>
        {
            ActiveGroupKey = groupKey;
            ActiveDevicePath = path;
            ActiveKeyboardChanged?.Invoke(groupKey);
        });
        _input.DevicesChanged += () => _dispatcher.BeginInvoke(() =>
        {
            _devicesDebounce.Stop();
            _devicesDebounce.Start();
        });
        _input.Start();

        Logger.Info("Monitoramento de teclados iniciado");
    }

    public void Stop()
    {
        _input?.Stop();
        _input = null;
        Config.Flush();
        Logger.Info("Monitoramento de teclados parado");
    }

    public void SetPaused(bool paused)
    {
        if (IsPaused == paused)
            return;

        IsPaused = paused;
        if (_input != null)
            _input.Paused = paused;

        Logger.Info(paused ? "KeyNexus pausado" : "KeyNexus retomado");
        PausedChanged?.Invoke(paused);
    }

    /// <summary>Desliga as regras enquanto o token estiver vivo (captura de tecla no editor).</summary>
    public IDisposable SuspendRemapping()
    {
        _input?.Suspend();
        return new SuspendToken(this);
    }

    public void ReleaseStuckKeys() => _input?.ReleaseStuckKeys();

    public List<KeyboardGroup> GetConnectedKeyboardGroups() => RawInputDevices.GetKeyboardGroups();

    public void Dispose() => Stop();

    private sealed class SuspendToken : IDisposable
    {
        private DeviceMonitor? _owner;

        public SuspendToken(DeviceMonitor owner) => _owner = owner;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?._input?.Resume();
        }
    }
}
