using System;
using System.Collections.Generic;
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

        var extraIds = new List<string>(paths);
        extraIds.AddRange(SetupApiHelper.GetHardwareIds(representativePath));
        var identity = DeviceIdentityParser.Parse(representativePath, extraIds);

        var ident = new DeviceInfoSection { Title = "Identificação" };
        AddRow(ident, "Nome exibido", displayName);
        AddRow(ident, "Apelido no KeyNexus", config.GetDeviceAlias(groupKey) ?? "(nenhum)");
        AddRow(ident, "Chave de agrupamento", groupKey);
        AddRow(ident, "VID", identity.VendorId ?? "—");
        AddRow(ident, "PID", identity.ProductId ?? "—");
        AddRow(ident, "Tipo", identity.BusLabel);
        if (!string.IsNullOrEmpty(identity.BluetoothAddress))
            AddRow(ident, "Endereço Bluetooth", DeviceIdentityParser.FormatBluetoothAddress(identity.BluetoothAddress));
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

        foreach (var hidSection in CollectHidSections(groupKey, paths))
            report.Sections.Add(hidSection);

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

    private static IEnumerable<DeviceInfoSection> CollectHidSections(string groupKey, IReadOnlyList<string> rawPaths)
    {
        List<HidInterfaceSnapshot> snapshots;
        try
        {
            snapshots = HidDeviceInspector.InspectGroup(groupKey, rawPaths);
        }
        catch (Exception ex)
        {
            var error = new DeviceInfoSection { Title = "HID" };
            AddRow(error, "Erro", ex.Message);
            return new[] { error };
        }

        if (snapshots.Count == 0)
        {
            var empty = new DeviceInfoSection { Title = "HID" };
            AddRow(empty, "Status", "Nenhuma coleção HID encontrada no Raw Input");
            return new[] { empty };
        }

        var sections = new List<DeviceInfoSection>();
        var country = new DeviceInfoSection { Title = "Layout de fábrica" };
        AddRow(country, "Código de país HID",
            "Não exposto pelo Windows (kbdhid). O campo bCountryCode não chega ao Raw Input.");
        AddRow(country, "Como ler",
            "As pistas abaixo vêm do descritor de relatório (usages), não do layout do SO.");
        sections.Add(country);

        for (int i = 0; i < snapshots.Count; i++)
        {
            var snap = snapshots[i];
            var section = new DeviceInfoSection { Title = $"Coleção HID {i + 1}" };
            AddRow(section, "Tipo Raw Input", snap.RawTypeLabel);
            AddRow(section, "Handle", snap.HandleText);
            AddIfPresent(section, "Usage", snap.UsageLabel ?? "");
            AddIfPresent(section, "Resumo do teclado", snap.KeyboardSummary ?? "");
            AddIfPresent(section, "VID/PID HID", snap.HidVidPid ?? "");
            AddIfPresent(section, "Tamanho dos relatórios", snap.ReportSizes ?? "");
            AddIfPresent(section, "Feature reports", snap.FeatureHint ?? "");
            AddRow(section, "Pista de layout", snap.LayoutHint);
            AddIfPresent(section, "Usages notáveis", snap.NotableUsages ?? "");
            AddIfPresent(section, "Produto", snap.Product ?? "");
            AddIfPresent(section, "Fabricante HID", snap.Manufacturer ?? "");
            AddIfPresent(section, "Serial", snap.Serial ?? "");
            AddIfPresent(section, "Consulta direta", snap.DirectQuery ?? "");
            AddRow(section, "Caminho", snap.Path);
            sections.Add(section);
        }

        return sections;
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

    internal static void AddRow(DeviceInfoSection section, string label, string value) =>
        section.Rows.Add(new DeviceInfoRow { Label = label, Value = value });

    internal static void AddIfPresent(DeviceInfoSection section, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            AddRow(section, label, value);
    }
}
