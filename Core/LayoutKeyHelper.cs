using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using KeyNexus.Core.Profiles;

namespace KeyNexus.Core;

/// <summary>
/// Resolve nomes de teclas conforme o layout de teclado (HKL) vinculado ao dispositivo.
/// </summary>
public static class LayoutKeyHelper
{
    /// <summary>ToUnicodeEx sem alterar o estado do teclado (não "consome" tecla morta). Windows 10 1607+.</summary>
    private const uint ToUnicodeDoNotChangeState = 0x4;

    private static readonly int[] KnownVks =
    {
        0x08, 0x09, 0x0D, 0x1B, 0x20, 0x21, 0x22, 0x23, 0x24,
        0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E,
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39,
        0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49, 0x4A, 0x4B,
        0x4C, 0x4D, 0x4E, 0x4F, 0x50, 0x51, 0x52, 0x53, 0x54, 0x55, 0x56,
        0x57, 0x58, 0x59, 0x5A,
        0x60, 0x61, 0x62, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F,
        0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x7B,
        0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0,
        0xC1, 0xC2, // ABNT brasileiro (/ ? ° e tecla extra)
        0xDB, 0xDC, 0xDD, 0xDE, 0xDF, 0xE2,
    };

    private static readonly int[] LabelModifierSets =
    {
        0,
        ModifierFlags.Shift,
        ModifierFlags.AltGr,
    };

    private static readonly ConcurrentDictionary<IntPtr, IReadOnlyList<KeyOption>> KeysByLayout = new();
    private static readonly ConcurrentDictionary<IntPtr, Dictionary<string, KeyOption>> LabelsByLayout = new();

    public static IntPtr ParseHkl(string? hklHex)
    {
        if (string.IsNullOrWhiteSpace(hklHex))
            return IntPtr.Zero;

        try
        {
            uint value = Convert.ToUInt32(hklHex, 16);
            return new IntPtr(unchecked((int)value));
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    public static IntPtr ResolveHkl(string? layoutHklHex)
    {
        IntPtr hkl = ParseHkl(layoutHklHex);
        if (hkl != IntPtr.Zero)
            return hkl;

        return NativeMethods.GetKeyboardLayout(0);
    }

    public static void TryActivateLayout(Window window, string? layoutHklHex)
    {
        IntPtr hkl = ParseHkl(layoutHklHex);
        if (hkl == IntPtr.Zero)
            return;

        try
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle == IntPtr.Zero)
                return;

            NativeMethods.ActivateKeyboardLayout(hkl, NativeMethods.KLF_SETFORPROCESS);
            NativeMethods.PostMessage(helper.Handle, NativeMethods.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, hkl);
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao ativar layout no editor", ex);
        }
    }

    public static int VkFromScanCode(uint scanCode, bool extended, string? layoutHklHex) =>
        ScanCodeTable.Lookup(ScanCodeTable.For(ResolveHkl(layoutHklHex)), scanCode, extended);

    /// <summary>
    /// Com AltGr pressionado o Windows reporta VK errado; usa scan code físico da tecla.
    /// </summary>
    public static int ResolvePhysicalVk(uint scanCode, bool extended, int fallbackVk, string? layoutHklHex)
    {
        int vk = VkFromScanCode(scanCode, extended, layoutHklHex);
        if (vk != 0)
            return vk;

        // AltGr costuma reportar VK de numpad (0x6E etc.) — não confiar no fallback
        if (IsLikelyAltGrGhostVk(fallbackVk))
            return 0;

        return fallbackVk;
    }

    private static bool IsLikelyAltGrGhostVk(int vk) =>
        vk is >= 0x60 and <= 0x6F; // numpad

    public static string GetKeyName(int vk, string? layoutHklHex, int modifiers = 0)
    {
        modifiers = ModifierFlags.Normalize(modifiers);
        IntPtr hkl = ResolveHkl(layoutHklHex);

        if (IsLayoutInvariantKey(vk))
            return VkHelper.GetKeyName(vk);

        string? label = TryGetLayoutLabel(vk, modifiers, hkl);
        if (!string.IsNullOrEmpty(label))
            return label;

        return VkHelper.GetKeyName(vk);
    }

    public static string FormatTrigger(int vk, int modifiers, string? layoutHklHex)
    {
        modifiers = ModifierFlags.Normalize(modifiers);
        IntPtr hkl = ResolveHkl(layoutHklHex);
        string baseLabel = TryGetLayoutLabel(vk, 0, hkl) ?? VkHelper.GetKeyName(vk);

        if (modifiers == 0)
            return GetKeyName(vk, layoutHklHex, 0);

        string modLabel = GetKeyName(vk, layoutHklHex, modifiers);
        string display = BuildDisplayName(baseLabel, modLabel, modifiers);
        if (modifiers == ModifierFlags.Shift || ModifierFlags.IsAltGr(modifiers))
            return display;

        string modStr = ModifierFlags.ToDisplayString(modifiers);
        return string.IsNullOrEmpty(modStr) ? display : $"{modStr}+{baseLabel}";
    }

