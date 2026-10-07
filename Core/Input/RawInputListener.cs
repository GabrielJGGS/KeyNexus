using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KeyNexus.Core.Input;

internal sealed class DeviceContext
{
    public DeviceContext(IntPtr handle, string path, string groupKey)
    {
        Handle = handle;
        Path = path;
        GroupKey = groupKey;
    }

    public IntPtr Handle { get; }
    public string Path { get; }
    public string GroupKey { get; }
}

/// <summary>
/// Identifica o teclado em uso a partir do WM_INPUT. Roda na thread de entrada;
/// cada hDevice é resolvido uma vez (inclusive "não é teclado").
/// </summary>
internal sealed unsafe class RawInputListener
{
    private readonly Dictionary<IntPtr, DeviceContext?> _cache = new();
    private HashSet<string>? _keyboardGroups;

    public DeviceContext? Active { get; private set; }

    /// <summary>Teclado ativo mudou (chamado na thread de entrada).</summary>
    public event Action<DeviceContext>? ActiveChanged;

    /// <summary>Um teclado ou coleção HID entrou ou saiu.</summary>
    public event Action? DevicesChanged;

    public bool Register(IntPtr hwnd)
    {
        const int flags = NativeMethods.RIDEV_INPUTSINK | NativeMethods.RIDEV_DEVNOTIFY;
        var devices = new NativeMethods.RAWINPUTDEVICE[2];
        devices[0] = new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x06, dwFlags = flags, hwndTarget = hwnd };
        devices[1] = new NativeMethods.RAWINPUTDEVICE { usUsagePage = 0x0C, usUsage = 0x01, dwFlags = flags, hwndTarget = hwnd };

        uint size = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>();
        if (NativeMethods.RegisterRawInputDevices(devices, 2, size))
            return true;

        Logger.Error("Raw Input: falha ao registrar teclado + consumer; tentando só teclado");
        if (NativeMethods.RegisterRawInputDevices(new[] { devices[0] }, 1, size))
            return true;

        Logger.Error("Raw Input: falha ao registrar teclado");
        return false;
    }

    public void OnInput(IntPtr hRawInput)
    {
        NativeMethods.RAWINPUTHEADER header;
        uint headerSize = (uint)sizeof(NativeMethods.RAWINPUTHEADER);
        uint size = headerSize;
        uint read = NativeMethods.GetRawInputData(hRawInput, NativeMethods.RID_HEADER, (IntPtr)(&header), ref size, headerSize);
        if (read != headerSize)
            return;

        // hDevice zero = entrada injetada (inclusive a do próprio KeyNexus).
        if (header.hDevice == IntPtr.Zero)
            return;

        if (header.dwType != NativeMethods.RIM_TYPEKEYBOARD && header.dwType != NativeMethods.RIM_TYPEHID)
            return;

        var context = Resolve(header.hDevice, header.dwType);
        if (context == null || ReferenceEquals(context, Active))
            return;

        bool sameKeyboard = Active != null
            && string.Equals(Active.GroupKey, context.GroupKey, StringComparison.OrdinalIgnoreCase);
        Active = context;

        if (!sameKeyboard)
            ActiveChanged?.Invoke(context);
    }

    public void OnDeviceChange(IntPtr wParam, IntPtr lParam)
    {
        _cache.Remove(lParam);
        _keyboardGroups = null;
        DevicesChanged?.Invoke();
    }

    private DeviceContext? Resolve(IntPtr hDevice, uint type)
    {
        if (_cache.TryGetValue(hDevice, out var cached))
            return cached;

        DeviceContext? context = null;
        string path = RawInputDevices.GetDeviceName(hDevice);
        if (!string.IsNullOrEmpty(path) && !DeviceGrouping.ShouldIgnore(path))
        {
            string groupKey = DeviceGrouping.GetGroupKey(path);
            bool isKeyboard = type == NativeMethods.RIM_TYPEKEYBOARD || KeyboardGroups.Contains(groupKey);
            if (isKeyboard && !string.IsNullOrEmpty(groupKey))
                context = new DeviceContext(hDevice, path, groupKey);
        }

        _cache[hDevice] = context;
        return context;
    }

    private HashSet<string> KeyboardGroups => _keyboardGroups ??= RawInputDevices.GetKeyboardGroupKeys();
}
