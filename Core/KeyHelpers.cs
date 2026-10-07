using System;
using System.Collections.Generic;
using System.Linq;

namespace KeyNexus.Core;

public static class ModifierFlags
{
    public const int Ctrl = 1;
    public const int Shift = 2;
    public const int Alt = 4;
    /// <summary>AltGr — bit separado; não confundir com Ctrl+Alt.</summary>
    public const int AltGr = 8;
    /// <summary>Tecla Windows. Regras não usam, mas com ela pressionada nenhuma regra dispara.</summary>
    public const int Win = 16;

    public static bool IsAltGr(int mods) => (mods & AltGr) != 0;

    /// <summary>Converte regras antigas que gravaram AltGr como Ctrl|Alt (5).</summary>
    public static int Normalize(int mods)
    {
        if ((mods & (Ctrl | Alt)) == (Ctrl | Alt))
            return (mods & ~(Ctrl | Alt)) | AltGr;
        return mods;
    }

    public static IReadOnlyList<string> ToParts(int mods)
    {
        mods = Normalize(mods);
        var parts = new List<string>(4);
        if ((mods & Win) != 0) parts.Add("Win");
        if ((mods & Ctrl) != 0) parts.Add("Ctrl");
        if ((mods & Alt) != 0) parts.Add("Alt");
        if (IsAltGr(mods)) parts.Add("AltGr");
        if ((mods & Shift) != 0) parts.Add("Shift");
        return parts;
    }

    public static string ToDisplayString(int mods) => string.Join("+", ToParts(mods));
}

public class KeyOption
{
    public int Vk { get; }
    public int Modifiers { get; }
    public string Name { get; }

    public KeyOption(int vk, string name, int modifiers = 0)
    {
        Vk = vk;
        Name = name;
        Modifiers = modifiers;
    }

    public override string ToString() => Name;
}

public static class VkHelper
{
    private static readonly Dictionary<int, string> VkNames = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x1B] = "Esc",
        [0x20] = "Space", [0x25] = "Left", [0x26] = "Up", [0x27] = "Right", [0x28] = "Down",
        [0x2D] = "Insert", [0x2E] = "Delete", [0x30] = "0", [0x31] = "1", [0x32] = "2",
        [0x33] = "3", [0x34] = "4", [0x35] = "5", [0x36] = "6", [0x37] = "7",
        [0x38] = "8", [0x39] = "9", [0x41] = "A", [0x42] = "B", [0x43] = "C",
        [0x44] = "D", [0x45] = "E", [0x46] = "F", [0x47] = "G", [0x48] = "H",
        [0x49] = "I", [0x4A] = "J", [0x4B] = "K", [0x4C] = "L", [0x4D] = "M",
        [0x4E] = "N", [0x4F] = "O", [0x50] = "P", [0x51] = "Q", [0x52] = "R",
        [0x53] = "S", [0x54] = "T", [0x55] = "U", [0x56] = "V", [0x57] = "W",
        [0x58] = "X", [0x59] = "Y", [0x5A] = "Z",
        [0x70] = "F1", [0x71] = "F2", [0x72] = "F3", [0x73] = "F4",
        [0x74] = "F5", [0x75] = "F6", [0x76] = "F7", [0x77] = "F8",
        [0x78] = "F9", [0x79] = "F10", [0x7A] = "F11", [0x7B] = "F12",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-",
        [0xBE] = ".", [0xBF] = "/", [0xC0] = "`",
        [0xC1] = "ABNT_C1", [0xC2] = "ABNT_C2",
        [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]", [0xDE] = "'",
        [0xDF] = "OEM_8", [0xE2] = "OEM_102",
    };

    public static string GetKeyName(int vk) =>
        VkNames.TryGetValue(vk, out var name) ? name : $"VK_{vk:X}";

    /// <summary>
    /// Lista todas as teclas conhecidas (vk + nome) para seleção em ComboBox.
    /// </summary>
    public static IReadOnlyList<KeyOption> GetAllKeys()
    {
        var list = new List<KeyOption>(VkNames.Count);
        foreach (var kvp in VkNames)
            list.Add(new KeyOption(kvp.Key, kvp.Value));
        return list
            .OrderBy(k => CategoryOrder(k.Vk))
            .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int CategoryOrder(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return 0; // letras
        if (vk >= 0x30 && vk <= 0x39) return 1; // números
        if (vk >= 0x70 && vk <= 0x7B) return 2; // F1-F12
        return 3;                               // especiais
    }

    public static string FormatTrigger(int vk, int mods)
    {
        string modStr = ModifierFlags.ToDisplayString(mods);
        string keyStr = GetKeyName(vk);
        return string.IsNullOrEmpty(modStr) ? keyStr : $"{modStr}+{keyStr}";
    }
}
