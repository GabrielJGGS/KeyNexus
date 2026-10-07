using System;
using KeyNexus.Core.Profiles;

namespace KeyNexus.Core.Input;

/// <summary>Uma tecla física a injetar: scan code (preferido) ou, sem scan code, a tecla virtual.</summary>
internal readonly record struct KeyStroke(ushort Vk, ushort Scan, bool Extended)
{
    public bool IsEmpty => Vk == 0 && Scan == 0;

    public static readonly KeyStroke LShift = new(NativeMethods.VK_LSHIFT, 0x2A, false);
    public static readonly KeyStroke RShift = new(NativeMethods.VK_RSHIFT, 0x36, false);
    public static readonly KeyStroke LCtrl = new(NativeMethods.VK_LCONTROL, 0x1D, false);
    public static readonly KeyStroke RCtrl = new(NativeMethods.VK_RCONTROL, 0x1D, true);
    public static readonly KeyStroke LAlt = new(NativeMethods.VK_LMENU, 0x38, false);
    public static readonly KeyStroke RAlt = new(NativeMethods.VK_RMENU, 0x38, true);
    public static readonly KeyStroke LWin = new(NativeMethods.VK_LWIN, 0x5B, true);
    public static readonly KeyStroke RWin = new(NativeMethods.VK_RWIN, 0x5C, true);

    /// <summary>Tecla virtual não atribuída: quebra o "Alt/Win tocado sozinho" que abre menu ou Iniciar.</summary>
    public static readonly KeyStroke MenuMask = new(0xE8, 0, false);

    public static KeyStroke ForModifier(PhysicalModifiers bit) => bit switch
    {
        PhysicalModifiers.LShift => LShift,
        PhysicalModifiers.RShift => RShift,
        PhysicalModifiers.LCtrl or PhysicalModifiers.AltGrCtrl => LCtrl,
        PhysicalModifiers.RCtrl => RCtrl,
        PhysicalModifiers.LAlt => LAlt,
        PhysicalModifiers.RAlt => RAlt,
        PhysicalModifiers.LWin => LWin,
        PhysicalModifiers.RWin => RWin,
        _ => default,
    };

    /// <summary>Scan code da tecla virtual no layout indicado (inclui o prefixo E0 das teclas estendidas).</summary>
    public static KeyStroke ForVk(int vk, IntPtr hkl)
    {
        uint scanEx = NativeMethods.MapVirtualKeyEx((uint)vk, NativeMethods.MAPVK_VK_TO_VSC_EX, hkl);
        ushort scan = (ushort)(scanEx & 0xFF);
        if (scan == 0)
            return new KeyStroke((ushort)vk, 0, IsExtendedNavigationVk(vk));

        uint prefix = scanEx & 0xFF00;
        return new KeyStroke((ushort)vk, scan, prefix is 0xE000 or 0xE100);
    }

    private static bool IsExtendedNavigationVk(int vk) =>
        vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5;
}

/// <summary>
/// Monta, sem alocar, o lote único de SendInput de uma regra:
/// solta os modificadores presos, envia a saída e devolve exatamente o que estava preso.
/// </summary>
internal static class InputBatchBuilder
{
    public static readonly IntPtr Signature = new(0x4B4E584E);

    private static readonly (PhysicalModifiers Bit, KeyStroke Key)[] ReleaseOrder =
    {
        (PhysicalModifiers.RWin, KeyStroke.RWin),
        (PhysicalModifiers.LWin, KeyStroke.LWin),
        (PhysicalModifiers.RAlt, KeyStroke.RAlt),
        (PhysicalModifiers.LAlt, KeyStroke.LAlt),
        (PhysicalModifiers.AltGrCtrl, KeyStroke.LCtrl),
        (PhysicalModifiers.LCtrl, KeyStroke.LCtrl),
        (PhysicalModifiers.RCtrl, KeyStroke.RCtrl),
        (PhysicalModifiers.RShift, KeyStroke.RShift),
        (PhysicalModifiers.LShift, KeyStroke.LShift),
    };

    /// <summary>Máximo de eventos para soltar (com máscara) e devolver todos os modificadores.</summary>
    public const int ModifierEventsBudget = 2 + 9 * 2;

    public static int RequiredCapacity(CompiledAction action) =>
        action.MaxInputs + ModifierEventsBudget + 16;

    /// <summary>
    /// Máscara necessária quando um Alt sai sem Ctrl junto (barra de menu) ou quando sai um Win (Iniciar).
    /// Com AltGr o Ctrl falso está preso, então não precisa.
    /// </summary>
    public static bool NeedsMenuMask(PhysicalModifiers held) =>
        (held & PhysicalModifiers.AnyWin) != 0
        || ((held & PhysicalModifiers.AnyAlt) != 0 && (held & PhysicalModifiers.AnyCtrl) == 0);

    public static int ReleaseHeld(NativeMethods.INPUT[] buf, int n, PhysicalModifiers held)
    {
        if (held == PhysicalModifiers.None)
            return n;

        if (NeedsMenuMask(held))
            n = AppendPress(buf, n, KeyStroke.MenuMask);

        foreach (var (bit, key) in ReleaseOrder)
        {
            if ((held & bit) == 0 || IsDuplicateCtrl(bit, held))
                continue;
            n = AppendKey(buf, n, key, up: true);
        }
        return n;
    }

