using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyNexus.Core;

/// <summary>
/// Resolve nomes amigáveis de dispositivos HID usando a SetupAPI do Windows.
/// Transforma "\\?\HID#VID_32C2&PID_0018..." em "USB Input Device" ou similar.
/// </summary>
public static class DeviceNameResolver
{
    private static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Retorna nome amigável do dispositivo a partir do seu raw device path.
    /// </summary>
    public static string GetFriendlyName(string rawDevicePath)
    {
        if (string.IsNullOrEmpty(rawDevicePath))
            return "Desconhecido";

        if (_cache.TryGetValue(rawDevicePath, out var cached))
            return cached;

        string friendly = ResolveFriendlyName(rawDevicePath);
        _cache[rawDevicePath] = friendly;
        return friendly;
    }

    /// <summary>
    /// Extrai VID e PID do caminho do dispositivo para exibição resumida.
    /// </summary>
    public static string GetShortId(string rawDevicePath)
    {
        if (string.IsNullOrEmpty(rawDevicePath))
            return "???";

        return DeviceIdentityParser.FormatShortId(
            DeviceIdentityParser.Parse(rawDevicePath, SetupApiHelper.GetHardwareIds(rawDevicePath)));
    }

    private static string ResolveFriendlyName(string rawDevicePath)
    {
        try
        {
            #pragma warning disable CA1416
            var guid = NativeMethods.GUID_DEVINTERFACE_HID;
            IntPtr hDevInfo = NativeMethods.SetupDiGetClassDevs(
                ref guid, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.DIGCF_PRESENT | NativeMethods.DIGCF_DEVICEINTERFACE);

            if (hDevInfo == NativeMethods.INVALID_HANDLE_VALUE)
                return FallbackName(rawDevicePath);

            try
            {
                uint index = 0;
                var did = new NativeMethods.SP_DEVICE_INTERFACE_DATA();
                did.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.SP_DEVICE_INTERFACE_DATA));

                while (NativeMethods.SetupDiEnumDeviceInterfaces(hDevInfo, IntPtr.Zero, ref guid, index, ref did))
                {
                    var devInfoData = new NativeMethods.SP_DEVINFO_DATA();
                    devInfoData.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.SP_DEVINFO_DATA));

                    string? devicePath = SetupApiHelper.TryGetHidDevicePath(hDevInfo, ref did, ref devInfoData);
                    if (!string.IsNullOrEmpty(devicePath) && PathsMatch(rawDevicePath, devicePath))
                    {
                        string desc = GetDeviceDescription(hDevInfo, ref devInfoData);
                        if (!string.IsNullOrEmpty(desc))
                            return desc;
                    }

                    index++;
                    did.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.SP_DEVICE_INTERFACE_DATA));
                }
            }
            finally
            {
                NativeMethods.SetupDiDestroyDeviceInfoList(hDevInfo);
            }
            #pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao resolver nome amigável", ex);
        }

        return FallbackName(rawDevicePath);
    }

    private static bool PathsMatch(string rawPath, string setupPath)
    {
        if (string.Equals(rawPath, setupPath, StringComparison.OrdinalIgnoreCase))
            return true;

        string rawKey = DeviceGrouping.GetGroupKey(rawPath);
        string setupKey = DeviceGrouping.GetGroupKey(setupPath);
        if (!string.IsNullOrEmpty(rawKey) && rawKey.Equals(setupKey, StringComparison.OrdinalIgnoreCase))
            return true;

        var rawId = DeviceIdentityParser.Parse(rawPath);
        var setupId = DeviceIdentityParser.Parse(setupPath);
        return !string.IsNullOrEmpty(rawId.VendorId)
            && rawId.VendorId == setupId.VendorId
            && !string.IsNullOrEmpty(rawId.ProductId)
            && rawId.ProductId == setupId.ProductId
            && (rawId.BluetoothAddress == null || rawId.BluetoothAddress == setupId.BluetoothAddress);
    }

    private static string GetDeviceDescription(IntPtr hDevInfo, ref NativeMethods.SP_DEVINFO_DATA devInfoData)
    {
        #pragma warning disable CA1416
        uint requiredSize = 0;
        NativeMethods.SetupDiGetDeviceRegistryProperty(
            hDevInfo, ref devInfoData, NativeMethods.SPDRP_DEVICEDESC,
            out _, IntPtr.Zero, 0, out requiredSize);

        if (requiredSize == 0) return string.Empty;

        IntPtr buffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            if (NativeMethods.SetupDiGetDeviceRegistryProperty(
                hDevInfo, ref devInfoData, NativeMethods.SPDRP_DEVICEDESC,
                out _, buffer, requiredSize, out _))
            {
                return Marshal.PtrToStringAuto(buffer) ?? string.Empty;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        #pragma warning restore CA1416
        return string.Empty;
    }

    private static string FallbackName(string rawDevicePath)
    {
        var identity = DeviceIdentityParser.Parse(rawDevicePath);
        if (identity.BusType == KeyboardBusType.Acpi)
            return "Teclado Integrado (Notebook)";
        if (identity.BusType is KeyboardBusType.BluetoothHid or KeyboardBusType.BluetoothLeHid)
            return "Teclado Bluetooth";
        return DeviceIdentityParser.FormatShortId(identity);
    }
}
