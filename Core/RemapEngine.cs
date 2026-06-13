using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyNexus.Core;

public class RemapEngine : IDisposable
{
    private readonly ConfigManager _config;
    private readonly Func<string> _getActiveGroupKey;
    private IntPtr _hookHandle = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _hookProc;
    private bool _isInjecting;

    public RemapEngine(ConfigManager config, Func<string> getActiveGroupKey)
    {
        _config = config;
        _getActiveGroupKey = getActiveGroupKey;
    }

    public void Start()
    {
        if (_hookHandle != IntPtr.Zero)
            return;

        _hookProc = HookCallback;
        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        if (curModule == null)
        {
            Logger.Error("RemapEngine: MainModule nulo");
            return;
        }

        _hookHandle = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _hookProc,
            NativeMethods.GetModuleHandle(curModule.ModuleName),
            0);

        if (_hookHandle == IntPtr.Zero)
            Logger.Error("RemapEngine: falha ao instalar hook de teclado");
        else
            Logger.Info($"RemapEngine: hook instalado (INPUT={NativeMethods.InputStructSize} bytes)");
    }

    public void Stop()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
            Logger.Info("RemapEngine: hook de teclado removido");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0 || _isInjecting)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        int msg = wParam.ToInt32();
        bool isKeyDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        if (!isKeyDown)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var hookStruct = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        if ((hookStruct.flags & NativeMethods.LLKHF_INJECTED) != 0)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        int vk = (int)hookStruct.vkCode;
        if (IsModifierKey(vk))
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        string activeKey = _getActiveGroupKey();
        if (string.IsNullOrEmpty(activeKey))
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var rules = _config.GetRemapRules(activeKey);
        if (rules.Count == 0)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        string? layoutHkl = _config.GetLayoutForDevice(activeKey);
        bool extended = (hookStruct.flags & NativeMethods.LLKHF_EXTENDED) != 0;
        int physicalVk = LayoutKeyHelper.VkFromScanCode(hookStruct.scanCode, extended, layoutHkl);
        if (physicalVk == 0)
            physicalVk = vk;

        int currentMods = GetCurrentModifiers();
        var rule = rules.FirstOrDefault(r =>
            r.TriggerVk == physicalVk && ModifierFlags.Normalize(r.Modifiers) == currentMods);
        if (rule == null)
            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        var capturedRule = rule;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                _isInjecting = true;
                Thread.Sleep(1);
                ExecuteRule(capturedRule, layoutHkl);
            }
            catch (Exception ex)
            {
                Logger.Error("RemapEngine: falha ao executar regra", ex);
            }
            finally
            {
                _isInjecting = false;
            }
        });

        return (IntPtr)1;
    }

    private void ExecuteRule(RemapRule rule, string? layoutHkl)
    {
        switch (rule.OutputType)
        {
            case RemapOutputType.Key:
                int outMods = ModifierFlags.Normalize(rule.OutputModifiers);
                if (LayoutKeyHelper.TryGetOutputCharacter(rule.OutputVk, outMods, layoutHkl, out char ch))
                {
                    SendText(ch.ToString());
                }
                else if (outMods != 0)
                {
                    SendKeyWithModifiers(rule.OutputVk, outMods, layoutHkl);
                }
                else
                {
                    SendKeyPress(rule.OutputVk, layoutHkl);
                }
                break;
            case RemapOutputType.Text:
                SendText(rule.OutputText);
                break;
            case RemapOutputType.Sequence:
                ExecuteSequence(rule.Sequence, layoutHkl);
                break;
        }
    }

    private void ExecuteSequence(List<RemapSequenceStep> steps, string? layoutHkl)
    {
        foreach (var step in steps)
        {
            if (step.DelayMs > 0)
                Thread.Sleep(step.DelayMs);

            if (!string.IsNullOrEmpty(step.Text))
                SendText(step.Text);
            else if (step.Vk > 0)
            {
                int mods = ModifierFlags.Normalize(step.Modifiers);
                if (LayoutKeyHelper.TryGetOutputCharacter(step.Vk, mods, layoutHkl, out char ch))
                    SendText(ch.ToString());
                else
                    SendKeyWithModifiers(step.Vk, mods, layoutHkl);
            }
        }
    }

    private void SendKeyPress(int vk, string? layoutHkl)
    {
        var inputs = new NativeMethods.INPUT[2];
        inputs[0] = CreateKeyInput((ushort)vk, false, layoutHkl);
        inputs[1] = CreateKeyInput((ushort)vk, true, layoutHkl);
        NativeMethods.SendInput(2, inputs, NativeMethods.InputStructSize);
    }

    private void SendKeyWithModifiers(int vk, int modifiers, string? layoutHkl)
    {
        var modKeys = GetModifierVks(modifiers);
        var inputs = new List<NativeMethods.INPUT>();

        foreach (var mod in modKeys)
            inputs.Add(CreateKeyInput((ushort)mod, false, layoutHkl));

        inputs.Add(CreateKeyInput((ushort)vk, false, layoutHkl));
        inputs.Add(CreateKeyInput((ushort)vk, true, layoutHkl));

        for (int i = modKeys.Count - 1; i >= 0; i--)
            inputs.Add(CreateKeyInput((ushort)modKeys[i], true, layoutHkl));

        NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), NativeMethods.InputStructSize);
    }

    private void SendText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var inputs = new List<NativeMethods.INPUT>();
        foreach (char c in text)
        {
            inputs.Add(CreateUnicodeInput(c, false));
            inputs.Add(CreateUnicodeInput(c, true));
        }

        uint sent = NativeMethods.SendInput((uint)inputs.Count, inputs.ToArray(), NativeMethods.InputStructSize);
        if (sent == 0)
        {
            Logger.Error($"RemapEngine: SendInput falhou para texto \"{text}\"");
            foreach (char c in text)
                TryPostChar(c);
        }
    }

    private static bool TryPostChar(char c)
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return false;

        return NativeMethods.PostMessage(hwnd, NativeMethods.WM_CHAR, (IntPtr)c, IntPtr.Zero);
    }

    private static NativeMethods.INPUT CreateKeyInput(ushort vk, bool keyUp, string? layoutHklHex = null)
    {
        IntPtr hkl = LayoutKeyHelper.ResolveHkl(layoutHklHex);
        uint scan = NativeMethods.MapVirtualKeyEx(vk, NativeMethods.MAPVK_VK_TO_VSC, hkl);
        uint flags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0;

        if (scan != 0)
        {
            if (IsExtendedVk(vk))
                flags |= NativeMethods.KEYEVENTF_EXTENDEDKEY;

            return new NativeMethods.INPUT
            {
                type = NativeMethods.INPUT_KEYBOARD,
                ki = new NativeMethods.KEYBDINPUT
                {
                    wVk = 0,
                    wScan = (ushort)(scan & 0xFF),
                    dwFlags = flags | NativeMethods.KEYEVENTF_SCANCODE,
                    time = 0,
                    dwExtraInfo = new IntPtr(unchecked((int)0x4B4E584E))
                }
            };
        }

        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = vk,
                wScan = 0,
                dwFlags = flags,
                time = 0,
                dwExtraInfo = new IntPtr(unchecked((int)0x4B4E584E))
            }
        };
    }

    private static bool IsExtendedVk(ushort vk) =>
        vk is 0xA3 or 0xA5 or 0x2D or 0x2E or 0xC1 or 0xC2 or 0xE2
            or >= 0x21 and <= 0x28;

    private static NativeMethods.INPUT CreateUnicodeInput(char c, bool keyUp)
    {
        return new NativeMethods.INPUT
        {
            type = NativeMethods.INPUT_KEYBOARD,
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = (keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0) | NativeMethods.KEYEVENTF_UNICODE,
                time = 0,
                dwExtraInfo = new IntPtr(unchecked((int)0x4B4E584E))
            }
        };
    }

    private static int GetCurrentModifiers()
    {
        bool shift = IsKeyDown(NativeMethods.VK_SHIFT)
            || IsKeyDown(NativeMethods.VK_LSHIFT) || IsKeyDown(NativeMethods.VK_RSHIFT);
        bool lCtrl = IsKeyDown(NativeMethods.VK_LCONTROL);
        bool rCtrl = IsKeyDown(NativeMethods.VK_RCONTROL);
        bool lAlt = IsKeyDown(NativeMethods.VK_LMENU);
        bool rAlt = IsKeyDown(NativeMethods.VK_RMENU);

        int mods = 0;
        if (shift) mods |= ModifierFlags.Shift;

        // AltGr: Alt direito sozinho, ou Ctrl+Alt (teclado ABNT)
        if (rAlt && !lAlt && !lCtrl && !rCtrl)
            mods |= ModifierFlags.AltGr;
        else if ((lCtrl || rCtrl) && (lAlt || rAlt))
            mods |= ModifierFlags.AltGr;
        else
        {
            if (lCtrl || rCtrl) mods |= ModifierFlags.Ctrl;
            if (lAlt || rAlt) mods |= ModifierFlags.Alt;
        }

        return mods;
    }

    private static bool IsKeyDown(int vk) =>
        (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool IsModifierKey(int vk) =>
        vk is NativeMethods.VK_SHIFT or NativeMethods.VK_CONTROL or NativeMethods.VK_MENU
            or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT
            or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
            or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU;

    private static List<int> GetModifierVks(int modifiers)
    {
        modifiers = ModifierFlags.Normalize(modifiers);
        var list = new List<int>();

        if (ModifierFlags.IsAltGr(modifiers))
        {
            list.Add(NativeMethods.VK_LCONTROL);
            list.Add(NativeMethods.VK_RMENU);
        }
        else
        {
            if ((modifiers & ModifierFlags.Ctrl) != 0) list.Add(NativeMethods.VK_CONTROL);
            if ((modifiers & ModifierFlags.Alt) != 0) list.Add(NativeMethods.VK_MENU);
        }

        if ((modifiers & ModifierFlags.Shift) != 0)
            list.Add(NativeMethods.VK_SHIFT);

        return list;
    }

    public void Dispose() => Stop();
}

public static class ModifierFlags
{
    public const int Ctrl = 1;
    public const int Shift = 2;
    public const int Alt = 4;
    /// <summary>AltGr — bit separado; não confundir com Ctrl+Alt.</summary>
    public const int AltGr = 8;

    public static bool IsAltGr(int mods) => (mods & AltGr) != 0;

    /// <summary>Converte regras antigas que gravaram AltGr como Ctrl|Alt (5).</summary>
    public static int Normalize(int mods)
    {
        if ((mods & (Ctrl | Alt)) == (Ctrl | Alt))
            return (mods & ~(Ctrl | Alt)) | AltGr;
        return mods;
    }

    public static string ToDisplayString(int mods)
    {
        mods = Normalize(mods);
        var parts = new List<string>();
        if (IsAltGr(mods)) parts.Add("AltGr");
        if ((mods & Shift) != 0) parts.Add("Shift");
        if ((mods & Ctrl) != 0) parts.Add("Ctrl");
        if ((mods & Alt) != 0) parts.Add("Alt");
        return parts.Count > 0 ? string.Join("+", parts) : "";
    }
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
