using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KeyNexus.Core.Input;

internal readonly record struct RawDeviceEntry(IntPtr Handle, uint Type, string Name);

/// <summary>Enumeração de dispositivos Raw Input. Pode ser chamada de qualquer thread.</summary>
internal static class RawInputDevices
{
    public static List<RawDeviceEntry> Enumerate()
    {
        var result = new List<RawDeviceEntry>();
        uint count = 0;
        uint entrySize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICELIST>();

        if (NativeMethods.GetRawInputDeviceList(IntPtr.Zero, ref count, entrySize) != 0 || count == 0)
            return result;

        IntPtr list = Marshal.AllocHGlobal((int)(entrySize * count));
        try
        {
            uint read = NativeMethods.GetRawInputDeviceList(list, ref count, entrySize);
            if (read == uint.MaxValue)
                return result;

            for (int i = 0; i < read; i++)
            {
                var item = Marshal.PtrToStructure<NativeMethods.RAWINPUTDEVICELIST>(list + (int)(i * entrySize));
                if (item.dwType != NativeMethods.RIM_TYPEKEYBOARD && item.dwType != NativeMethods.RIM_TYPEHID)
                    continue;

                string name = GetDeviceName(item.hDevice);
                if (!string.IsNullOrEmpty(name))
                    result.Add(new RawDeviceEntry(item.hDevice, item.dwType, name));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(list);
        }

        return result;
    }

    public static string GetDeviceName(IntPtr hDevice)
    {
        try
        {
            uint chars = 0;
            NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICENAME, IntPtr.Zero, ref chars);
            if (chars == 0 || chars > 8192)
                return string.Empty;

            IntPtr data = Marshal.AllocHGlobal((int)chars * 2);
            try
            {
                uint result = NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICENAME, data, ref chars);
                if (result == uint.MaxValue || result == 0)
                    return string.Empty;
                return Marshal.PtrToStringUni(data) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Teclados conectados, com as coleções HID extras (mídia, vendor) do mesmo aparelho juntas.
    /// </summary>
    public static List<KeyboardGroup> GetKeyboardGroups()
    {
        var keyboardPaths = new List<string>();
        var hidPaths = new List<string>();

        foreach (var entry in Enumerate())
        {
            if (DeviceGrouping.ShouldIgnore(entry.Name))
                continue;

            if (entry.Type == NativeMethods.RIM_TYPEKEYBOARD)
                keyboardPaths.Add(entry.Name);
            else
                hidPaths.Add(entry.Name);
        }

        var groups = DeviceGrouping.GroupDevices(keyboardPaths);
        if (hidPaths.Count == 0 || groups.Count == 0)
            return groups;

        var keyboardKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
            keyboardKeys.Add(group.GroupKey);

        var merged = new List<string>(keyboardPaths);
        foreach (var hidPath in hidPaths)
        {
            if (keyboardKeys.Contains(DeviceGrouping.GetGroupKey(hidPath)))
                merged.Add(hidPath);
        }

        return merged.Count > keyboardPaths.Count ? DeviceGrouping.GroupDevices(merged) : groups;
    }

    public static HashSet<string> GetKeyboardGroupKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Enumerate())
        {
            if (entry.Type == NativeMethods.RIM_TYPEKEYBOARD && !DeviceGrouping.ShouldIgnore(entry.Name))
                keys.Add(DeviceGrouping.GetGroupKey(entry.Name));
        }
        return keys;
    }
}
