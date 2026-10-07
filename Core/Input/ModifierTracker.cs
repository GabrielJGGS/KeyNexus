using System;

namespace KeyNexus.Core.Input;

[Flags]
internal enum PhysicalModifiers : ushort
{
    None = 0,
    LShift = 1 << 0,
    RShift = 1 << 1,
    LCtrl = 1 << 2,
    RCtrl = 1 << 3,
    LAlt = 1 << 4,
    RAlt = 1 << 5,
    LWin = 1 << 6,
    RWin = 1 << 7,
    /// <summary>Ctrl esquerdo que o Windows gera sozinho junto com o AltGr (scan code 0x21D).</summary>
    AltGrCtrl = 1 << 8,

    AnyCtrl = LCtrl | RCtrl | AltGrCtrl,
    AnyAlt = LAlt | RAlt,
    AnyWin = LWin | RWin,
}

/// <summary>
/// Estado físico dos modificadores, lido só de eventos não injetados.
/// GetAsyncKeyState não serve: ele mistura as teclas que o próprio KeyNexus injeta.
/// </summary>
internal sealed class ModifierTracker
{
    public const uint AltGrFakeCtrlScanCode = 0x21D;

    private static readonly PhysicalModifiers[] AllBits =
    {
        PhysicalModifiers.LShift, PhysicalModifiers.RShift,
        PhysicalModifiers.LCtrl, PhysicalModifiers.RCtrl,
        PhysicalModifiers.LAlt, PhysicalModifiers.RAlt,
        PhysicalModifiers.LWin, PhysicalModifiers.RWin,
        PhysicalModifiers.AltGrCtrl,
    };

    public PhysicalModifiers Held { get; private set; }

    public int CurrentFlags => ToModifierFlags(Held);

    public static bool IsModifierVk(uint vk) =>
        vk is >= 0xA0 and <= 0xA5
            or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN
            or 0x10 or 0x11 or 0x12;

    public static PhysicalModifiers ToModifier(uint vk, uint scanCode) => vk switch
    {
        0xA0 or 0x10 => PhysicalModifiers.LShift,
        0xA1 => PhysicalModifiers.RShift,
        0xA2 or 0x11 => (scanCode & 0xFFF) == AltGrFakeCtrlScanCode
            ? PhysicalModifiers.AltGrCtrl
            : PhysicalModifiers.LCtrl,
        0xA3 => PhysicalModifiers.RCtrl,
        0xA4 or 0x12 => PhysicalModifiers.LAlt,
        0xA5 => PhysicalModifiers.RAlt,
        NativeMethods.VK_LWIN => PhysicalModifiers.LWin,
        NativeMethods.VK_RWIN => PhysicalModifiers.RWin,
        _ => PhysicalModifiers.None,
    };

    /// <summary>Tecla virtual que representa o bit (o Ctrl falso do AltGr é o Ctrl esquerdo).</summary>
    public static int VkOf(PhysicalModifiers bit) => bit switch
    {
        PhysicalModifiers.LShift => NativeMethods.VK_LSHIFT,
        PhysicalModifiers.RShift => NativeMethods.VK_RSHIFT,
        PhysicalModifiers.LCtrl or PhysicalModifiers.AltGrCtrl => NativeMethods.VK_LCONTROL,
        PhysicalModifiers.RCtrl => NativeMethods.VK_RCONTROL,
        PhysicalModifiers.LAlt => NativeMethods.VK_LMENU,
        PhysicalModifiers.RAlt => NativeMethods.VK_RMENU,
        PhysicalModifiers.LWin => NativeMethods.VK_LWIN,
        PhysicalModifiers.RWin => NativeMethods.VK_RWIN,
        _ => 0,
    };

    /// <summary>Junta o Ctrl falso do AltGr com o Ctrl esquerdo (é a mesma tecla lógica).</summary>
    public static PhysicalModifiers FoldCtrl(PhysicalModifiers mods) =>
        (mods & PhysicalModifiers.AltGrCtrl) != 0
            ? (mods & ~PhysicalModifiers.AltGrCtrl) | PhysicalModifiers.LCtrl
            : mods;

    public void Update(uint vk, uint scanCode, bool isUp)
    {
        var bit = ToModifier(vk, scanCode);
        if (bit == PhysicalModifiers.None)
            return;

        if (isUp)
        {
            // Soltar o Ctrl esquerdo solta as duas origens (real e a do AltGr).
            if (bit is PhysicalModifiers.LCtrl or PhysicalModifiers.AltGrCtrl)
                Held &= ~(PhysicalModifiers.LCtrl | PhysicalModifiers.AltGrCtrl);
            else
                Held &= ~bit;
        }
        else
        {
            Held |= bit;
        }
    }

    /// <summary>
    /// Limpa bits cuja soltura o hook não viu (ex.: tela segura do Ctrl+Alt+Del).
    /// </summary>
    public void Resync(Func<int, bool> isLogicallyDown)
    {
        if (Held == PhysicalModifiers.None)
            return;

        foreach (var bit in AllBits)
        {
            if ((Held & bit) != 0 && !isLogicallyDown(VkOf(bit)))
                Held &= ~bit;
        }
    }

    public void Reset() => Held = PhysicalModifiers.None;

    public static int ToModifierFlags(PhysicalModifiers held)
    {
        int mods = 0;
        if ((held & (PhysicalModifiers.LShift | PhysicalModifiers.RShift)) != 0)
            mods |= ModifierFlags.Shift;

        bool realCtrl = (held & (PhysicalModifiers.LCtrl | PhysicalModifiers.RCtrl)) != 0;
        bool fakeCtrl = (held & PhysicalModifiers.AltGrCtrl) != 0;
        bool lAlt = (held & PhysicalModifiers.LAlt) != 0;
        bool rAlt = (held & PhysicalModifiers.RAlt) != 0;

        if (rAlt && !lAlt && !realCtrl)
            mods |= ModifierFlags.AltGr;
        else if ((realCtrl || fakeCtrl) && (lAlt || rAlt))
            mods |= ModifierFlags.AltGr;
        else
        {
            if (realCtrl) mods |= ModifierFlags.Ctrl;
            if (lAlt || rAlt) mods |= ModifierFlags.Alt;
        }

        if ((held & PhysicalModifiers.AnyWin) != 0)
            mods |= ModifierFlags.Win;

        return mods;
    }
}
