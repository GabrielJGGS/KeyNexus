using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KeyNexus.Core;

/// <summary>
/// Coleta detalhes de dispositivos lendo o Registro do Windows (100% gerenciado,
/// sem risco de AccessViolation que as chamadas nativas da SetupAPI podem causar).
/// Também expõe o caminho HID via SetupAPI apenas para resolução de nome amigável.
/// </summary>
internal static class SetupApiHelper
{
    private static int DevicePathOffset => IntPtr.Size == 8 ? 8 : 4;

    /// <summary>
    /// Usado por DeviceNameResolver. Mantém SetupAPI só para HID (caminho testado e estável).
    /// </summary>
    public static string? TryGetHidDevicePath(IntPtr hDevInfo, ref NativeMethods.SP_DEVICE_INTERFACE_DATA did,
        ref NativeMethods.SP_DEVINFO_DATA devInfoData)
    {
        uint requiredSize = 0;
        NativeMethods.SetupDiGetDeviceInterfaceDetail(
            hDevInfo, ref did, IntPtr.Zero, 0, ref requiredSize, ref devInfoData);

        if (requiredSize == 0 || requiredSize > 4096)
            return null;

        IntPtr detailBuf = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            Marshal.WriteInt32(detailBuf, DevicePathOffset);
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(
                hDevInfo, ref did, detailBuf, requiredSize, ref requiredSize, ref devInfoData))
                return null;

            return Marshal.PtrToStringAuto(detailBuf + DevicePathOffset);
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuf);
        }
    }

    /// <summary>
    /// Coleta propriedades do dispositivo lendo HKLM\SYSTEM\CurrentControlSet\Enum.
    /// Funciona para HID e ACPI sem chamadas nativas arriscadas.
    /// </summary>
    public static void CollectProperties(string rawPath, DeviceInfoSection section)
    {
        try
        {
            string? enumKey = RawPathToEnumKey(rawPath);
            if (enumKey == null)
            {
                DeviceInfoCollector.AddRow(section, "Status", "Não foi possível mapear o caminho para o Registro");
                return;
            }

            DeviceInfoCollector.AddRow(section, "Chave do Registro", $@"Enum\{enumKey}");

            #pragma warning disable CA1416
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{enumKey}", false);

            if (key == null)
            {
                DeviceInfoCollector.AddRow(section, "Status", "Dispositivo não encontrado no Registro");
                return;
            }

            AddString(section, "Descrição", key, "DeviceDesc");
            AddString(section, "Nome amigável", key, "FriendlyName");
            AddString(section, "Fabricante", key, "Mfg");
            AddString(section, "Classe", key, "Class");
            AddString(section, "GUID da classe", key, "ClassGUID");
            AddString(section, "Serviço (driver)", key, "Service");
            AddString(section, "Container ID", key, "ContainerID");
            AddMultiString(section, "Hardware IDs", key, "HardwareID");
            AddMultiString(section, "Compatible IDs", key, "CompatibleIDs");
            AddLocationInfo(section, key);
            #pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao ler detalhes do dispositivo no Registro", ex);
            DeviceInfoCollector.AddRow(section, "Erro", ex.Message);
        }
    }

    #pragma warning disable CA1416
    private static void AddString(DeviceInfoSection section, string label, RegistryKey key, string valueName)
    {
        try
        {
            if (key.GetValue(valueName) is string s && !string.IsNullOrWhiteSpace(s))
                DeviceInfoCollector.AddRow(section, label, CleanIndirectString(s));
        }
        catch { }
    }

    private static void AddMultiString(DeviceInfoSection section, string label, RegistryKey key, string valueName)
    {
        try
        {
            var val = key.GetValue(valueName);
            if (val is string[] arr && arr.Length > 0)
                DeviceInfoCollector.AddRow(section, label, string.Join(Environment.NewLine, arr));
            else if (val is string s && !string.IsNullOrWhiteSpace(s))
                DeviceInfoCollector.AddRow(section, label, s);
        }
        catch { }
    }

    private static void AddLocationInfo(DeviceInfoSection section, RegistryKey key)
    {
        try
        {
            if (key.GetValue("LocationInformation") is string s && !string.IsNullOrWhiteSpace(s))
                DeviceInfoCollector.AddRow(section, "Localização", s);
        }
        catch { }
    }
    #pragma warning restore CA1416

    /// <summary>
    /// Remove referências indiretas tipo "@oem12.inf,%desc%;Texto real".
    /// </summary>
    private static string CleanIndirectString(string value)
    {
        if (value.StartsWith("@", StringComparison.Ordinal))
        {
            int semi = value.IndexOf(';');
            if (semi >= 0 && semi < value.Length - 1)
                return value[(semi + 1)..].Trim();
        }
        return value;
    }

    /// <summary>
    /// Converte o raw device path em subchave do Enum.
    /// Ex.: \\?\HID#VID_320F&amp;PID_227C&amp;MI_00#7&amp;abc&amp;0&amp;0000#{guid}
    ///   -> HID\VID_320F&amp;PID_227C&amp;MI_00\7&amp;abc&amp;0&amp;0000
    /// </summary>
    private static string? RawPathToEnumKey(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return null;

        string s = rawPath;

        if (s.StartsWith(@"\\?\", StringComparison.Ordinal) || s.StartsWith(@"\??\", StringComparison.Ordinal))
            s = s[4..];

        int guidIdx = s.LastIndexOf("#{", StringComparison.Ordinal);
        if (guidIdx >= 0)
            s = s[..guidIdx];

        s = s.Replace('#', '\\');

        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
