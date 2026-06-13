using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using KeyNexus.Core;
using MessageBox = System.Windows.MessageBox;
using ComboBox = System.Windows.Controls.ComboBox;

namespace KeyNexus;

public partial class RemapEditorWindow : Window
{
    private readonly string _groupKey;
    private readonly string _displayName;
    private readonly string? _layoutHkl;
    private readonly DeviceMonitor _monitor;
    private readonly ObservableCollection<RemapRuleViewModel> _rules = new();

    private bool _capturingTrigger;
    private bool _capturingOutput;
    private bool _suppressComboEvents;
    private int _triggerVk;
    private int _triggerMods;
    private int _outputVk;
    private int _outputMods;

    public RemapEditorWindow(string groupKey, string displayName, DeviceMonitor monitor)
    {
        InitializeComponent();
        _groupKey = groupKey;
        _displayName = displayName;
        _monitor = monitor;
        _layoutHkl = monitor.Config.GetLayoutForDevice(groupKey);

        txtDeviceName.Text = displayName;
        lstRules.ItemsSource = _rules;

        var keys = LayoutKeyHelper.GetAllKeys(_layoutHkl);
        cmbTriggerKey.ItemsSource = keys;
        cmbOutputKey.ItemsSource = keys;

        Loaded += (_, _) =>
        {
            LayoutKeyHelper.TryActivateLayout(this, _layoutHkl);
            txtLayoutHint.Text = string.IsNullOrEmpty(_layoutHkl)
                ? "Teclas exibidas conforme o layout ativo do Windows. Vincule um layout ao teclado para maior precisão."
                : $"Teclas exibidas conforme o layout vinculado: {ResolveLayoutName(_layoutHkl)}";
        };

        LoadRules();

        SourceInitialized += RemapEditorWindow_SourceInitialized;
    }

