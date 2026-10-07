using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace KeyNexus.Core;

internal sealed class HidInterfaceSnapshot
{
    public string Path { get; init; } = string.Empty;
    public string HandleText { get; init; } = "—";
    public string RawTypeLabel { get; init; } = string.Empty;
    public string? UsageLabel { get; init; }
    public string? KeyboardSummary { get; init; }
    public string? HidVidPid { get; init; }
    public string? ReportSizes { get; init; }
    public string? FeatureHint { get; init; }
    public string LayoutHint { get; init; } = string.Empty;
    public string? NotableUsages { get; init; }
    public string? Product { get; init; }
    public string? Manufacturer { get; init; }
    public string? Serial { get; init; }
    public string? DirectQuery { get; init; }
}

/// <summary>
/// Lê caps HID via Raw Input (RIDI_PREPARSEDDATA) sem abrir a coleção de teclado.
/// Tenta strings/atributos com CreateFile(access=0) nas coleções que o Windows permitir.
/// </summary>
internal static class HidDeviceInspector
{
    private static readonly Dictionary<ushort, string> NotableKeyboardUsages = new()
    {
        [0x32] = "Non-US # (ISO)",
        [0x35] = "Grave/Tilde",
        [0x64] = "Non-US \\ (ISO)",
        [0x87] = "International1 (ABNT/JIS Ro)",
        [0x88] = "International2 (Kana)",
        [0x89] = "International3 (Yen)",
        [0x8A] = "International4 (Convert)",
        [0x8B] = "International5 (NoConvert)",
        [0x8C] = "International6",
        [0x90] = "LANG1 (Hangul)",
        [0x91] = "LANG2 (Hanja)",
        [0x92] = "LANG3",
        [0x93] = "LANG4",
        [0x94] = "LANG5"
    };

    public static List<HidInterfaceSnapshot> InspectGroup(string groupKey, IReadOnlyList<string> knownPaths)
    {
        var snapshots = new List<HidInterfaceSnapshot>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (handle, dwType, name) in Input.RawInputDevices.Enumerate())
        {

            bool listed = ContainsPath(knownPaths, name);
            bool sameGroup = DeviceGrouping.GetGroupKey(name)
                .Equals(groupKey, StringComparison.OrdinalIgnoreCase);

            if (!listed && !sameGroup)
                continue;

            seenPaths.Add(name);
            snapshots.Add(InspectHandle(handle, dwType, name));
        }

        foreach (var path in knownPaths)
        {
            if (string.IsNullOrEmpty(path) || seenPaths.Contains(path))
                continue;
            snapshots.Add(InspectPathWithoutHandle(path));
        }

