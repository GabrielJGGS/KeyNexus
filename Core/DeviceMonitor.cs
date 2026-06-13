using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace KeyNexus.Core;

public class DeviceMonitor : IDisposable
{
    private HwndSource? _messageWindow;
    private readonly ConfigManager _configManager;
    private RemapEngine? _remapEngine;
    private string _currentActiveDevice = string.Empty;
    private string _currentActiveGroupKey = string.Empty;
    private IntPtr _lastForegroundWindow = IntPtr.Zero;

    /// <summary>
    /// Disparado quando o dispositivo ativo muda.
    /// Parâmetros: (groupKey, friendlyName, layoutHkl ou null)
    /// </summary>
    public event Action<string, string, string?>? OnActiveDeviceChanged;

    public event Action? OnDevicesChanged;

    public ConfigManager Config => _configManager;
    public string CurrentActiveGroupKey => _currentActiveGroupKey;

    public DeviceMonitor()
    {
        _configManager = new ConfigManager();
        Logger.Info("DeviceMonitor inicializado");
    }

    public void Start()
    {
        var parameters = new HwndSourceParameters("KeyNexus_RawInputWindow")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = new IntPtr(-3)
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);

        RegisterRawInput(_messageWindow.Handle);

        _remapEngine = new RemapEngine(_configManager, () => _currentActiveGroupKey);
        _remapEngine.Start();

        Logger.Info("Monitoramento de teclados iniciado");
    }

    public void Stop()
    {
        _remapEngine?.Stop();
        _remapEngine?.Dispose();
        _remapEngine = null;

        _messageWindow?.RemoveHook(WndProc);
        _messageWindow?.Dispose();
        _messageWindow = null;
        Logger.Info("Monitoramento de teclados parado");
    }

    private void RegisterRawInput(IntPtr hwnd)
    {
        var devices = new NativeMethods.RAWINPUTDEVICE[1];
        devices[0].usUsagePage = 0x01;
        devices[0].usUsage = 0x06;
        devices[0].dwFlags = NativeMethods.RIDEV_INPUTSINK;
        devices[0].hwndTarget = hwnd;

        if (!NativeMethods.RegisterRawInputDevices(
            devices, (uint)devices.Length,
            (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICE))))
        {
            Logger.Error("Falha ao registrar Raw Input");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case NativeMethods.WM_INPUT:
                ProcessRawInput(lParam);
                break;

            case NativeMethods.WM_DEVICECHANGE:
                if (wParam.ToInt32() == NativeMethods.DBT_DEVNODES_CHANGED)
                {
                    Logger.Info("Mudança de dispositivos detectada (hot-plug)");
                    OnDevicesChanged?.Invoke();
                }
                break;
        }
        return IntPtr.Zero;
    }

    private void ProcessRawInput(IntPtr hRawInput)
    {
        uint dwSize = 0;
        uint headerSize = (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTHEADER));
        NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, IntPtr.Zero, ref dwSize, headerSize);

        if (dwSize == 0) return;

        IntPtr pData = Marshal.AllocHGlobal((int)dwSize);
        try
        {
            if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, pData, ref dwSize, headerSize) == dwSize)
            {
                var header = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(pData);
                if (header.dwType == NativeMethods.RIM_TYPEKEYBOARD)
                    HandleKeyboardInput(header.hDevice);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pData);
        }
    }

    private void HandleKeyboardInput(IntPtr hDevice)
    {
        string deviceName = GetDeviceName(hDevice);
        if (DeviceGrouping.ShouldIgnore(deviceName))
            return;

        string groupKey = DeviceGrouping.GetGroupKey(deviceName);
        string friendlyName = DeviceNameResolver.GetFriendlyName(deviceName);
        string? alias = _configManager.GetDeviceAlias(groupKey);
        string displayName = !string.IsNullOrEmpty(alias) ? alias : friendlyName;
        string? layoutHklHex = _configManager.GetLayoutForDevice(groupKey);

        _currentActiveGroupKey = groupKey;
        OnActiveDeviceChanged?.Invoke(groupKey, displayName, layoutHklHex);

        IntPtr fgWindow = NativeMethods.GetForegroundWindow();

        if (_currentActiveDevice == groupKey && _lastForegroundWindow == fgWindow)
            return;

        _currentActiveDevice = groupKey;
        _lastForegroundWindow = fgWindow;

        if (!string.IsNullOrEmpty(layoutHklHex))
            ChangeForegroundLayout(layoutHklHex, fgWindow);
    }

    private string GetDeviceName(IntPtr hDevice)
    {
        uint pcbSize = 0;
        NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICENAME, IntPtr.Zero, ref pcbSize);

        if (pcbSize == 0) return string.Empty;

        IntPtr pData = Marshal.AllocHGlobal((int)pcbSize * 2);
        try
        {
            uint result = NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICENAME, pData, ref pcbSize);
            if (result != unchecked((uint)-1) && result != 0)
                return Marshal.PtrToStringAuto(pData) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(pData);
        }
        return string.Empty;
    }

    private void ChangeForegroundLayout(string hklHex, IntPtr fgWindow)
    {
        try
        {
            uint handleValue = Convert.ToUInt32(hklHex, 16);
            IntPtr hkl = new IntPtr(unchecked((int)handleValue));

            if (fgWindow == IntPtr.Zero) return;

            uint targetThreadId = NativeMethods.GetWindowThreadProcessId(fgWindow, out _);
            uint currentThreadId = NativeMethods.GetCurrentThreadId();

            bool attached = false;
            try
            {
                if (targetThreadId != currentThreadId)
                    attached = NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, true);

                NativeMethods.ActivateKeyboardLayout(hkl, NativeMethods.KLF_SETFORPROCESS);
                NativeMethods.PostMessage(fgWindow, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);

                Logger.Info($"Layout alterado para {hklHex} na janela 0x{fgWindow:X}");
            }
            finally
            {
                if (attached)
                    NativeMethods.AttachThreadInput(currentThreadId, targetThreadId, false);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao alterar layout", ex);
        }
    }

    public List<KeyboardGroup> GetConnectedKeyboardGroups()
    {
        var rawPaths = new List<string>();
        uint deviceCount = 0;
        uint dwSize = (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICELIST));

        if (NativeMethods.GetRawInputDeviceList(IntPtr.Zero, ref deviceCount, dwSize) != 0)
            return new List<KeyboardGroup>();

        var pRawInputDeviceList = Marshal.AllocHGlobal((int)(dwSize * deviceCount));
        try
        {
            NativeMethods.GetRawInputDeviceList(pRawInputDeviceList, ref deviceCount, dwSize);

            for (int i = 0; i < deviceCount; i++)
            {
                IntPtr currentPtr = new IntPtr(pRawInputDeviceList.ToInt64() + (i * dwSize));
                var rid = Marshal.PtrToStructure<NativeMethods.RAWINPUTDEVICELIST>(currentPtr);

                if (rid.dwType == NativeMethods.RIM_TYPEKEYBOARD)
                {
                    string name = GetDeviceName(rid.hDevice);
                    if (!string.IsNullOrEmpty(name))
                        rawPaths.Add(name);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pRawInputDeviceList);
        }

        return DeviceGrouping.GroupDevices(rawPaths);
    }

    public void Dispose() => Stop();
}