    /// <summary>Partes para desenhar como teclas: modificadores + tecla física (ex.: AltGr, .).</summary>
    public static IReadOnlyList<string> GetTriggerParts(int vk, int modifiers, string? layoutHklHex)
    {
        var parts = new List<string>(ModifierFlags.ToParts(modifiers));
        parts.Add(BaseLabel(vk, ResolveHkl(layoutHklHex)));
        return parts;
    }

    /// <summary>
    /// Partes da saída: quando Shift/AltGr só servem para gerar um caractere, mostra o caractere (ex.: ?).
    /// </summary>
    public static IReadOnlyList<string> GetOutputParts(int vk, int modifiers, string? layoutHklHex)
    {
        modifiers = ModifierFlags.Normalize(modifiers);
        bool charModifiers = modifiers is ModifierFlags.Shift or ModifierFlags.AltGr
            or (ModifierFlags.Shift | ModifierFlags.AltGr);

        if (charModifiers && !IsLayoutInvariantKey(vk)
            && TryGetLayoutLabel(vk, modifiers, ResolveHkl(layoutHklHex)) is { Length: 1 } character)
            return new[] { character };

        return GetTriggerParts(vk, modifiers, layoutHklHex);
    }

    /// <summary>Acha a tecla (e os modificadores) que gera o caractere no layout.</summary>
    public static bool TryFindKeyForCharacter(char character, string? layoutHklHex, out int vk, out int modifiers)
    {
        vk = 0;
        modifiers = 0;

        short result = NativeMethods.VkKeyScanEx(character, ResolveHkl(layoutHklHex));
        if (result == -1)
            return false;

        vk = result & 0xFF;
        int shiftState = (result >> 8) & 0xFF;
        if ((shiftState & 0x38) != 0)
            return false;

        if ((shiftState & 0x6) == 0x6)
            modifiers |= ModifierFlags.AltGr;
        else
        {
            if ((shiftState & 0x2) != 0) modifiers |= ModifierFlags.Ctrl;
            if ((shiftState & 0x4) != 0) modifiers |= ModifierFlags.Alt;
        }
        if ((shiftState & 0x1) != 0)
            modifiers |= ModifierFlags.Shift;

        return vk != 0;
    }

    public static IReadOnlyList<KeyOption> GetAllKeys(string? layoutHklHex) =>
        KeysByLayout.GetOrAdd(ResolveHkl(layoutHklHex), BuildAllKeys);