        return snapshots;
    }

    /// <summary>
    /// String de produto do firmware (HidD_GetProductString), quando o Windows deixa abrir a coleção.
    /// </summary>
    internal static string? TryGetProductString(string path)
    {
        IntPtr handle = OpenHidQuery(path);
        if (handle == NativeMethods.INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
            return null;

        try
        {
            return ReadHidString(NativeMethods.HidD_GetProductString, handle);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static HidInterfaceSnapshot InspectHandle(IntPtr hDevice, uint dwType, string path)
    {
        var info = GetDeviceInfo(hDevice);
        var caps = GetCapsFromRawInput(hDevice);
        if (caps == null)
            caps = TryCapsFromCreateFile(path);

        var strings = TryQueryHidStrings(path);

        string? usageLabel = null;
        if (caps != null)
            usageLabel = FormatUsage(caps.Value.UsagePage, caps.Value.Usage);
        else if (info != null && info.Value.dwType == NativeMethods.RIM_TYPEHID)
            usageLabel = FormatUsage(info.Value.hid.usUsagePage, info.Value.hid.usUsage);

        string? keyboardSummary = null;
        string? hidVidPid = null;
        if (info != null && info.Value.dwType == NativeMethods.RIM_TYPEKEYBOARD)
        {
            var k = info.Value.keyboard;
            keyboardSummary =
                $"tipo {k.dwType}, subtipo {k.dwSubType}, modo {k.dwKeyboardMode}, " +
                $"{k.dwNumberOfFunctionKeys} Fn, {k.dwNumberOfIndicators} indicadores, " +
                $"{k.dwNumberOfKeysTotal} teclas";
        }
        else if (info != null && info.Value.dwType == NativeMethods.RIM_TYPEHID)
        {
            var h = info.Value.hid;
            hidVidPid = $"VID {h.dwVendorId:X4}  PID {h.dwProductId:X4}  rev {h.dwVersionNumber:X4}";
        }

        string? reportSizes = null;
        string? featureHint = null;
        string layoutHint = "Não foi possível ler o descritor HID.";
        string? notable = null;

        if (caps != null)
        {
            var c = caps.Value;
            reportSizes =
                $"input {c.InputReportByteLength} B, output {c.OutputReportByteLength} B, feature {c.FeatureReportByteLength} B";
            if (c.FeatureReportByteLength > 0)
                featureHint = "O firmware expõe feature reports (possível protocolo do fabricante).";

            var usages = GetKeyboardUsages(hDevice, path, c);
            (layoutHint, notable) = BuildLayoutHint(c, usages);
        }

        return new HidInterfaceSnapshot
        {
            Path = path,
            HandleText = $"0x{hDevice.ToInt64():X}",
            RawTypeLabel = RawTypeLabel(dwType),
            UsageLabel = usageLabel,
            KeyboardSummary = keyboardSummary,
            HidVidPid = hidVidPid ?? strings.AttributesVidPid,
            ReportSizes = reportSizes,
            FeatureHint = featureHint,
            LayoutHint = layoutHint,
            NotableUsages = notable,
            Product = strings.Product,
            Manufacturer = strings.Manufacturer,
            Serial = strings.Serial,
            DirectQuery = strings.Status
        };
    }

    private static HidInterfaceSnapshot InspectPathWithoutHandle(string path)
    {
        var strings = TryQueryHidStrings(path);
        var caps = TryCapsFromCreateFile(path);
        string layoutHint = "Handle Raw Input não encontrado para este caminho.";
        string? notable = null;
        string? reportSizes = null;
        string? featureHint = null;
        string? usageLabel = null;

        if (caps != null)
        {
            var c = caps.Value;
            usageLabel = FormatUsage(c.UsagePage, c.Usage);
            reportSizes =
                $"input {c.InputReportByteLength} B, output {c.OutputReportByteLength} B, feature {c.FeatureReportByteLength} B";
            if (c.FeatureReportByteLength > 0)
                featureHint = "O firmware expõe feature reports (possível protocolo do fabricante).";
            var usages = GetKeyboardUsages(IntPtr.Zero, path, c);
            (layoutHint, notable) = BuildLayoutHint(c, usages);
        }

        return new HidInterfaceSnapshot
        {
            Path = path,
            HandleText = "não encontrado",
            RawTypeLabel = "—",
            UsageLabel = usageLabel,
            ReportSizes = reportSizes,
            FeatureHint = featureHint,
            LayoutHint = layoutHint,
            NotableUsages = notable,
            Product = strings.Product,
            Manufacturer = strings.Manufacturer,
            Serial = strings.Serial,
            DirectQuery = strings.Status
        };
    }

    private static NativeMethods.RID_DEVICE_INFO? GetDeviceInfo(IntPtr hDevice)
    {
        uint size = (uint)Marshal.SizeOf<NativeMethods.RID_DEVICE_INFO>();
        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            Marshal.WriteInt32(buf, (int)size);
            uint result = NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICEINFO, buf, ref size);
            if (result == unchecked((uint)-1) || result == 0)
                return null;
            return Marshal.PtrToStructure<NativeMethods.RID_DEVICE_INFO>(buf);
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static HidCaps? GetCapsFromRawInput(IntPtr hDevice)
    {
        uint size = 0;
        NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0 || size > 1_000_000)
            return null;

        IntPtr buf = Marshal.AllocHGlobal((int)size);
        try
        {
            uint result = NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_PREPARSEDDATA, buf, ref size);
            if (result == unchecked((uint)-1) || result == 0)
                return null;
            return ParseCaps(buf);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static HidCaps? TryCapsFromCreateFile(string path)
    {
        IntPtr handle = OpenHidQuery(path);
        if (handle == NativeMethods.INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
            return null;

        try
        {
            if (!NativeMethods.HidD_GetPreparsedData(handle, out IntPtr preparsed) || preparsed == IntPtr.Zero)
                return null;
            try
            {
                return ParseCaps(preparsed);
            }
            finally
            {
                NativeMethods.HidD_FreePreparsedData(preparsed);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static HidCaps? ParseCaps(IntPtr preparsed)
    {
        IntPtr buf = Marshal.AllocHGlobal(64);
        try
        {
            uint status = NativeMethods.HidP_GetCaps(preparsed, buf);
            if (status != NativeMethods.HIDP_STATUS_SUCCESS)
                return null;

            byte[] bytes = new byte[64];
            Marshal.Copy(buf, bytes, 0, 62);
            ushort U16(int index) => BitConverter.ToUInt16(bytes, index * 2);

            return new HidCaps
            {
                Usage = U16(0),
                UsagePage = U16(1),
                InputReportByteLength = U16(2),
                OutputReportByteLength = U16(3),
                FeatureReportByteLength = U16(4),
                NumberInputButtonCaps = U16(23)
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static List<(ushort page, ushort min, ushort max)> GetKeyboardUsages(
        IntPtr rawHandle, string path, HidCaps caps)
    {
        var usages = new List<(ushort, ushort, ushort)>();
        if (caps.NumberInputButtonCaps == 0 || caps.NumberInputButtonCaps > 512)
            return usages;

        if (rawHandle != IntPtr.Zero)
        {
            uint size = 0;
            NativeMethods.GetRawInputDeviceInfo(rawHandle, NativeMethods.RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
            if (size > 0 && size < 1_000_000)
            {
                IntPtr buf = Marshal.AllocHGlobal((int)size);
                try
                {
                    uint result = NativeMethods.GetRawInputDeviceInfo(
                        rawHandle, NativeMethods.RIDI_PREPARSEDDATA, buf, ref size);
                    if (result != unchecked((uint)-1) && result != 0)
                        return ReadButtonCaps(buf, caps.NumberInputButtonCaps);
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }

        IntPtr file = OpenHidQuery(path);
        if (file == NativeMethods.INVALID_HANDLE_VALUE || file == IntPtr.Zero)
            return usages;

        try
        {
            if (!NativeMethods.HidD_GetPreparsedData(file, out IntPtr preparsed) || preparsed == IntPtr.Zero)
                return usages;
            try
            {
                return ReadButtonCaps(preparsed, caps.NumberInputButtonCaps);
            }
            finally
            {
                NativeMethods.HidD_FreePreparsedData(preparsed);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(file);
        }
    }

    private static List<(ushort page, ushort min, ushort max)> ReadButtonCaps(IntPtr preparsed, ushort count)
    {
        var list = new List<(ushort, ushort, ushort)>();
        int bytes = count * NativeMethods.HIDP_BUTTON_CAPS_SIZE;
        IntPtr buf = Marshal.AllocHGlobal(bytes);
        try
        {
            ushort length = count;
            uint status = NativeMethods.HidP_GetButtonCaps(NativeMethods.HidP_Input, buf, ref length, preparsed);
            if (status != NativeMethods.HIDP_STATUS_SUCCESS)
                return list;

            byte[] data = new byte[bytes];
            Marshal.Copy(buf, data, 0, bytes);
            for (int i = 0; i < length; i++)
            {
                int o = i * NativeMethods.HIDP_BUTTON_CAPS_SIZE;
                ushort page = BitConverter.ToUInt16(data, o);
                bool isRange = data[o + 12] != 0;
                ushort usageMin = BitConverter.ToUInt16(data, o + 56);
                ushort usageMax = isRange ? BitConverter.ToUInt16(data, o + 58) : usageMin;
                list.Add((page, usageMin, usageMax));
            }
        }
        catch
        {
            // Caps malformados: ignora usages e mantém o restante do relatório.
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return list;
    }

    private static (string hint, string? notable) BuildLayoutHint(
        HidCaps caps, List<(ushort page, ushort min, ushort max)> usages)
    {
        bool keyboardCollection = caps.UsagePage == 0x01 && caps.Usage is 0x06 or 0x07;
        if (!keyboardCollection && usages.TrueForAll(u => u.page != 0x07))
        {
            return (
                caps.UsagePage == 0x0C
                    ? "Coleção Consumer Control (mídia/volume). Sem layout de teclado."
                    : "Coleção sem Usage Page de teclado.",
                null);
        }

        bool genericRange = false;
        var found = new SortedSet<ushort>();
        foreach (var (page, min, max) in usages)
        {
            if (page != 0x07)
                continue;
            if (max >= min && (max - min) >= 80)
                genericRange = true;

            foreach (var kv in NotableKeyboardUsages)
            {
                if (kv.Key >= min && kv.Key <= max)
                    found.Add(kv.Key);
            }
        }

        string? notable = found.Count == 0
            ? null
            : string.Join(", ", found.Select(u => $"{NotableKeyboardUsages[u]} (0x{u:X2})"));

        if (genericRange)
        {
            return (
                "O descritor declara um intervalo genérico de teclas. " +
                "Isso não revela se o físico é ANSI, ISO ou ABNT.",
                notable);
        }

        bool iso = found.Contains(0x32) || found.Contains(0x64);
        bool jis = found.Contains(0x88) || found.Contains(0x89) || found.Contains(0x8A) || found.Contains(0x8B);
        bool intl1 = found.Contains(0x87);
        bool korean = found.Contains(0x90) || found.Contains(0x91);

        string hint;
        if (jis)
            hint = "Pista de fábrica: teclas JIS (Yen/Kana/Convert) no descritor.";
        else if (intl1)
            hint = "Pista de fábrica: International1 presente (ABNT brasileiro ou JIS).";
        else if (iso)
            hint = "Pista de fábrica: teclas Non-US (físico ISO).";
        else if (korean)
            hint = "Pista de fábrica: teclas LANG (coreano).";
        else
            hint = "Nenhuma tecla ISO/ABNT/JIS no descritor. Comum em ANSI ou firmware que não declara extras.";

        return (hint, notable);
    }

    private static HidStringQuery TryQueryHidStrings(string path)
    {
        IntPtr handle = OpenHidQuery(path);
        if (handle == NativeMethods.INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
        {
            return new HidStringQuery
            {
                Status = "Windows bloqueou CreateFile nesta coleção (normal em teclado de sistema)."
            };
        }

        try
        {
            var attrs = new NativeMethods.HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<NativeMethods.HIDD_ATTRIBUTES>() };
            string? vidPid = null;
            if (NativeMethods.HidD_GetAttributes(handle, ref attrs))
                vidPid = $"VID {attrs.VendorID:X4}  PID {attrs.ProductID:X4}  rev {attrs.VersionNumber:X4}";

            return new HidStringQuery
            {
                Product = ReadHidString(NativeMethods.HidD_GetProductString, handle),
                Manufacturer = ReadHidString(NativeMethods.HidD_GetManufacturerString, handle),
                Serial = ReadHidString(NativeMethods.HidD_GetSerialNumberString, handle),
                AttributesVidPid = vidPid,
                Status = "Consulta direta (CreateFile acesso 0) aceita nesta coleção."
            };
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static IntPtr OpenHidQuery(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return NativeMethods.INVALID_HANDLE_VALUE;

        return NativeMethods.CreateFile(
            path,
            0,
            NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);
    }

    private static string? ReadHidString(HidStringFn fn, IntPtr handle)
    {
        byte[] buffer = new byte[512];
        if (!fn(handle, buffer, (uint)buffer.Length))
            return null;
        string value = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool ContainsPath(IReadOnlyList<string> paths, string name)
    {
        foreach (var path in paths)
        {
            if (string.Equals(path, name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string RawTypeLabel(uint dwType) => dwType switch
    {
        NativeMethods.RIM_TYPEKEYBOARD => "Keyboard (Raw Input)",
        NativeMethods.RIM_TYPEHID => "HID genérico (Raw Input)",
        0 => "Mouse (Raw Input)",
        _ => $"tipo {dwType}"
    };

    internal static string FormatUsage(ushort page, ushort usage)
    {
        string name = (page, usage) switch
        {
            (0x01, 0x06) => "Generic Desktop / Keyboard",
            (0x01, 0x07) => "Generic Desktop / Keypad",
            (0x0C, 0x01) => "Consumer / Consumer Control",
            (0x01, 0x80) => "Generic Desktop / System Control",
            _ when page >= 0xFF00 => "Vendor-defined",
            _ => "ver HID Usage Tables"
        };
        return $"0x{page:X4}/0x{usage:X4} ({name})";
    }

    private delegate bool HidStringFn(IntPtr handle, byte[] buffer, uint length);

    private readonly struct HidCaps
    {
        public ushort Usage { get; init; }
        public ushort UsagePage { get; init; }
        public ushort InputReportByteLength { get; init; }
        public ushort OutputReportByteLength { get; init; }
        public ushort FeatureReportByteLength { get; init; }
        public ushort NumberInputButtonCaps { get; init; }
    }

    private readonly struct HidStringQuery
    {
        public string? Product { get; init; }
        public string? Manufacturer { get; init; }
        public string? Serial { get; init; }
        public string? AttributesVidPid { get; init; }
        public string Status { get; init; }
    }
}
