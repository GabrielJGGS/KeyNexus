using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using KeyNexus.Core;
using KeyNexus.Themes;
using KeyNexus.ViewModels;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;

namespace KeyNexus;

public partial class RemapEditorWindow : Window
{
    private enum CaptureTarget
    {
        None,
        Trigger,
        Output
    }

    private readonly string _groupKey;
    private readonly string? _layoutHkl;
    private readonly DeviceMonitor _monitor;
    private readonly List<RemapRule> _ruleModels = new();
    private readonly ObservableCollection<RemapRuleViewModel> _rules = new();

    private CaptureTarget _capture;
    private IDisposable? _suspension;
    private bool _suppressComboEvents;
    private bool _suppressToggleEvents;
    private int _triggerVk;
    private int _triggerMods;
    private int _outputVk;
    private int _outputMods;
    private int _editingIndex = -1;

    public RemapEditorWindow(string groupKey, string displayName, DeviceMonitor monitor)
    {
        InitializeComponent();
        WindowEffects.Apply(this, useMica: true);

        _groupKey = groupKey;
        _monitor = monitor;
        _layoutHkl = monitor.Config.GetLayoutForDevice(groupKey);

        txtTitle.Text = $"Teclas do {displayName}";
        txtTest.Tag = $"Digite aqui usando o {displayName} para testar as regras";
        lstRules.ItemsSource = _rules;

        var keys = LayoutKeyHelper.GetAllKeys(_layoutHkl);
        cmbTriggerKey.ItemsSource = keys;
        cmbOutputKey.ItemsSource = keys;

        foreach (var rule in monitor.Config.GetRemapRules(groupKey))
            _ruleModels.Add(RemapRuleViewModel.Clone(rule));

        RebuildRuleViews();
        UpdateTypeHelp();
        UpdateTriggerChips();
        UpdateOutputChips();

        Loaded += (_, _) =>
        {
            LayoutKeyHelper.TryActivateLayout(this, _layoutHkl);
            UpdateLayoutHint();
        };
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(CaptureWndHook);
        };
        Deactivated += (_, _) => StopCapture();
        Closed += (_, _) => StopCapture();
    }

    private void UpdateLayoutHint()
    {
        if (string.IsNullOrEmpty(_layoutHkl))
        {
            txtLayoutHint.Text = "Este teclado não tem layout definido: as teclas aparecem conforme o layout ativo do Windows.";
            return;
        }

        var info = KeyboardLayoutCatalog.Describe(_layoutHkl);
        string title = _monitor.Config.GetLayoutAlias(_layoutHkl) ?? info.LayoutName;
        txtLayoutHint.Text = $"Layout: {title} · {info.LanguageName}. As teclas aparecem como nesse layout.";
    }

    // ══════════════════════════════════════
    // Captura de tecla
    // ══════════════════════════════════════

    private void BtnCaptureTrigger_Click(object sender, RoutedEventArgs e) => ToggleCapture(CaptureTarget.Trigger);

    private void BtnCaptureOutput_Click(object sender, RoutedEventArgs e) => ToggleCapture(CaptureTarget.Output);

    private void ToggleCapture(CaptureTarget target)
    {
        if (_capture == target)
        {
            StopCapture();
            return;
        }

        StopCapture();
        HideHint();
        _capture = target;
        _suspension = _monitor.SuspendRemapping();

        if (target == CaptureTarget.Trigger)
        {
            btnCaptureTrigger.Tag = "capturing";
            triggerChips.ItemsSource = null;
            txtTriggerPlaceholder.Text = "Aperte a tecla agora (clique de novo para cancelar)";
            txtTriggerPlaceholder.Visibility = Visibility.Visible;
        }
        else
        {
            btnCaptureOutput.Tag = "capturing";
            outputChips.ItemsSource = null;
            txtOutputPlaceholder.Text = "Aperte a tecla que deve sair (clique de novo para cancelar)";
            txtOutputPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void StopCapture()
    {
        if (_capture == CaptureTarget.None && _suspension == null)
            return;

        _capture = CaptureTarget.None;
        _suspension?.Dispose();
        _suspension = null;
        btnCaptureTrigger.Tag = null;
        btnCaptureOutput.Tag = null;
        UpdateTriggerChips();
        UpdateOutputChips();
    }

    private IntPtr CaptureWndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_capture == CaptureTarget.None)
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

        var target = _capture;
        int mods = CaptureModifiersFromKeyboard();
        StopCapture();
        ApplyCapturedKey(target, physicalVk, mods);

        handled = true;
        return (IntPtr)1;
    }

    private static bool IsCaptureModifierOnly(int vk) =>
        vk is NativeMethods.VK_SHIFT or NativeMethods.VK_CONTROL or NativeMethods.VK_MENU
            or NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT
            or NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL
            or NativeMethods.VK_LMENU or NativeMethods.VK_RMENU
            or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN;

    private static int CaptureModifiersFromKeyboard()
    {
        int mods = 0;
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            mods |= ModifierFlags.Shift;

        bool rAlt = Keyboard.IsKeyDown(Key.RightAlt);
        bool lAlt = Keyboard.IsKeyDown(Key.LeftAlt);
        bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);

        // AltGr chega como Ctrl esquerdo + Alt direito: grava como AltGr, nunca como Ctrl+Alt.
        if (rAlt && !lAlt)
            mods |= ModifierFlags.AltGr;
        else if (ctrl && lAlt)
            mods |= ModifierFlags.AltGr;
        else
        {
            if (ctrl) mods |= ModifierFlags.Ctrl;
            if (lAlt) mods |= ModifierFlags.Alt;
        }

        return mods;
    }

    private void ApplyCapturedKey(CaptureTarget target, int vk, int mods)
    {
        mods = ModifierFlags.Normalize(mods);
        if (target == CaptureTarget.Trigger)
        {
            _triggerVk = vk;
            _triggerMods = mods;
            SelectKeyInCombo(cmbTriggerKey, vk, 0);
            SyncToggles(mods);
            UpdateTriggerChips();
        }
        else if (target == CaptureTarget.Output)
        {
            _outputVk = vk;
            _outputMods = mods;
            SelectKeyInCombo(cmbOutputKey, vk, mods);
            UpdateOutputChips();
        }
    }

    // ══════════════════════════════════════
    // Gatilho e saída
    // ══════════════════════════════════════

    private void CmbTriggerKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents || cmbTriggerKey.SelectedItem is not KeyOption option)
            return;

        _triggerVk = option.Vk;
        _triggerMods = ModifierFlags.Normalize(option.Modifiers);
        SyncToggles(_triggerMods);
        UpdateTriggerChips();
        HideHint();
    }

    private void CmbOutputKey_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressComboEvents || cmbOutputKey.SelectedItem is not KeyOption option)
            return;

        _outputVk = option.Vk;
        _outputMods = ModifierFlags.Normalize(option.Modifiers);
        UpdateOutputChips();
        HideHint();
    }

    private void TriggerModifier_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents)
            return;

        _suppressToggleEvents = true;
        try
        {
            if (sender == tglAltGr && tglAltGr.IsChecked == true)
            {
                tglCtrl.IsChecked = false;
                tglAlt.IsChecked = false;
            }
            else if ((sender == tglCtrl || sender == tglAlt) && ((ToggleButton)sender).IsChecked == true)
            {
                tglAltGr.IsChecked = false;
            }

            // Ctrl + Alt juntos funcionam como AltGr no Windows.
            if (tglCtrl.IsChecked == true && tglAlt.IsChecked == true)
            {
                tglCtrl.IsChecked = false;
                tglAlt.IsChecked = false;
                tglAltGr.IsChecked = true;
            }
        }
        finally
        {
            _suppressToggleEvents = false;
        }

        _triggerMods = ReadToggles();
        UpdateTriggerChips();
    }

    private int ReadToggles()
    {
        int mods = 0;
        if (tglShift.IsChecked == true) mods |= ModifierFlags.Shift;
        if (tglAltGr.IsChecked == true) mods |= ModifierFlags.AltGr;
        if (tglCtrl.IsChecked == true) mods |= ModifierFlags.Ctrl;
        if (tglAlt.IsChecked == true) mods |= ModifierFlags.Alt;
        return ModifierFlags.Normalize(mods);
    }

    private void SyncToggles(int mods)
    {
        mods = ModifierFlags.Normalize(mods);
        _suppressToggleEvents = true;
        tglShift.IsChecked = (mods & ModifierFlags.Shift) != 0;
        tglAltGr.IsChecked = ModifierFlags.IsAltGr(mods);
        tglCtrl.IsChecked = (mods & ModifierFlags.Ctrl) != 0;
        tglAlt.IsChecked = (mods & ModifierFlags.Alt) != 0;
        _suppressToggleEvents = false;
    }

    private void UpdateTriggerChips()
    {
        if (_capture == CaptureTarget.Trigger)
            return;

        if (_triggerVk == 0)
        {
            triggerChips.ItemsSource = null;
            txtTriggerPlaceholder.Text = "Clique aqui e aperte a tecla";
            txtTriggerPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        triggerChips.ItemsSource = LayoutKeyHelper.GetTriggerParts(_triggerVk, _triggerMods, _layoutHkl);
        txtTriggerPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void UpdateOutputChips()
    {
        if (_capture == CaptureTarget.Output)
            return;

        if (_outputVk == 0)
        {
            outputChips.ItemsSource = null;
            txtOutputPlaceholder.Text = "Clique aqui e aperte a tecla que deve sair";
            txtOutputPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        outputChips.ItemsSource = LayoutKeyHelper.GetOutputParts(_outputVk, _outputMods, _layoutHkl);
        txtOutputPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void SelectKeyInCombo(ComboBox combo, int vk, int modifiers)
    {
        _suppressComboEvents = true;
        var match = LayoutKeyHelper.FindByVk(vk, modifiers, _layoutHkl);
        if (match != null)
            combo.SelectedItem = match;
        else
        {
            combo.SelectedItem = null;
            combo.Text = LayoutKeyHelper.GetKeyName(vk, _layoutHkl, modifiers);
        }
        _suppressComboEvents = false;
    }

    private void OutputType_Checked(object sender, RoutedEventArgs e)
    {
        if (panelOutputKey == null || txtOutputText == null || panelOutputSequence == null)
            return;

        panelOutputKey.Visibility = rbKey.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        txtOutputText.Visibility = rbText.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        panelOutputSequence.Visibility = rbMacro.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UpdateTypeHelp();
        HideHint();
    }

    private void UpdateTypeHelp()
    {
        if (txtTypeHelp == null)
            return;

        if (rbText.IsChecked == true)
            txtTypeHelp.Text = "Digita exatamente estes caracteres. Bom para símbolos que não existem no layout; pode falhar em terminal e SSH.";
        else if (rbMacro.IsChecked == true)
            txtTypeHelp.Text = "Envia uma sequência de teclas, com atraso opcional entre elas.";
        else
            txtTypeHelp.Text = "Simula a tecla física. Funciona em terminal, SSH, jogos e acesso remoto. Recomendado.";
    }

    // ══════════════════════════════════════
    // Regras
    // ══════════════════════════════════════

    private void BtnAddRule_Click(object sender, RoutedEventArgs e)
    {
        StopCapture();

        if (_triggerVk == 0)
        {
            ShowHint("Primeiro clique em \"Quando eu apertar\" e aperte a tecla que quer mudar.");
            return;
        }

        var rule = new RemapRule
        {
            TriggerVk = _triggerVk,
            Modifiers = ModifierFlags.Normalize(_triggerMods)
        };

        if (rbKey.IsChecked == true)
        {
            if (_outputVk == 0)
            {
                ShowHint("Escolha a tecla que deve sair: clique no campo e aperte a tecla.");
                return;
            }
            rule.OutputType = RemapOutputType.Key;
            rule.OutputVk = _outputVk;
            rule.OutputModifiers = ModifierFlags.Normalize(_outputMods);
        }
        else if (rbText.IsChecked == true)
        {
            if (string.IsNullOrEmpty(txtOutputText.Text))
            {
                ShowHint("Digite o texto que deve sair.");
                return;
            }
            rule.OutputType = RemapOutputType.Text;
            rule.OutputText = txtOutputText.Text;
        }
        else
        {
            rule.OutputType = RemapOutputType.Sequence;
            rule.Sequence = ParseSequence(txtOutputSequence.Text);
            if (rule.Sequence.Count == 0)
            {
                ShowHint("Defina os passos da macro. Ex.: F1,100; F2,50; A");
                return;
            }
        }

        int duplicate = _ruleModels.FindIndex(r =>
            r.TriggerVk == rule.TriggerVk && ModifierFlags.Normalize(r.Modifiers) == rule.Modifiers);

        if (duplicate >= 0 && duplicate != _editingIndex)
        {
            string trigger = string.Join(" + ", LayoutKeyHelper.GetTriggerParts(rule.TriggerVk, rule.Modifiers, _layoutHkl));
            var answer = MessageBox.Show(
                $"Já existe uma regra para {trigger}. Substituir?",
                "KeyNexus", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;

            if (_editingIndex >= 0)
            {
                _ruleModels.RemoveAt(_editingIndex);
                if (duplicate > _editingIndex)
                    duplicate--;
            }
            _ruleModels[duplicate] = rule;
        }
        else if (_editingIndex >= 0)
        {
            _ruleModels[_editingIndex] = rule;
        }
        else
        {
            _ruleModels.Add(rule);
        }

        _editingIndex = -1;
        SaveRules();
        ResetBuilder();
    }

    private void BtnEditRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RemapRuleViewModel vm })
            return;

        int index = _rules.IndexOf(vm);
        if (index < 0 || index >= _ruleModels.Count)
            return;

        StopCapture();
        HideHint();
        _editingIndex = index;
        LoadIntoBuilder(_ruleModels[index]);
        foreach (var item in _rules)
            item.IsEditing = ReferenceEquals(item, vm);

        btnAddRule.Content = "Salvar alteração";
        btnCancelEdit.Visibility = Visibility.Visible;
    }

    private void BtnCancelEdit_Click(object sender, RoutedEventArgs e) => ResetBuilder();

    private void BtnDeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RemapRuleViewModel vm })
            return;

        int index = _rules.IndexOf(vm);
        if (index < 0 || index >= _ruleModels.Count)
            return;

        _ruleModels.RemoveAt(index);
        if (_editingIndex == index)
            ResetBuilder();
        else if (_editingIndex > index)
            _editingIndex--;

        SaveRules();
    }

    private void BtnConvertRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RemapRuleViewModel vm })
            return;

        int index = _rules.IndexOf(vm);
        if (index < 0 || index >= _ruleModels.Count)
            return;

        var rule = _ruleModels[index];
        if (rule.OutputText.Length != 1
            || !LayoutKeyHelper.TryFindKeyForCharacter(rule.OutputText[0], _layoutHkl, out int vk, out int mods))
        {
            ShowHint($"\"{rule.OutputText}\" não existe como tecla no layout deste teclado. Mantenha como texto.");
            return;
        }

        _ruleModels[index] = new RemapRule
        {
            TriggerVk = rule.TriggerVk,
            Modifiers = ModifierFlags.Normalize(rule.Modifiers),
            OutputType = RemapOutputType.Key,
            OutputVk = vk,
            OutputModifiers = ModifierFlags.Normalize(mods),
            Description = rule.Description
        };

        if (_editingIndex == index)
            ResetBuilder();
        SaveRules();
    }

    private void LoadIntoBuilder(RemapRule rule)
    {
        _triggerVk = rule.TriggerVk;
        _triggerMods = ModifierFlags.Normalize(rule.Modifiers);
        SelectKeyInCombo(cmbTriggerKey, _triggerVk, 0);
        SyncToggles(_triggerMods);
        UpdateTriggerChips();

        _outputVk = 0;
        _outputMods = 0;
        txtOutputText.Text = string.Empty;
        txtOutputSequence.Text = string.Empty;

        switch (rule.OutputType)
        {
            case RemapOutputType.Key:
                rbKey.IsChecked = true;
                _outputVk = rule.OutputVk;
                _outputMods = ModifierFlags.Normalize(rule.OutputModifiers);
                SelectKeyInCombo(cmbOutputKey, _outputVk, _outputMods);
                break;
            case RemapOutputType.Text:
                rbText.IsChecked = true;
                txtOutputText.Text = rule.OutputText;
                break;
            case RemapOutputType.Sequence:
                rbMacro.IsChecked = true;
                txtOutputSequence.Text = FormatSequence(rule.Sequence);
                break;
        }

        UpdateOutputChips();
    }

    private void ResetBuilder()
    {
        StopCapture();
        _editingIndex = -1;
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

        SyncToggles(0);
        txtOutputText.Text = string.Empty;
        txtOutputSequence.Text = string.Empty;
        btnAddRule.Content = "Adicionar regra";
        btnCancelEdit.Visibility = Visibility.Collapsed;
        HideHint();

        foreach (var item in _rules)
            item.IsEditing = false;

        UpdateTriggerChips();
        UpdateOutputChips();
    }

    private void SaveRules()
    {
        _monitor.Config.SetRemapRules(_groupKey, _ruleModels.Select(RemapRuleViewModel.Clone).ToList());
        RebuildRuleViews();
    }

    private void RebuildRuleViews()
    {
        _rules.Clear();
        for (int i = 0; i < _ruleModels.Count; i++)
            _rules.Add(new RemapRuleViewModel(_ruleModels[i], _layoutHkl) { IsEditing = i == _editingIndex });

        txtRuleCount.Text = _ruleModels.Count switch
        {
            0 => string.Empty,
            1 => "1 regra",
            _ => $"{_ruleModels.Count} regras"
        };
        txtNoRules.Visibility = _ruleModels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowHint(string text)
    {
        txtBuilderHint.Text = text;
        txtBuilderHint.Visibility = Visibility.Visible;
    }

    private void HideHint() => txtBuilderHint.Visibility = Visibility.Collapsed;

    // ══════════════════════════════════════
    // Macro
    // ══════════════════════════════════════

    private List<RemapSequenceStep> ParseSequence(string text)
    {
        var steps = new List<RemapSequenceStep>();
        if (string.IsNullOrWhiteSpace(text))
            return steps;

        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(',', StringSplitOptions.TrimEntries);
            if (pieces.Length == 0 || string.IsNullOrEmpty(pieces[0]))
                continue;

            var step = new RemapSequenceStep();
            string keyName = pieces[0].Trim();

            var option = LayoutKeyHelper.FindByLabel(keyName, _layoutHkl);
            if (option != null)
            {
                step.Vk = option.Vk;
                step.Modifiers = option.Modifiers;
            }
            else if (keyName.StartsWith("F", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(keyName[1..], out int fn) && fn >= 1 && fn <= 24)
            {
                step.Vk = 0x6F + fn;
            }
            else
            {
                step.Vk = ParseNamedKey(keyName);
            }

            if (step.Vk == 0)
                continue;

            if (pieces.Length > 1 && int.TryParse(pieces[1], out int delay))
                step.DelayMs = Math.Max(0, delay);

            steps.Add(step);
        }
        return steps;
    }

    private string FormatSequence(IEnumerable<RemapSequenceStep> steps) =>
        string.Join("; ", steps.Select(step =>
        {
            string name = step.Vk > 0
                ? LayoutKeyHelper.GetKeyName(step.Vk, _layoutHkl, step.Modifiers)
                : step.Text;
            return step.DelayMs > 0 ? $"{name},{step.DelayMs}" : name;
        }));

    private static int ParseNamedKey(string name) => name.ToUpperInvariant() switch
    {
        "ENTER" => 0x0D,
        "ESC" or "ESCAPE" => 0x1B,
        "TAB" => 0x09,
        "SPACE" or "ESPAÇO" => 0x20,
        "BACKSPACE" => 0x08,
        "DELETE" or "DEL" => 0x2E,
        _ => 0
    };

    private void BtnDone_Click(object sender, RoutedEventArgs e)
    {
        StopCapture();
        DialogResult = true;
    }
}
