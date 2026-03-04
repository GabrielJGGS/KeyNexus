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

        // Padrão: \\?\HID#VID_XXXX&PID_XXXX
        string upper = rawDevicePath.ToUpperInvariant();
        int vidIdx = upper.IndexOf("VID_");
        int pidIdx = upper.IndexOf("PID_");

        if (vidIdx >= 0 && pidIdx >= 0)
        {
            string vid = upper.Substring(vidIdx + 4, Math.Min(4, upper.Length - vidIdx - 4));
            string pid = upper.Substring(pidIdx + 4, Math.Min(4, upper.Length - pidIdx - 4));
            // Limpa caracteres extras
            vid = vid.Split('&')[0].Split('#')[0];
            pid = pid.Split('&')[0].Split('#')[0];
            return $"VID:{vid} PID:{pid}";
        }

        // Para dispositivos ACPI (teclado do notebook)
        if (upper.Contains("ACPI"))
            return "Teclado Integrado";

        return rawDevicePath.Length > 30 ? rawDevicePath[..30] + "..." : rawDevicePath;
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
                    uint requiredSize = 0;
                    var devInfoData = new NativeMethods.SP_DEVINFO_DATA();
                    devInfoData.cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.SP_DEVINFO_DATA));

                    // Primeiro: obter tamanho necessário
                    NativeMethods.SetupDiGetDeviceInterfaceDetail(
                        hDevInfo, ref did, IntPtr.Zero, 0, ref requiredSize, ref devInfoData);

                    IntPtr detailBuf = Marshal.AllocHGlobal((int)requiredSize);
                    try
                    {
                        // cbSize do SP_DEVICE_INTERFACE_DETAIL_DATA:
                        // Em 64-bit = 8, em 32-bit = 5 (4 + 1 TCHAR)
                        Marshal.WriteInt32(detailBuf, IntPtr.Size == 8 ? 8 : 4 + Marshal.SystemDefaultCharSize);

                        if (NativeMethods.SetupDiGetDeviceInterfaceDetail(
                            hDevInfo, ref did, detailBuf, requiredSize, ref requiredSize, ref devInfoData))
                        {
                            // O caminho do device começa em offset 4
                            string devicePath = Marshal.PtrToStringAuto(detailBuf + 4) ?? "";

                            // Compara usando apenas VID e PID
                            if (PathsMatch(rawDevicePath, devicePath))
                            {
                                string desc = GetDeviceDescription(hDevInfo, ref devInfoData);
                                if (!string.IsNullOrEmpty(desc))
                                    return desc;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detailBuf);
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
        // Comparação simples: encontrar VID_XXXX&PID_XXXX em ambos
        string rawUpper = rawPath.ToUpperInvariant();
        string setupUpper = setupPath.ToUpperInvariant();

        int rVid = rawUpper.IndexOf("VID_");
        int sVid = setupUpper.IndexOf("VID_");

        if (rVid < 0 || sVid < 0) return false;

        // Extrai segmento VID_XXXX&PID_XXXX
        string ExtractVidPid(string s, int start)
        {
            int end = s.IndexOf('#', start);
            if (end < 0) end = s.IndexOf('\\', start);
            if (end < 0) end = s.Length;
            return s.Substring(start, end - start);
        }

        return ExtractVidPid(rawUpper, rVid) == ExtractVidPid(setupUpper, sVid);
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
        string upper = rawDevicePath.ToUpperInvariant();
        if (upper.Contains("ACPI"))
            return "Teclado Integrado (Notebook)";
        return GetShortId(rawDevicePath);
    }
}
