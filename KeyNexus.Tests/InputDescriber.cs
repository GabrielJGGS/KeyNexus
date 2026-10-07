using KeyNexus.Core.Input;

namespace KeyNexus.Tests;

/// <summary>Descreve eventos INPUT como texto legível ("RAlt up", "sc34 down", "'?' down").</summary>
internal static class InputDescriber
{
    public static List<string> Describe(NativeMethods.INPUT[] inputs, int count) =>
        inputs.Take(count).Select(Describe).ToList();

    public static string Describe(NativeMethods.INPUT input)
    {
        var ki = input.ki;
        string direction = (ki.dwFlags & NativeMethods.KEYEVENTF_KEYUP) != 0 ? "up" : "down";

        if ((ki.dwFlags & NativeMethods.KEYEVENTF_UNICODE) != 0)
            return $"'{(char)ki.wScan}' {direction}";

        bool extended = (ki.dwFlags & NativeMethods.KEYEVENTF_EXTENDEDKEY) != 0;
        if ((ki.dwFlags & NativeMethods.KEYEVENTF_SCANCODE) != 0)
            return $"{ScanName(ki.wScan, extended)} {direction}";

        return $"vk{ki.wVk:X2} {direction}";
    }

    public static string ScanName(ushort scan, bool extended) => (scan, extended) switch
    {
        (0x1D, false) => "LCtrl",
        (0x1D, true) => "RCtrl",
        (0x38, false) => "LAlt",
        (0x38, true) => "RAlt",
        (0x2A, _) => "LShift",
        (0x36, _) => "RShift",
        (0x5B, true) => "LWin",
        (0x5C, true) => "RWin",
        _ => $"sc{scan:X2}{(extended ? "e" : "")}"
    };

    /// <summary>Tecla virtual de um evento injetado por scan code (só modificadores importam aqui).</summary>
    public static int ModifierVk(ushort scan, bool extended) => (scan, extended) switch
    {
        (0x1D, false) => NativeMethods.VK_LCONTROL,
        (0x1D, true) => NativeMethods.VK_RCONTROL,
        (0x38, false) => NativeMethods.VK_LMENU,
        (0x38, true) => NativeMethods.VK_RMENU,
        (0x2A, _) => NativeMethods.VK_LSHIFT,
        (0x36, _) => NativeMethods.VK_RSHIFT,
        (0x5B, true) => NativeMethods.VK_LWIN,
        (0x5C, true) => NativeMethods.VK_RWIN,
        _ => 0
    };

    public static bool IsSignedByKeyNexus(NativeMethods.INPUT input) =>
        input.ki.dwExtraInfo == InputBatchBuilder.Signature;
}
