using System;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Win32;

namespace KeyNexus.Core;

/// <summary>
/// Nome real do teclado: o nome pareado no Bluetooth, a string de produto do USB,
/// "Teclado do notebook" no ACPI. Evita o genérico "Dispositivo de teclado HID".
/// </summary>
public static class DeviceNameResolver
{
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] GenericMarkers =
    {
        "HID", "USB Input", "Composite", "Composto", "Dispositivo de entrada", "Input Device",
        "GATT", "Generic", "Genéric", "Standard", "Padrão", "Keyboard Device", "Dispositivo de teclado",
    };

    /// <summary>Nome amigável do teclado. Pode tocar no registro e no cfgmgr32: evite chamar na thread da UI.</summary>
    public static string GetFriendlyName(string rawDevicePath)
    {
        if (string.IsNullOrEmpty(rawDevicePath))
            return "Teclado";

        return Cache.GetOrAdd(rawDevicePath, Resolve);
    }

    public static bool TryGetCachedName(string rawDevicePath, out string name) =>
        Cache.TryGetValue(rawDevicePath ?? string.Empty, out name!);

    /// <summary>VID/PID resumido para a tela de detalhes.</summary>
    public static string GetShortId(string rawDevicePath)
    {
        if (string.IsNullOrEmpty(rawDevicePath))
            return "???";

        return DeviceIdentityParser.FormatShortId(
            DeviceIdentityParser.Parse(rawDevicePath, SetupApiHelper.GetHardwareIds(rawDevicePath)));
    }

    public static string GetTypeLabel(KeyboardBusType bus) => bus switch
    {
        KeyboardBusType.Acpi => "Teclado do notebook",
        KeyboardBusType.BluetoothHid or KeyboardBusType.BluetoothLeHid => "Bluetooth",
        KeyboardBusType.UsbHid => "USB",
        KeyboardBusType.Virtual => "Virtual, criado por um programa",
        _ => "Teclado"
    };

    /// <summary>Nome genérico a partir da chave de grupo, para teclados desconectados.</summary>
    public static string GetFallbackName(string groupKey)
    {
        var identity = DeviceIdentityParser.Parse(groupKey);
        string baseName = GenericName(identity);
        return !string.IsNullOrEmpty(identity.VendorId) && !string.IsNullOrEmpty(identity.ProductId)
            ? $"{baseName} ({identity.VendorId}:{identity.ProductId})"
            : baseName;
    }

    private static string Resolve(string rawPath)
    {
        try
        {
            var identity = DeviceIdentityParser.Parse(rawPath);
            if (identity.BusType == KeyboardBusType.Acpi)
                return "Teclado do notebook";

            string? instanceId = SetupApiHelper.RawPathToEnumKey(rawPath);
            string? name = instanceId != null ? FromDeviceTree(instanceId) : null;

            if (name == null && identity.BusType is KeyboardBusType.BluetoothHid or KeyboardBusType.BluetoothLeHid)
                name = FromBluetoothContainer(rawPath);

            name ??= Useful(HidDeviceInspector.TryGetProductString(rawPath));
            return name ?? GenericName(identity);
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao resolver nome do teclado", ex);
            return "Teclado";
        }
    }

    /// <summary>
    /// Sobe na árvore de dispositivos: o nó BTHLE/BTHENUM tem o nome pareado;
    /// o nó USB tem a string de produto que o próprio teclado informa.
    /// </summary>
    private static string? FromDeviceTree(string instanceId)
    {
        if (NativeMethods.CM_Locate_DevNodeW(out uint node, instanceId, 0) != NativeMethods.CR_SUCCESS)
            return null;

        for (int depth = 0; depth < 8 && node != 0; depth++)
        {
            string id = GetDeviceId(node).ToUpperInvariant();

            if (id.StartsWith(@"BTHLE\DEV_", StringComparison.Ordinal)
                || id.StartsWith(@"BTHENUM\DEV_", StringComparison.Ordinal))
            {
                var name = Useful(GetStringProperty(node, NativeMethods.DEVPKEY_Device_FriendlyName))
                    ?? Useful(GetStringProperty(node, NativeMethods.DEVPKEY_NAME));
                if (name != null)
                    return name;
            }
            else if (id.StartsWith(@"USB\VID_", StringComparison.Ordinal)
                || id.StartsWith(@"BTHLEDEVICE\", StringComparison.Ordinal)
                || id.StartsWith(@"BTHENUM\", StringComparison.Ordinal))
            {
                var name = Useful(GetStringProperty(node, NativeMethods.DEVPKEY_Device_BusReportedDeviceDesc));
                if (name != null)
                    return name;
            }

            if (id.StartsWith(@"USB\ROOT_HUB", StringComparison.Ordinal)
                || id.StartsWith(@"PCI\", StringComparison.Ordinal)
                || id.StartsWith(@"ACPI\", StringComparison.Ordinal)
                || id.StartsWith(@"ROOT\", StringComparison.Ordinal)
                || id.StartsWith(@"BTH\", StringComparison.Ordinal))
                break;

            if (NativeMethods.CM_Get_Parent(out uint parent, node, 0) != NativeMethods.CR_SUCCESS)
                break;
            node = parent;
        }

        return null;
    }

    /// <summary>
    /// Bluetooth clássico: o nó com o nome (BTHENUM\Dev_MAC) não é ancestral da coleção HID,
    /// mas compartilha o ContainerID.
    /// </summary>
    private static string? FromBluetoothContainer(string rawPath)
    {
        string? container = SetupApiHelper.GetContainerId(rawPath);
        if (string.IsNullOrEmpty(container))
            return null;

        foreach (string enumerator in new[] { "BTHLE", "BTHENUM" })
        {
            try
            {
                #pragma warning disable CA1416
                using var root = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{enumerator}", false);
                if (root == null)
                    continue;

                foreach (string deviceKey in root.GetSubKeyNames())
                {
                    if (!deviceKey.StartsWith("Dev_", StringComparison.OrdinalIgnoreCase))
                        continue;

                    using var device = root.OpenSubKey(deviceKey, false);
                    if (device == null)
                        continue;

                    foreach (string instanceKey in device.GetSubKeyNames())
                    {
                        using var instance = device.OpenSubKey(instanceKey, false);
                        if (instance?.GetValue("ContainerID") is string id
                            && string.Equals(id, container, StringComparison.OrdinalIgnoreCase))
                        {
                            var name = Useful(instance.GetValue("FriendlyName") as string);
                            if (name != null)
                                return name;
                        }
                    }
                }
                #pragma warning restore CA1416
            }
            catch
            {
                // Sem acesso a este ramo do registro: tenta o próximo.
            }
        }

        return null;
    }

    private static string GetDeviceId(uint node)
    {
        var buffer = new char[NativeMethods.MAX_DEVICE_ID_LEN + 1];
        return NativeMethods.CM_Get_Device_IDW(node, buffer, buffer.Length, 0) == NativeMethods.CR_SUCCESS
            ? new string(buffer).TrimEnd('\0')
            : string.Empty;
    }

    private static string? GetStringProperty(uint node, NativeMethods.DEVPROPKEY key)
    {
        uint size = 0;
        NativeMethods.CM_Get_DevNode_PropertyW(node, ref key, out _, null, ref size, 0);
        if (size == 0 || size > 4096)
            return null;

        var buffer = new byte[size];
        if (NativeMethods.CM_Get_DevNode_PropertyW(node, ref key, out _, buffer, ref size, 0) != NativeMethods.CR_SUCCESS)
            return null;

        return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim();
    }

    private static string? Useful(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        foreach (var marker in GenericMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return name.Trim();
    }

    private static string GenericName(DeviceIdentity identity) => identity.BusType switch
    {
        KeyboardBusType.Acpi => "Teclado do notebook",
        KeyboardBusType.BluetoothHid or KeyboardBusType.BluetoothLeHid => "Teclado Bluetooth",
        KeyboardBusType.UsbHid => "Teclado USB",
        KeyboardBusType.Virtual => "Teclado virtual",
        _ => "Teclado"
    };
}
