using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.Win32;

namespace KeyNexus.Core;

public sealed class KeyboardLayoutInfo
{
    /// <summary>HKL em hexadecimal, ex.: F0010416.</summary>
    public string Hkl { get; init; } = string.Empty;
    /// <summary>Identificador do layout no registro, ex.: 00020409.</summary>
    public string Klid { get; init; } = string.Empty;
    /// <summary>Nome como aparece nas Configurações do Windows, ex.: Estados Unidos (internacional).</summary>
    public string LayoutName { get; init; } = string.Empty;
    /// <summary>Idioma de entrada, ex.: Português (Brasil).</summary>
    public string LanguageName { get; init; } = string.Empty;
    public bool IsInstalled { get; init; }

    public string TechnicalInfo => $"HKL {Hkl} · KLID {Klid}";
}

/// <summary>
/// Nomes reais dos layouts, lidos do Windows: HKL → KLID (inclusive pelo "Layout Id"),
/// "Layout Display Name" localizado e idioma de entrada.
/// </summary>
public static class KeyboardLayoutCatalog
{
    private const string LayoutsKey = @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts";

    private static readonly ConcurrentDictionary<uint, KeyboardLayoutInfo> Cache = new();
    private static readonly object LayoutIdLock = new();
    private static Dictionary<int, string>? _klidByLayoutId;

    public static string FormatHkl(long hkl) => (hkl & 0xFFFFFFFF).ToString("X8");

    public static IReadOnlyList<KeyboardLayoutInfo> GetInstalledLayouts()
    {
        var result = new List<KeyboardLayoutInfo>();
        int count = NativeMethods.GetKeyboardLayoutList(0, null!);
        if (count <= 0)
            return result;

        var layouts = new IntPtr[count];
        count = NativeMethods.GetKeyboardLayoutList(count, layouts);
        for (int i = 0; i < count; i++)
            result.Add(Describe((uint)layouts[i].ToInt64(), installed: true));

        return result;
    }

    public static KeyboardLayoutInfo Describe(string? hklHex)
    {
        if (string.IsNullOrWhiteSpace(hklHex) || !uint.TryParse(hklHex, NumberStyles.HexNumber, null, out uint hkl))
        {
            return new KeyboardLayoutInfo
            {
                Hkl = hklHex ?? string.Empty,
                LayoutName = hklHex ?? "Desconhecido",
                LanguageName = string.Empty
            };
        }

        return Describe(hkl, IsInstalled(hkl));
    }

    public static KeyboardLayoutInfo Describe(uint hkl, bool installed)
    {
        var info = Cache.GetOrAdd(hkl, Build);
        return info.IsInstalled == installed
            ? info
            : new KeyboardLayoutInfo
            {
                Hkl = info.Hkl,
                Klid = info.Klid,
                LayoutName = info.LayoutName,
                LanguageName = info.LanguageName,
                IsInstalled = installed
            };
    }

    public static string HklToKlid(uint hkl) => HklToKlid(hkl, FindKlidByLayoutId);

    /// <summary>
    /// Palavra baixa = idioma. Palavra alta: 0xFxxx aponta para o valor "Layout Id" de uma variante,
    /// 0xExxx é IME, e o resto é o próprio KLID (ou o idioma, quando igual a ele).
    /// </summary>
    internal static string HklToKlid(uint hkl, Func<int, string?> findKlidByLayoutId)
    {
        ushort lang = (ushort)(hkl & 0xFFFF);
        ushort device = (ushort)(hkl >> 16);

        if ((device & 0xF000) == 0xF000)
            return findKlidByLayoutId(device & 0x0FFF) ?? lang.ToString("X8");

        if ((device & 0xF000) == 0xE000)
            return hkl.ToString("X8");

        return (device == 0 || device == lang ? lang : device).ToString("X8");
    }

    private static KeyboardLayoutInfo Build(uint hkl)
    {
        string hex = hkl.ToString("X8");
        string klid = HklToKlid(hkl);
        string layoutName = ReadLayoutName(klid) ?? hex;
        string language = GetLanguageName((ushort)(hkl & 0xFFFF));

        return new KeyboardLayoutInfo
        {
            Hkl = hex,
            Klid = klid,
            LayoutName = layoutName,
            LanguageName = language,
            IsInstalled = true
        };
    }

    private static bool IsInstalled(uint hkl)
    {
        int count = NativeMethods.GetKeyboardLayoutList(0, null!);
        if (count <= 0)
            return false;

        var layouts = new IntPtr[count];
        count = NativeMethods.GetKeyboardLayoutList(count, layouts);
        for (int i = 0; i < count; i++)
        {
            if ((uint)layouts[i].ToInt64() == hkl)
                return true;
        }
        return false;
    }

    private static string? FindKlidByLayoutId(int layoutId)
    {
        lock (LayoutIdLock)
        {
            _klidByLayoutId ??= LoadLayoutIds();
            return _klidByLayoutId.TryGetValue(layoutId, out var klid) ? klid : null;
        }
    }

    private static Dictionary<int, string> LoadLayoutIds()
    {
        var map = new Dictionary<int, string>();
        try
        {
            #pragma warning disable CA1416
            using var root = Registry.LocalMachine.OpenSubKey(LayoutsKey, false);
            if (root == null)
                return map;

            foreach (string klid in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(klid, false);
                if (key?.GetValue("Layout Id") is string value
                    && int.TryParse(value, NumberStyles.HexNumber, null, out int id)
                    && !map.ContainsKey(id))
                {
                    map[id] = klid.ToUpperInvariant();
                }
            }
            #pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao ler os Layout Id do registro", ex);
        }
        return map;
    }

    private static string? ReadLayoutName(string klid)
    {
        try
        {
            #pragma warning disable CA1416
            using var key = Registry.LocalMachine.OpenSubKey($@"{LayoutsKey}\{klid}", false);
            if (key == null)
                return null;

            if (key.GetValue("Layout Display Name") is string indirect && indirect.StartsWith('@'))
            {
                var buffer = new StringBuilder(260);
                if (NativeMethods.SHLoadIndirectString(indirect, buffer, buffer.Capacity, IntPtr.Zero) == 0
                    && buffer.Length > 0)
                    return buffer.ToString();
            }

            return key.GetValue("Layout Text") as string;
            #pragma warning restore CA1416
        }
        catch
        {
            return null;
        }
    }

    private static string GetLanguageName(ushort langId)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(langId);
            var buffer = new StringBuilder(128);
            string name = NativeMethods.GetLocaleInfoEx(culture.Name, NativeMethods.LOCALE_SLOCALIZEDDISPLAYNAME, buffer, buffer.Capacity) > 0
                ? buffer.ToString()
                : culture.NativeName;
            return Capitalize(name, culture);
        }
        catch
        {
            return $"Idioma 0x{langId:X4}";
        }
    }

    private static string Capitalize(string text, CultureInfo culture) =>
        string.IsNullOrEmpty(text) ? text : char.ToUpper(text[0], culture) + text[1..];
}
