using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows;

namespace KeyNexus.Core;

public class DeviceMonitor : IDisposable
{
    private HwndSource? _messageWindow;
    private readonly ConfigManager _configManager;
    private string _currentActiveDevice = string.Empty;

    public event Action<string, string>? OnActiveDeviceChanged;

    public DeviceMonitor()
    {
        _configManager = new ConfigManager();
    }

    public ConfigManager Config => _configManager;

    public void Start()
    {
        // Cria uma "Message Only Window" para processamento no background
        var parameters = new HwndSourceParameters("KeyNexus_RawInputWindow")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = new IntPtr(-3) // HWND_MESSAGE
        };
        _messageWindow = new HwndSource(parameters);
        _messageWindow.AddHook(WndProc);

        RegisterRawInput(_messageWindow.Handle);
    }

    public void Stop()
    {
        _messageWindow?.RemoveHook(WndProc);
        _messageWindow?.Dispose();
        _messageWindow = null;
    }

    private void RegisterRawInput(IntPtr hwnd)
    {
        var devices = new NativeMethods.RAWINPUTDEVICE[1];
        devices[0].usUsagePage = 0x01; // HID_USAGE_PAGE_GENERIC
        devices[0].usUsage = 0x06;     // HID_USAGE_GENERIC_KEYBOARD
        devices[0].dwFlags = NativeMethods.RIDEV_INPUTSINK; 
        devices[0].hwndTarget = hwnd;

        if (!NativeMethods.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICE))))
        {
            System.Diagnostics.Debug.WriteLine("Falha ao registrar Raw Input.");
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_INPUT)
        {
            ProcessRawInput(lParam);
        }
        return IntPtr.Zero;
    }

    private void ProcessRawInput(IntPtr hRawInput)
    {
        uint dwSize = 0;
        NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, IntPtr.Zero, ref dwSize, (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTHEADER)));
        
        if (dwSize == 0) return;

        IntPtr pData = Marshal.AllocHGlobal((int)dwSize);
        try
        {
            if (NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_INPUT, pData, ref dwSize, (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTHEADER))) == dwSize)
            {
                var header = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(pData);
                if (header.dwType == NativeMethods.RIM_TYPEKEYBOARD)
                {
                    HandleKeyboardInput(header.hDevice);
                }
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
        
        string? layoutHklHex = _configManager.GetLayoutForDevice(deviceName);
        
        // Notifica a UI em tempo real
        OnActiveDeviceChanged?.Invoke(deviceName, layoutHklHex ?? "Nenhum layout vinculado");

        // Evita chamadas repetitivas redundantes caso o dispositivo não mudou (performance em digitação)
        if (_currentActiveDevice == deviceName)
            return;

        _currentActiveDevice = deviceName;
        
        if (!string.IsNullOrEmpty(layoutHklHex))
        {
            ChangeForegroundLayout(layoutHklHex);
        }
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
            {
                return Marshal.PtrToStringAuto(pData) ?? string.Empty;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pData);
        }
        return string.Empty;
    }

    private void ChangeForegroundLayout(string hklHex)
    {
        try
        {
            uint handleValue = Convert.ToUInt32(hklHex, 16);
            IntPtr hkl = new IntPtr(unchecked((int)handleValue));

            IntPtr fgWindow = NativeMethods.GetForegroundWindow();
            if (fgWindow != IntPtr.Zero)
            {
                // Manda uma requisição para a thread da UI em foco trocar o layout de idioma 
                NativeMethods.PostMessage(fgWindow, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
            }
        }
        catch (Exception ex)
        {
             System.Diagnostics.Debug.WriteLine($"Error changing layout: {ex.Message}");
        }
    }

    public List<string> GetConnectedKeyboardsNames()
    {
        List<string> keyboards = new List<string>();
        uint deviceCount = 0;
        uint dwSize = (uint)Marshal.SizeOf(typeof(NativeMethods.RAWINPUTDEVICELIST));

        if (NativeMethods.GetRawInputDeviceList(IntPtr.Zero, ref deviceCount, dwSize) != 0)
            return keyboards;

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
                    {
                        keyboards.Add(name);
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pRawInputDeviceList);
        }

        return keyboards;
    }

    public void Dispose()
    {
        Stop();
    }
}
