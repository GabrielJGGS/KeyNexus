using System;
using System.Collections.Generic;
using System.Globalization;

namespace KeyNexus.Core;

/// <summary>
/// Resolve nomes legíveis para Keyboard Layouts (HKL) do Windows
/// usando CultureInfo + Registry como fallback.
/// </summary>
public static class LayoutNameResolver
{
    private static readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public static string GetLayoutName(long hklRaw)
    {
        string hexHkl = (hklRaw & 0xFFFFFFFF).ToString("X8");

        if (_cache.TryGetValue(hexHkl, out var cached))
            return cached;

        string name = Resolve(hklRaw, hexHkl);
        _cache[hexHkl] = name;
        return name;
    }

    public static string GetHexHkl(long hklRaw) => (hklRaw & 0xFFFFFFFF).ToString("X8");

    private static string Resolve(long hklRaw, string hexHkl)
    {
        string cultureName = "";
        string subLayout = "";

        try
        {
            int lcid = (int)(hklRaw & 0xFFFF);
            var culture = CultureInfo.GetCultureInfo(lcid);
            cultureName = culture.NativeName;
        }
        catch
        {
            cultureName = "Desconhecido";
        }

        // Tenta buscar o nome exato no Registry do Windows (funciona apenas no Windows)
        try
        {
            #pragma warning disable CA1416
            // O layout ID completo é usado como chave no Registry
            string? regName = Microsoft.Win32.Registry.GetValue(
                $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{hexHkl}",
                "Layout Text", null) as string;

            if (string.IsNullOrEmpty(regName))
            {
                // Tenta com LCID no formato 00000XXX
                string lcidKey = (hklRaw & 0xFFFF).ToString("X8");
                regName = Microsoft.Win32.Registry.GetValue(
                    $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{lcidKey}",
                    "Layout Text", null) as string;
            }

            if (!string.IsNullOrEmpty(regName))
                subLayout = regName;
            #pragma warning restore CA1416
        }
        catch
        {
            // Silencia caso o Registry não esteja acessível
        }

        if (!string.IsNullOrEmpty(subLayout))
            return $"{cultureName} — {subLayout}";

        return cultureName;
    }
}
