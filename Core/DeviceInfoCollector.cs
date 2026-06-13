using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace KeyNexus.Core;

public class DeviceInfoRow
{
    public string Label { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class DeviceInfoSection
{
    public string Title { get; set; } = string.Empty;
    public List<DeviceInfoRow> Rows { get; set; } = new();
}

public class DeviceInfoReport
{
    public string DeviceTitle { get; set; } = string.Empty;
    public List<DeviceInfoSection> Sections { get; set; } = new();
}

public static class DeviceInfoCollector
{
    private static readonly Regex VidRegex = new(@"VID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PidRegex = new(@"PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MiRegex = new(@"MI_([0-9A-F]{2})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ColRegex = new(@"Col(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static DeviceInfoReport Collect(
        string groupKey,
        string displayName,
        string representativePath,
        IReadOnlyList<string> rawPaths,
        ConfigManager config)
    {
        var report = new DeviceInfoReport { DeviceTitle = displayName };
        var paths = rawPaths ?? Array.Empty<string>();
        bool isAcpi = representativePath.Contains("ACPI", StringComparison.OrdinalIgnoreCase);

        var ident = new DeviceInfoSection { Title = "Identificação" };
        AddRow(ident, "Nome exibido", displayName);
        AddRow(ident, "Apelido no KeyNexus", config.GetDeviceAlias(groupKey) ?? "(nenhum)");
        AddRow(ident, "Chave de agrupamento", groupKey);
        AddRow(ident, "VID", ExtractMatch(VidRegex, representativePath) ?? "—");
        AddRow(ident, "PID", ExtractMatch(PidRegex, representativePath) ?? "—");
        AddRow(ident, "Tipo", isAcpi ? "ACPI (integrado)" : "HID USB");
        AddRow(ident, "Caminho principal", representativePath);
        report.Sections.Add(ident);

        var interfaces = new DeviceInfoSection { Title = $"Interfaces Windows ({paths.Count})" };
        if (paths.Count == 0)
            AddRow(interfaces, "Status", "Nenhuma interface listada");
        else
        {
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                AddRow(interfaces, $"Interface {i + 1}", path);
                string? mi = ExtractMatch(MiRegex, path);
                string? col = ExtractMatch(ColRegex, path);
                if (mi != null) AddRow(interfaces, "  └ MI", mi);
                if (col != null) AddRow(interfaces, "  └ Coleção", col);
                string? instance = ExtractInstanceId(path);
                if (instance != null) AddRow(interfaces, "  └ Instância", instance);
            }
        }
        report.Sections.Add(interfaces);

        var setup = new DeviceInfoSection { Title = "Detalhes do Windows" };
        SetupApiHelper.CollectProperties(representativePath, setup);
        report.Sections.Add(setup);

        var rawInput = CollectRawInputInfo(paths);
        if (rawInput.Rows.Count > 0)
            report.Sections.Add(rawInput);

        var keynexus = new DeviceInfoSection { Title = "Configuração KeyNexus" };
        string? layout = config.GetLayoutForDevice(groupKey);
        AddRow(keynexus, "Layout vinculado", string.IsNullOrEmpty(layout) ? "(nenhum)" : layout);
        var rules = config.GetRemapRules(groupKey);
        AddRow(keynexus, "Regras de mapeamento", rules.Count.ToString());
        foreach (var rule in rules)
        {
            string trigger = VkHelper.FormatTrigger(rule.TriggerVk, rule.Modifiers);
            string output = rule.OutputType switch
            {
                RemapOutputType.Key => VkHelper.GetKeyName(rule.OutputVk),
                RemapOutputType.Text => $"\"{rule.OutputText}\"",
                RemapOutputType.Sequence => $"{rule.Sequence.Count} passo(s)",
                _ => "?"
            };
            AddRow(keynexus, $"  └ {trigger}", $"→ {output}");
        }
        report.Sections.Add(keynexus);

        return report;
    }

    private static DeviceInfoSection CollectRawInputInfo(IReadOnlyList<string> rawPaths)
    {
        var section = new DeviceInfoSection { Title = "Raw Input" };

        try
        {
            var handles = GetRawInputHandles();

            foreach (var path in rawPaths)
            {
                if (handles.TryGetValue(path, out IntPtr hDevice))
                {
                    AddRow(section, ShortPath(path), $"Handle 0x{hDevice.ToInt64():X}");
                }
                else
                {
                    AddRow(section, ShortPath(path), "Handle não encontrado na lista atual");
                }
            }
        }
        catch (Exception ex)
        {
            AddRow(section, "Erro", ex.Message);
        }

        return section;
    }

    private static Dictionary<string, IntPtr> GetRawInputHandles()
    {
        var map = new Dictionary<string, IntPtr>(StringComparer.OrdinalIgnoreCase);
        uint count = 0;
        uint dwSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICELIST>();

        if (NativeMethods.GetRawInputDeviceList(IntPtr.Zero, ref count, dwSize) != 0)
            return map;

        if (count == 0) return map;

        IntPtr list = Marshal.AllocHGlobal((int)(dwSize * count));
        try
        {
            if (NativeMethods.GetRawInputDeviceList(list, ref count, dwSize) == unchecked((uint)-1))
                return map;

            for (int i = 0; i < count; i++)
            {
                IntPtr ptr = new IntPtr(list.ToInt64() + (i * dwSize));
                var item = Marshal.PtrToStructure<NativeMethods.RAWINPUTDEVICELIST>(ptr);
                if (item.dwType != NativeMethods.RIM_TYPEKEYBOARD)
                    continue;

                string? name = GetRawDeviceName(item.hDevice);
                if (!string.IsNullOrEmpty(name))
                    map[name] = item.hDevice;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(list);
        }
        return map;
    }

    private static string? GetRawDeviceName(IntPtr hDevice)
    {
        try
        {
            uint pcbSize = 0;
            NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICENAME, IntPtr.Zero, ref pcbSize);
            if (pcbSize == 0 || pcbSize > 8192) return null;

            // pcbSize vem em caracteres; aloca em bytes (Unicode = 2 bytes/char) + folga
            IntPtr pData = Marshal.AllocHGlobal((int)pcbSize * 2);
            try
            {
                uint result = NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICENAME, pData, ref pcbSize);
                if (result == unchecked((uint)-1) || result == 0) return null;
                return Marshal.PtrToStringAuto(pData);
            }
            finally
            {
                Marshal.FreeHGlobal(pData);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractMatch(Regex regex, string path)
    {
        var m = regex.Match(path);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ExtractInstanceId(string path)
    {
        string upper = path.ToUpperInvariant();
        int firstHash = upper.IndexOf('#');
        if (firstHash < 0) return null;
        int secondHash = upper.IndexOf('#', firstHash + 1);
        if (secondHash < 0) return null;
        int thirdHash = upper.IndexOf('#', secondHash + 1);
        string instance = thirdHash > secondHash
            ? path[(secondHash + 1)..thirdHash]
            : path[(secondHash + 1)..];
        return instance.TrimEnd('\\');
    }

    private static string ShortPath(string path) =>
        path.Length > 72 ? path[..69] + "..." : path;

    internal static void AddRow(DeviceInfoSection section, string label, string value) =>
        section.Rows.Add(new DeviceInfoRow { Label = label, Value = value });

    internal static void AddIfPresent(DeviceInfoSection section, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            AddRow(section, label, value);
    }
}