    private void RemapEditorWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(CaptureWndHook);
    }

    private IntPtr CaptureWndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_capturingTrigger && !_capturingOutput)
            return IntPtr.Zero;

        if (msg != NativeMethods.WM_KEYDOWN && msg != NativeMethods.WM_SYSKEYDOWN)
            return IntPtr.Zero;

        int vkHook = wParam.ToInt32() & 0xFFFF;
        if (IsCaptureModifierOnly(vkHook))
            return IntPtr.Zero;

        long lp = lParam.ToInt64();
        uint scan = (uint)((lp >> 16) & 0xFF);
        bool extended = ((lp >> 24) & 1) != 0;

        int physicalVk = LayoutKeyHelper.ResolvePhysicalVk(scan, extended, vkHook, _layoutHkl);
        if (physicalVk == 0)
            return IntPtr.Zero;

        bool captureTrigger = _capturingTrigger;
        bool captureOutput = _capturingOutput;
        int mods = ReadCaptureModifiers(captureTrigger);

        _capturingTrigger = false;
        _capturingOutput = false;

        Dispatcher.Invoke(() => ApplyCapturedKey(captureTrigger, captureOutput, physicalVk, mods));

        handled = true;
        return (IntPtr)1;
    }

    private static bool IsCaptureModifierOnly(int vk) =>
        vk is NativeMethods.VK_SHIFT or NativeMethods.VK_CONTROL or NativeMethods.VK_MENU
            or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT
            or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
            or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU;

    private void ApplyCapturedKey(bool trigger, bool output, int vk, int mods)
    {
        if (trigger)
        {
            _triggerVk = vk;
            _triggerMods = mods;
            SelectKeyInCombo(cmbTriggerKey, vk, mods);
            SyncModifierCheckboxes(mods);
        }
        else if (output)
        {
            _outputVk = vk;
            _outputMods = mods;
            SelectKeyInCombo(cmbOutputKey, vk, mods);
        }
    }

    private void LoadRules()
    {
        _rules.Clear();
        foreach (var rule in _monitor.Config.GetRemapRules(_groupKey))
            _rules.Add(RemapRuleViewModel.FromRule(rule, _layoutHkl));
        UpdateRuleCount();
    }

    private void UpdateRuleCount()
    {
        txtRuleCount.Text = $"{_rules.Count} regra(s) configurada(s)";
    }

    private string ResolveLayoutName(string layoutHkl)
    {
        long hkl = Convert.ToUInt32(layoutHkl, 16);
        return LayoutNameResolver.GetLayoutName(hkl);
    }

    private void BtnCaptureKey_Click(object sender, RoutedEventArgs e)
    {
        _capturingTrigger = true;
        _capturingOutput = false;
        cmbTriggerKey.Text = "Pressione uma tecla...";
    }

    private void BtnCaptureOutputKey_Click(object sender, RoutedEventArgs e)
    {
        _capturingOutput = true;
        _capturingTrigger = false;
        cmbOutputKey.Text = "Pressione uma tecla...";
    }

    private void CmbTriggerKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (cmbTriggerKey.SelectedItem is KeyOption opt)
        {
            _triggerVk = opt.Vk;
            _triggerMods = ModifierFlags.Normalize(opt.Modifiers);
            SyncModifierCheckboxes(_triggerMods);
        }
    }

    private void CmbOutputKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents) return;
        if (cmbOutputKey.SelectedItem is KeyOption opt)
        {
            _outputVk = opt.Vk;
            _outputMods = ModifierFlags.Normalize(opt.Modifiers);
        }
    }

    private void SyncModifierCheckboxes(int mods)
    {
        mods = ModifierFlags.Normalize(mods);
        if (ModifierFlags.IsAltGr(mods))
        {
            chkModCtrl.IsChecked = true;
            chkModAlt.IsChecked = true;
            chkModShift.IsChecked = (mods & ModifierFlags.Shift) != 0;
            return;
        }

        chkModCtrl.IsChecked = (mods & ModifierFlags.Ctrl) != 0;
        chkModShift.IsChecked = (mods & ModifierFlags.Shift) != 0;
        chkModAlt.IsChecked = (mods & ModifierFlags.Alt) != 0;
    }

    private static int CaptureModifiersFromKeyboard()
    {
        int mods = 0;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            mods |= ModifierFlags.Shift;

        bool rAlt = Keyboard.IsKeyDown(Key.RightAlt);
        bool lAlt = Keyboard.IsKeyDown(Key.LeftAlt);
        bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

        // AltGr — nunca gravar como Ctrl+Alt separados
        if (rAlt && !lAlt)
            mods |= ModifierFlags.AltGr;
        else if (ctrl && (lAlt || rAlt))
            mods |= ModifierFlags.AltGr;

        return mods;
    }

    private void TriggerModifier_Click(object sender, RoutedEventArgs e)
    {
        _triggerMods = LayoutKeyHelper.ReadModifiersFromCheckboxes(
            chkModCtrl.IsChecked == true,
            chkModShift.IsChecked == true,
            chkModAlt.IsChecked == true);
    }

    private int ReadCaptureModifiers(bool forTrigger)
    {
        if (forTrigger)
        {
            int fromBoxes = LayoutKeyHelper.ReadModifiersFromCheckboxes(
                chkModCtrl.IsChecked == true,
                chkModShift.IsChecked == true,
                chkModAlt.IsChecked == true);
            if (ModifierFlags.IsAltGr(fromBoxes) || fromBoxes != 0)
                return ModifierFlags.Normalize(fromBoxes);
        }

        return ModifierFlags.Normalize(CaptureModifiersFromKeyboard());
    }

    private void SelectKeyInCombo(ComboBox combo, int vk, int modifiers = 0)
    {
        _suppressComboEvents = true;
        var match = LayoutKeyHelper.FindByVk(vk, modifiers, _layoutHkl);
        if (match != null)
            combo.SelectedItem = match;
        else
            combo.Text = LayoutKeyHelper.GetKeyName(vk, _layoutHkl, modifiers);
        _suppressComboEvents = false;
    }

    private void CmbOutputType_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (panelOutputKey == null) return;

        string? tag = (cmbOutputType.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        panelOutputKey.Visibility = tag == "Key" ? Visibility.Visible : Visibility.Collapsed;
        panelOutputText.Visibility = tag == "Text" ? Visibility.Visible : Visibility.Collapsed;
        panelOutputSequence.Visibility = tag == "Sequence" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnAddRule_Click(object sender, RoutedEventArgs e)
    {
        if (_triggerVk == 0)
        {
            MessageBox.Show("Capture uma tecla gatilho primeiro.", "KeyNexus", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string? typeTag = (cmbOutputType.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Key";
        var rule = new RemapRule
        {
            TriggerVk = _triggerVk,
            Modifiers = ModifierFlags.Normalize(_triggerMods)
        };

        switch (typeTag)
        {
            case "Key":
                if (_outputVk == 0)
                {
                    MessageBox.Show("Capture uma tecla de saída.", "KeyNexus", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                int outMods = ModifierFlags.Normalize(_outputMods);
                // Caractere simples → salva como Texto (mais confiável que simular VK)
                if (LayoutKeyHelper.TryGetOutputCharacter(_outputVk, outMods, _layoutHkl, out char outChar))
                {
                    rule.OutputType = RemapOutputType.Text;
                    rule.OutputText = outChar.ToString();
                }
                else
                {
                    rule.OutputType = RemapOutputType.Key;
                    rule.OutputVk = _outputVk;
                    rule.OutputModifiers = outMods;
                }
                break;
            case "Text":
                rule.OutputType = RemapOutputType.Text;
                rule.OutputText = txtOutputText.Text;
                if (string.IsNullOrWhiteSpace(rule.OutputText))
                {
                    MessageBox.Show("Digite o texto de saída.", "KeyNexus", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                break;
            case "Sequence":
                rule.OutputType = RemapOutputType.Sequence;
                rule.Sequence = ParseSequence(txtOutputSequence.Text);
                if (rule.Sequence.Count == 0)
                {
                    MessageBox.Show("Defina a sequência de teclas.", "KeyNexus", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                break;
        }

        _rules.Add(RemapRuleViewModel.FromRule(rule, _layoutHkl));
        UpdateRuleCount();

        _triggerVk = 0;
        _triggerMods = 0;
        _outputVk = 0;
        _outputMods = 0;
        _suppressComboEvents = true;
        cmbTriggerKey.SelectedItem = null;
        cmbTriggerKey.Text = string.Empty;
        cmbOutputKey.SelectedItem = null;
        cmbOutputKey.Text = string.Empty;
        _suppressComboEvents = false;
        chkModCtrl.IsChecked = false;
        chkModShift.IsChecked = false;
        chkModAlt.IsChecked = false;
    }

    private List<RemapSequenceStep> ParseSequence(string text)
    {
        var steps = new List<RemapSequenceStep>();
        if (string.IsNullOrWhiteSpace(text))
            return steps;

        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(',', StringSplitOptions.TrimEntries);
            if (pieces.Length == 0) continue;

            var step = new RemapSequenceStep();
            string keyName = pieces[0].Trim();

            var opt = LayoutKeyHelper.FindByLabel(keyName, _layoutHkl);
            if (opt != null)
            {
                step.Vk = opt.Vk;
                step.Modifiers = opt.Modifiers;
            }
            else if (keyName.StartsWith("F", StringComparison.OrdinalIgnoreCase) && int.TryParse(keyName[1..], out int fn) && fn >= 1 && fn <= 24)
                step.Vk = 0x6F + fn;
            else
                step.Vk = ParseNamedKey(keyName);

            if (pieces.Length > 1 && int.TryParse(pieces[1], out int delay))
                step.DelayMs = delay;

            steps.Add(step);
        }
        return steps;
    }

    private static int ParseNamedKey(string name)
    {
        return name.ToUpperInvariant() switch
        {
            "ENTER" => 0x0D,
            "ESC" or "ESCAPE" => 0x1B,
            "TAB" => 0x09,
            "SPACE" => 0x20,
            "BACKSPACE" => 0x08,
            "DELETE" => 0x2E,
            _ => 0
        };
    }

    private void BtnRemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (lstRules.SelectedItem is RemapRuleViewModel vm)
        {
            _rules.Remove(vm);
            UpdateRuleCount();
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var rules = _rules.Select(vm => vm.ToRule()).ToList();
        _monitor.Config.SetRemapRules(_groupKey, rules);
        DialogResult = true;
        Close();
    }
}

public class RemapRuleViewModel
{
    private readonly string? _layoutHkl;

    public int TriggerVk { get; set; }
    public int Modifiers { get; set; }
    public RemapOutputType OutputType { get; set; }
    public int OutputVk { get; set; }
    public int OutputModifiers { get; set; }
    public string OutputText { get; set; } = string.Empty;
    public List<RemapSequenceStep> Sequence { get; set; } = new();

    public string TriggerDisplay => LayoutKeyHelper.FormatTrigger(TriggerVk, Modifiers, _layoutHkl);

    public string OutputDisplay => OutputType switch
    {
        RemapOutputType.Key => LayoutKeyHelper.FormatTrigger(OutputVk, OutputModifiers, _layoutHkl),
        RemapOutputType.Text => $"\"{OutputText}\"",
        RemapOutputType.Sequence => $"{Sequence.Count} passo(s)",
        _ => ""
    };

    public string TypeDisplay => OutputType switch
    {
        RemapOutputType.Key => "Tecla",
        RemapOutputType.Text => "Texto",
        RemapOutputType.Sequence => "Macro",
        _ => ""
    };

    public static RemapRuleViewModel FromRule(RemapRule rule, string? layoutHkl) => new(layoutHkl)
    {
        TriggerVk = rule.TriggerVk,
        Modifiers = ModifierFlags.Normalize(rule.Modifiers),
        OutputType = rule.OutputType,
        OutputVk = rule.OutputVk,
        OutputModifiers = ModifierFlags.Normalize(rule.OutputModifiers),
        OutputText = rule.OutputText,
        Sequence = rule.Sequence
    };

    private RemapRuleViewModel(string? layoutHkl) => _layoutHkl = layoutHkl;

    public RemapRule ToRule() => new()
    {
        TriggerVk = TriggerVk,
        Modifiers = ModifierFlags.Normalize(Modifiers),
        OutputType = OutputType,
        OutputVk = OutputVk,
        OutputModifiers = ModifierFlags.Normalize(OutputModifiers),
        OutputText = OutputText,
        Sequence = Sequence
    };
}