    public static int RestoreHeld(NativeMethods.INPUT[] buf, int n, PhysicalModifiers held)
    {
        if (held == PhysicalModifiers.None)
            return n;

        for (int i = ReleaseOrder.Length - 1; i >= 0; i--)
        {
            var (bit, key) = ReleaseOrder[i];

            // O Ctrl do AltGr não volta à mão: quando o Alt direito desce com o Ctrl solto, o Windows cria o
            // Ctrl falso e o solta junto com o AltGr físico. Se o Ctrl descer antes, o Windows nunca o solta.
            if ((held & bit) == 0 || bit == PhysicalModifiers.AltGrCtrl)
                continue;
            n = AppendKey(buf, n, key, up: false);
        }
        return n;
    }

    /// <summary>Solta, envia a ação e devolve os modificadores, tudo no mesmo lote.</summary>
    public static int BuildRemap(NativeMethods.INPUT[] buf, int n, PhysicalModifiers held, CompiledAction action, IntPtr hkl)
    {
        n = ReleaseHeld(buf, n, held);
        n = AppendAction(buf, n, action, hkl);
        return RestoreHeld(buf, n, held);
    }

    public static int AppendAction(NativeMethods.INPUT[] buf, int n, CompiledAction action, IntPtr hkl)
    {
        switch (action.Kind)
        {
            case CompiledActionKind.Key:
                var key = action.BoundKey.IsEmpty ? KeyStroke.ForVk(action.OutputVk, hkl) : action.BoundKey;
                return AppendKeyPress(buf, n, key, action.OutputModifiers);

            case CompiledActionKind.Text:
                return AppendText(buf, n, action.Text);

            case CompiledActionKind.Macro:
                foreach (var step in action.Steps)
                    n = AppendStep(buf, n, step, hkl);
                return n;
        }
        return n;
    }

    public static int AppendStep(NativeMethods.INPUT[] buf, int n, CompiledMacroStep step, IntPtr hkl)
    {
        if (!string.IsNullOrEmpty(step.Text))
            return AppendText(buf, n, step.Text);

        if (step.Vk <= 0)
            return n;

        var key = step.BoundKey.IsEmpty ? KeyStroke.ForVk(step.Vk, hkl) : step.BoundKey;
        return AppendKeyPress(buf, n, key, step.Modifiers);
    }

    public static int AppendKeyPress(NativeMethods.INPUT[] buf, int n, KeyStroke key, int modifiers)
    {
        n = AppendOutputModifiers(buf, n, modifiers, up: false);
        n = AppendPress(buf, n, key);
        return AppendOutputModifiers(buf, n, modifiers, up: true);
    }

    public static int AppendOutputModifiers(NativeMethods.INPUT[] buf, int n, int modifiers, bool up)
    {
        modifiers = ModifierFlags.Normalize(modifiers);
        if (modifiers == 0)
            return n;

        Span<KeyStroke> keys = stackalloc KeyStroke[5];
        int count = 0;
        if (ModifierFlags.IsAltGr(modifiers))
        {
            keys[count++] = KeyStroke.LCtrl;
            keys[count++] = KeyStroke.RAlt;
        }
        else
        {
            if ((modifiers & ModifierFlags.Ctrl) != 0) keys[count++] = KeyStroke.LCtrl;
            if ((modifiers & ModifierFlags.Alt) != 0) keys[count++] = KeyStroke.LAlt;
        }
        if ((modifiers & ModifierFlags.Shift) != 0) keys[count++] = KeyStroke.LShift;
        if ((modifiers & ModifierFlags.Win) != 0) keys[count++] = KeyStroke.LWin;

        if (!up)
        {
            for (int i = 0; i < count; i++)
                n = AppendKey(buf, n, keys[i], up: false);
        }
        else
        {
            for (int i = count - 1; i >= 0; i--)
                n = AppendKey(buf, n, keys[i], up: true);
        }
        return n;
    }

    public static int AppendPress(NativeMethods.INPUT[] buf, int n, KeyStroke key)
    {
        n = AppendKey(buf, n, key, up: false);
        return AppendKey(buf, n, key, up: true);
    }

    public static int AppendKey(NativeMethods.INPUT[] buf, int n, KeyStroke key, bool up)
    {
        uint flags = up ? NativeMethods.KEYEVENTF_KEYUP : 0;
        if (key.Extended)
            flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;

        ushort vk = key.Vk;
        if (key.Scan != 0)
        {
            flags |= NativeMethods.KEYEVENTF_SCANCODE;
            vk = 0;
        }

        buf[n].type = NativeMethods.INPUT_KEYBOARD;
        buf[n].ki = new NativeMethods.KEYBDINPUT
        {
            wVk = vk,
            wScan = key.Scan,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = Signature
        };
        return n + 1;
    }

    public static int AppendText(NativeMethods.INPUT[] buf, int n, string text)
    {
        foreach (char c in text)
        {
            n = AppendUnicode(buf, n, c, up: false);
            n = AppendUnicode(buf, n, c, up: true);
        }
        return n;
    }

    private static int AppendUnicode(NativeMethods.INPUT[] buf, int n, char c, bool up)
    {
        buf[n].type = NativeMethods.INPUT_KEYBOARD;
        buf[n].ki = new NativeMethods.KEYBDINPUT
        {
            wVk = 0,
            wScan = c,
            dwFlags = NativeMethods.KEYEVENTF_UNICODE | (up ? NativeMethods.KEYEVENTF_KEYUP : 0),
            time = 0,
            dwExtraInfo = Signature
        };
        return n + 1;
    }

    private static bool IsDuplicateCtrl(PhysicalModifiers bit, PhysicalModifiers held) =>
        bit == PhysicalModifiers.AltGrCtrl && (held & PhysicalModifiers.LCtrl) != 0;
}