    private static IReadOnlyList<KeyOption> BuildAllKeys(IntPtr hkl)
    {
        var options = new List<KeyOption>();
        var usedLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (int vk in KnownVks)
        {
            if (IsLayoutInvariantKey(vk))
            {
                string name = VkHelper.GetKeyName(vk);
                if (usedLabels.Add(name))
                    options.Add(new KeyOption(vk, name));
                continue;
            }

            bool added = false;
            string baseLabel = TryGetLayoutLabel(vk, 0, hkl) ?? VkHelper.GetKeyName(vk);

            foreach (int mods in LabelModifierSets)
            {
                string? label = TryGetLayoutLabel(vk, mods, hkl);
                if (string.IsNullOrWhiteSpace(label))
                    continue;

                string display = BuildDisplayName(baseLabel, label, mods);
                if (!usedLabels.Add(display))
                {
                    // Se o caractere colide (ex.: duas teclas geram "?"), mantém nome físico
                    string physical = mods switch
                    {
                        0 => baseLabel,
                        ModifierFlags.Shift => $"Shift+{baseLabel}",
                        ModifierFlags.AltGr => $"AltGr+{baseLabel}",
                        _ => $"{ModifierFlags.ToDisplayString(mods)}+{baseLabel}"
                    };
                    if (!usedLabels.Add(physical))
                        continue;
                    display = physical;
                }

                options.Add(new KeyOption(vk, display, mods));
                added = true;
            }

            if (!added)
            {
                string fallback = VkHelper.GetKeyName(vk);
                if (usedLabels.Add(fallback))
                    options.Add(new KeyOption(vk, fallback));
            }
        }

        return options
            .OrderBy(k => CategoryOrder(k.Vk))
            .ThenBy(k => k.Modifiers)
            .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildDisplayName(string baseLabel, string modLabel, int mods)
    {
        if (mods == 0)
            return baseLabel;

        // Mostra o caractere gerado: ? ! ° @ # etc.
        if (!string.IsNullOrEmpty(modLabel) && !string.Equals(modLabel, baseLabel, StringComparison.Ordinal))
            return modLabel;

        return mods switch
        {
            ModifierFlags.Shift => $"Shift+{baseLabel}",
            ModifierFlags.AltGr => $"AltGr+{baseLabel}",
            _ => $"{ModifierFlags.ToDisplayString(mods)}+{baseLabel}"
        };
    }

    private static string BaseLabel(int vk, IntPtr hkl) =>
        IsLayoutInvariantKey(vk)
            ? VkHelper.GetKeyName(vk)
            : TryGetLayoutLabel(vk, 0, hkl) ?? VkHelper.GetKeyName(vk);

    private static int CategoryOrder(int vk)
    {
        if (vk >= 0x41 && vk <= 0x5A) return 0;
        if (vk >= 0x30 && vk <= 0x39) return 1;
        if (vk >= 0x70 && vk <= 0x7B) return 2;
        return 3;
    }

    private static bool IsLayoutInvariantKey(int vk) =>
        vk is >= 0x70 and <= 0x7B // F1-F12
            or 0x25 or 0x26 or 0x27 or 0x28 // arrows
            or 0x2D or 0x2E // insert/delete
            or 0x08 or 0x09 or 0x0D or 0x1B or 0x20; // backspace, tab, enter, esc, space

    private static string? TryGetLayoutLabel(int vk, int modifiers, IntPtr hkl)
    {
        byte[] keyState = new byte[256];
        if (!NativeMethods.GetKeyboardState(keyState))
            Array.Clear(keyState, 0, keyState.Length);

        keyState[NativeMethods.VK_SHIFT] = 0;
        keyState[NativeMethods.VK_CONTROL] = 0;
        keyState[NativeMethods.VK_MENU] = 0;
        keyState[NativeMethods.VK_LSHIFT] = 0;
        keyState[NativeMethods.VK_RSHIFT] = 0;
        keyState[NativeMethods.VK_LCONTROL] = 0;
        keyState[NativeMethods.VK_RCONTROL] = 0;
        keyState[NativeMethods.VK_LMENU] = 0;
        keyState[NativeMethods.VK_RMENU] = 0;

        if ((modifiers & ModifierFlags.Shift) != 0)
            keyState[NativeMethods.VK_SHIFT] = 0x80;

        if (ModifierFlags.IsAltGr(modifiers))
        {
            keyState[NativeMethods.VK_CONTROL] = 0x80;
            keyState[NativeMethods.VK_MENU] = 0x80;
        }
        else
        {
            if ((modifiers & ModifierFlags.Ctrl) != 0)
                keyState[NativeMethods.VK_CONTROL] = 0x80;
            if ((modifiers & ModifierFlags.Alt) != 0)
                keyState[NativeMethods.VK_MENU] = 0x80;
        }

        uint scan = NativeMethods.MapVirtualKeyEx((uint)vk, NativeMethods.MAPVK_VK_TO_VSC, hkl);
        if (scan == 0)
            return null;

        var buffer = new StringBuilder(8);
        int result = NativeMethods.ToUnicodeEx((uint)vk, scan, keyState, buffer, buffer.Capacity, ToUnicodeDoNotChangeState, hkl);

        // result < 0: tecla morta (´ ~ ^); com a flag 0x4 o caractere vem no buffer sem ficar pendente.
        if ((result == 1 || result < 0) && buffer.Length > 0 && !char.IsControl(buffer[0]))
            return FormatCharLabel(buffer[0]);

        return null;
    }

    private static string FormatCharLabel(char c) =>
        char.IsLetter(c) ? char.ToUpperInvariant(c).ToString() : c.ToString();

    public static KeyOption? FindByLabel(string label, string? layoutHklHex)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;

        IntPtr hkl = ResolveHkl(layoutHklHex);
        var index = LabelsByLayout.GetOrAdd(hkl, _ => BuildLabelIndex(layoutHklHex));
        return index.TryGetValue(label.Trim(), out var key) ? key : null;
    }

    private static Dictionary<string, KeyOption> BuildLabelIndex(string? layoutHklHex)
    {
        var index = new Dictionary<string, KeyOption>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in GetAllKeys(layoutHklHex))
        {
            index.TryAdd(key.Name, key);
            index.TryAdd(GetKeyName(key.Vk, layoutHklHex, key.Modifiers), key);
        }
        return index;
    }

    public static KeyOption? FindByVk(int vk, int modifiers, string? layoutHklHex)
    {
        modifiers = ModifierFlags.Normalize(modifiers);
        var keys = GetAllKeys(layoutHklHex);
        return keys.FirstOrDefault(k => k.Vk == vk && k.Modifiers == modifiers)
            ?? keys.FirstOrDefault(k => k.Vk == vk && k.Modifiers == 0);
    }

    public static int FindVkByLabel(string label, string? layoutHklHex) =>
        FindByLabel(label, layoutHklHex)?.Vk ?? 0;

    /// <summary>
    /// Resolve o caractere que uma tecla produz no layout (ex.: VK C2 → '/').
    /// </summary>
    public static bool TryGetOutputCharacter(int vk, int modifiers, string? layoutHklHex, out char character)
    {
        character = default;
        modifiers = ModifierFlags.Normalize(modifiers);

        if (IsLayoutInvariantKey(vk))
            return false;

        string? label = TryGetLayoutLabel(vk, modifiers, ResolveHkl(layoutHklHex));
        if (string.IsNullOrEmpty(label) || label.Length != 1)
            return false;

        char c = label[0];
        if (char.IsControl(c))
            return false;

        character = c;
        return true;
    }
}
