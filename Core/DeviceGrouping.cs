using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace KeyNexus.Core;

/// <summary>
/// Agrupa múltiplas coleções HID (Col01, Col02, MI_0X) do mesmo teclado físico.
/// </summary>
public static class DeviceGrouping
{
    private static readonly Regex ColSuffixRegex = new(@"&Col\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MiSuffixRegex = new(@"&MI_\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool ShouldIgnore(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return true;

        string upper = rawPath.ToUpperInvariant();
        return upper.Contains("RDP")
            || upper.Contains(@"MICROSOFT KEYBOARD RID")
            || upper.Contains(@"CONVERTEDDEVICE");
    }

    /// <summary>
    /// Chave estável por teclado físico: VID+PID (coleções NKRO compartilham o mesmo par).
    /// </summary>
    public static string GetGroupKey(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return string.Empty;

        if (ShouldIgnore(rawPath))
            return rawPath;

        string normalized = rawPath.ToUpperInvariant();

        int hidIdx = normalized.IndexOf("HID#", StringComparison.Ordinal);
        if (hidIdx >= 0)
        {
            int start = hidIdx + 4;
            int end = normalized.IndexOf('#', start);
            string idSegment = end > start
                ? normalized[start..end]
                : normalized[start..];

            idSegment = ColSuffixRegex.Replace(idSegment, "");
            idSegment = MiSuffixRegex.Replace(idSegment, "");
            idSegment = idSegment.TrimEnd('&');

            if (!string.IsNullOrEmpty(idSegment))
                return $"HID#{idSegment}";
        }

        if (normalized.Contains("ACPI"))
        {
            int acpiStart = normalized.IndexOf("ACPI", StringComparison.Ordinal);
            int end = normalized.IndexOf('#', acpiStart + 4);
            return end > acpiStart
                ? normalized[acpiStart..end]
                : normalized[acpiStart..Math.Min(acpiStart + 40, normalized.Length)];
        }

        normalized = ColSuffixRegex.Replace(normalized, "");
        normalized = MiSuffixRegex.Replace(normalized, "");
        return normalized;
    }

    public static List<KeyboardGroup> GroupDevices(IEnumerable<string> rawPaths)
    {
        var groups = new Dictionary<string, KeyboardGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in rawPaths)
        {
            if (ShouldIgnore(path))
                continue;

            string key = GetGroupKey(path);
            if (string.IsNullOrEmpty(key))
                continue;

            if (!groups.TryGetValue(key, out var group))
            {
                group = new KeyboardGroup
                {
                    GroupKey = key,
                    RepresentativePath = path,
                    RawPaths = new List<string> { path }
                };
                groups[key] = group;
            }
            else
            {
                group.RawPaths.Add(path);
            }
        }

        return groups.Values.OrderBy(g => g.RepresentativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public class KeyboardGroup
{
    public string GroupKey { get; set; } = string.Empty;
    public string RepresentativePath { get; set; } = string.Empty;
    public List<string> RawPaths { get; set; } = new();

    public int CollectionCount => RawPaths.Count;
}
