using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace KeyNexus.Core;

public enum KeyboardBusType
{
    Unknown,
    Acpi,
    UsbHid,
    BluetoothHid,
    BluetoothLeHid
}

public sealed class DeviceIdentity
{
    public string? VendorId { get; init; }
    public string? ProductId { get; init; }
    public KeyboardBusType BusType { get; init; }
    public string BusLabel { get; init; } = "Desconhecido";
    public string? BluetoothAddress { get; init; }
}

/// <summary>
/// Extrai VID/PID e o tipo de barramento de caminhos HID USB e Bluetooth.
/// Bluetooth LE usa VID&amp;02xxxx (fonte USB-IF) em vez de VID_XXXX.
/// </summary>
public static class DeviceIdentityParser
{
    private static readonly Regex VidToken = new(@"VID[_&]([0-9A-F]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PidToken = new(@"PID[_&]([0-9A-F]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BtMacRegex = new(@"REV&[0-9A-F]{4}_([0-9A-F]{12})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public const string BluetoothHidServiceUuid = "00001812-0000-1000-8000-00805F9B34FB";
    public const string BluetoothHidProfileUuid = "00001124-0000-1000-8000-00805F9B34FB";

    public static DeviceIdentity Parse(string? path, IEnumerable<string>? extraIds = null)
    {
        var texts = new List<string>();
        if (!string.IsNullOrWhiteSpace(path))
            texts.Add(path);
        if (extraIds != null)
        {
            foreach (var extra in extraIds)
            {
                if (!string.IsNullOrWhiteSpace(extra))
                    texts.Add(extra);
            }
        }

        string combined = string.Join("\n", texts);
        string? vid = null;
        string? pid = null;
        foreach (var text in texts)
        {
            vid ??= ExtractDeviceId(VidToken, text);
            pid ??= ExtractDeviceId(PidToken, text);
            if (vid != null && pid != null)
                break;
        }

        var bus = DetectBus(combined);
        string? mac = ExtractBluetoothAddress(combined);

        return new DeviceIdentity
        {
            VendorId = vid,
            ProductId = pid,
            BusType = bus,
            BusLabel = FormatBusLabel(bus),
            BluetoothAddress = mac
        };
    }

    public static string FormatShortId(DeviceIdentity identity)
    {
        if (identity.BusType == KeyboardBusType.Acpi)
            return "Teclado Integrado";

        if (!string.IsNullOrEmpty(identity.VendorId) || !string.IsNullOrEmpty(identity.ProductId))
        {
            string prefix = identity.BusType switch
            {
                KeyboardBusType.BluetoothLeHid => "BT LE",
                KeyboardBusType.BluetoothHid => "BT",
                KeyboardBusType.UsbHid => "USB",
                _ => "HID"
            };
            return $"{prefix} VID:{identity.VendorId ?? "????"} PID:{identity.ProductId ?? "????"}";
        }

        return identity.BusLabel;
    }

    public static string FormatBluetoothAddress(string hex12)
    {
        hex12 = hex12.ToUpperInvariant();
        if (hex12.Length != 12)
            return hex12;

        return string.Join(":",
            hex12[..2], hex12[2..4], hex12[4..6],
            hex12[6..8], hex12[8..10], hex12[10..12]);
    }

    private static KeyboardBusType DetectBus(string text)
    {
        string upper = text.ToUpperInvariant();
        if (upper.Contains("ACPI"))
            return KeyboardBusType.Acpi;

        if (upper.Contains(BluetoothHidServiceUuid) || upper.Contains("BTHLEDEVICE") || upper.Contains("BTHLE\\"))
            return KeyboardBusType.BluetoothLeHid;

        if (upper.Contains(BluetoothHidProfileUuid) || upper.Contains("BTHENUM"))
            return KeyboardBusType.BluetoothHid;

        if (upper.Contains("HID"))
            return KeyboardBusType.UsbHid;

        return KeyboardBusType.Unknown;
    }

    private static string FormatBusLabel(KeyboardBusType bus) => bus switch
    {
        KeyboardBusType.Acpi => "ACPI (integrado)",
        KeyboardBusType.BluetoothLeHid => "HID Bluetooth (LE)",
        KeyboardBusType.BluetoothHid => "HID Bluetooth",
        KeyboardBusType.UsbHid => "HID USB",
        _ => "Desconhecido"
    };

    private static string? ExtractDeviceId(Regex token, string text)
    {
        var match = token.Match(text);
        if (!match.Success)
            return null;

        string hex = match.Groups[1].Value.ToUpperInvariant();
        if (hex.Length == 0)
            return null;

        // Bluetooth Device ID: 02xxxx = fonte USB-IF + VID de 4 dígitos.
        if (hex.Length == 6 && (hex.StartsWith("01") || hex.StartsWith("02")))
            return hex[2..];

        if (hex.Length >= 4)
            return hex[^4..];

        return hex.PadLeft(4, '0');
    }

    private static string? ExtractBluetoothAddress(string text)
    {
        var match = BtMacRegex.Match(text);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }
}
