using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KeyNexus.Core;
using KeyNexus.Themes;
using KeyNexus.ViewModels;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using ContextMenu = System.Windows.Controls.ContextMenu;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace KeyNexus;

public partial class MainWindow : Window
{
    private readonly DeviceMonitor _monitor;
    private readonly ObservableCollection<KeyboardItemViewModel> _keyboards = new();
    private readonly ObservableCollection<SavedKeyboardViewModel> _saved = new();
    private readonly ObservableCollection<LayoutOption> _layouts = new();
    private string _layoutSignature = string.Empty;
    private bool _refreshing;
    private bool _refreshPending;

    private sealed record KeyboardScan(KeyboardGroup Group, string Name, KeyboardBusType Bus);

    public MainWindow()
    {
        InitializeComponent();
        WindowEffects.Apply(this, useMica: true);

        _monitor = ((App)Application.Current).Monitor;
        lstKeyboards.ItemsSource = _keyboards;
        lstSaved.ItemsSource = _saved;

        string version = AppVersion.Current;
        txtVersion.Text = $"v{version}";
        txtVersionFull.Text = $"KeyNexus v{version}";

        chkAutoStart.IsChecked = ConfigManager.IsAutoStartEnabled();
        chkPaused.IsChecked = _monitor.IsPaused;
        UpdatePausedBanner();
        onboarding.Visibility = _monitor.Config.ShouldShowOnboarding ? Visibility.Visible : Visibility.Collapsed;

        ReloadLayouts(force: true);
        UpdateLiveStrip();

        _monitor.ActiveKeyboardChanged += OnActiveKeyboardChanged;
        _monitor.DevicesChanged += OnDevicesChanged;
        _monitor.PausedChanged += OnPausedChanged;

        Loaded += async (_, _) => await RefreshKeyboardsAsync();
        Activated += (_, _) => ReloadLayouts(force: false);

        AttachUpdateUi();
    }

    partial void AttachUpdateUi();

    protected override void OnClosed(EventArgs e)
    {
        _monitor.ActiveKeyboardChanged -= OnActiveKeyboardChanged;
        _monitor.DevicesChanged -= OnDevicesChanged;
        _monitor.PausedChanged -= OnPausedChanged;
        foreach (var vm in _keyboards)
            vm.LayoutChanged -= OnKeyboardLayoutChanged;
        base.OnClosed(e);
    }

    // ══════════════════════════════════════
    // Lista de teclados
    // ══════════════════════════════════════

    private async Task RefreshKeyboardsAsync()
    {
        if (_refreshing)
        {
            _refreshPending = true;
            return;
        }

        _refreshing = true;
        try
        {
            do
            {
                _refreshPending = false;
                List<KeyboardScan> scans;
                try
                {
                    scans = await Task.Run(() => _monitor.GetConnectedKeyboardGroups()
                        .Select(group => new KeyboardScan(
                            group,
                            DeviceNameResolver.GetFriendlyName(group.RepresentativePath),
                            DeviceIdentityParser.Parse(group.RepresentativePath, group.RawPaths).BusType))
                        .ToList());
                }
                catch (Exception ex)
                {
                    Logger.Error("Falha ao listar teclados", ex);
                    scans = new List<KeyboardScan>();
                }

                ApplyKeyboards(scans);
            }
            while (_refreshPending);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyKeyboards(List<KeyboardScan> scans)
    {
        var existing = _keyboards.ToDictionary(k => k.GroupKey, StringComparer.OrdinalIgnoreCase);
        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? active = _monitor.ActiveGroupKey;

        foreach (var scan in scans)
        {
            if (!existing.TryGetValue(scan.Group.GroupKey, out var vm))
            {
                vm = new KeyboardItemViewModel(_monitor.Config, scan.Group.GroupKey, _layouts);
                vm.LayoutChanged += OnKeyboardLayoutChanged;
                _keyboards.Add(vm);
            }

            vm.RawDevicePath = scan.Group.RepresentativePath;
            vm.RawPaths = scan.Group.RawPaths;
            vm.AutoName = scan.Name;
            vm.BusType = scan.Bus;
            vm.RemapRuleCount = _monitor.Config.GetRemapRuleCount(vm.GroupKey);
            vm.IsActive = string.Equals(vm.GroupKey, active, StringComparison.OrdinalIgnoreCase);
            connected.Add(vm.GroupKey);
        }

        for (int i = _keyboards.Count - 1; i >= 0; i--)
        {
            if (connected.Contains(_keyboards[i].GroupKey))
                continue;
            _keyboards[i].LayoutChanged -= OnKeyboardLayoutChanged;
            _keyboards.RemoveAt(i);
        }

        SortKeyboards();
        txtEmpty.Text = "Nenhum teclado encontrado. Conecte um teclado e ele aparece aqui.";
        txtEmpty.Visibility = _keyboards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RefreshSaved(connected);
        UpdateLiveStrip();
    }

    private void SortKeyboards()
    {
        var ordered = _keyboards
            .OrderBy(k => k.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        for (int i = 0; i < ordered.Count; i++)
        {
            int current = _keyboards.IndexOf(ordered[i]);
            if (current != i)
                _keyboards.Move(current, i);
        }
    }

    private void RefreshSaved(IReadOnlySet<string>? connected = null)
    {
        connected ??= _keyboards.Select(k => k.GroupKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var config = _monitor.Config;

        _saved.Clear();
        foreach (string key in config.GetConfiguredGroupKeys().Where(k => !connected.Contains(k)))
        {
            string name = config.GetDeviceAlias(key) ?? DeviceNameResolver.GetFallbackName(key);
            string? hkl = config.GetLayoutForDevice(key);
            string layout = string.IsNullOrEmpty(hkl) ? "sem layout" : LayoutTitle(hkl);
            int rules = config.GetRemapRuleCount(key);
            string rulesText = rules switch { 0 => "sem teclas personalizadas", 1 => "1 tecla personalizada", _ => $"{rules} teclas personalizadas" };

            _saved.Add(new SavedKeyboardViewModel
            {
                GroupKey = key,
                DisplayName = name,
                Summary = $"Layout: {layout} · {rulesText}"
            });
        }

        savedSection.Visibility = _saved.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        txtSavedHeader.Text = $"Teclados salvos ({_saved.Count})";
    }

    private void OnDevicesChanged() => _ = RefreshKeyboardsAsync();

    private void OnActiveKeyboardChanged(string groupKey)
    {
        KeyboardItemViewModel? active = null;
        foreach (var vm in _keyboards)
        {
            vm.IsActive = string.Equals(vm.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase);
            if (vm.IsActive)
                active = vm;
        }

        UpdateLiveStrip();
        PulseLiveDot();

        if (active == null)
        {
            _ = RefreshKeyboardsAsync();
            return;
        }

        if (IsVisible && lstKeyboards.ItemContainerGenerator.ContainerFromItem(active) is FrameworkElement container)
            container.BringIntoView();
    }

    private void OnKeyboardLayoutChanged(KeyboardItemViewModel vm)
    {
        UpdateLiveStrip();
        if (onboarding.Visibility == Visibility.Visible && vm.HasLayout)
        {
            _monitor.Config.DismissOnboarding();
            onboarding.Visibility = Visibility.Collapsed;
        }
    }

    // ══════════════════════════════════════
    // Layouts
    // ══════════════════════════════════════

    private void ReloadLayouts(bool force)
    {
        var config = _monitor.Config;
        var installed = KeyboardLayoutCatalog.GetInstalledLayouts();
        var installedSet = installed.Select(i => i.Hkl).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = config.GetAllMappings().Values
            .Where(h => !string.IsNullOrEmpty(h) && !installedSet.Contains(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var aliases = config.GetAllLayoutAliases()
            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .Select(k => $"{k.Key}={k.Value}");

        string signature = string.Join("|", installedSet.OrderBy(h => h)) + "#"
            + string.Join("|", missing) + "#" + string.Join("|", aliases);
        if (!force && signature == _layoutSignature)
            return;
        _layoutSignature = signature;

        var options = new List<LayoutOption> { LayoutOption.None() };
        options.AddRange(installed.Select(info => LayoutOption.From(info, config.GetLayoutAlias(info.Hkl))));
        options.AddRange(missing.Select(h => LayoutOption.From(KeyboardLayoutCatalog.Describe(h), config.GetLayoutAlias(h))));

        LayoutOption.IsRefreshing = true;
        try
        {
            _layouts.Clear();
            foreach (var option in options)
                _layouts.Add(option);
        }
        finally
        {
            LayoutOption.IsRefreshing = false;
        }

        foreach (var vm in _keyboards)
            vm.RefreshSelection();

        UpdateLiveStrip();
    }

    private string LayoutTitle(string hkl)
    {
        var option = _layouts.FirstOrDefault(o => o.Hkl.Equals(hkl, StringComparison.OrdinalIgnoreCase));
        return option?.Title ?? KeyboardLayoutCatalog.Describe(hkl).LayoutName;
    }

    private string LayoutDescription(string hkl)
    {
        var option = _layouts.FirstOrDefault(o => o.Hkl.Equals(hkl, StringComparison.OrdinalIgnoreCase));
        if (option == null)
        {
            var info = KeyboardLayoutCatalog.Describe(hkl);
            return $"{info.LayoutName} · {info.LanguageName}";
        }
        return string.IsNullOrEmpty(option.Subtitle) ? option.Title : $"{option.Title} · {option.Subtitle}";
    }

    // ══════════════════════════════════════
    // Faixa "digitando agora"
    // ══════════════════════════════════════

    private void UpdateLiveStrip()
    {
        var active = _keyboards.FirstOrDefault(k => k.IsActive);
        if (active == null)
        {
            txtLiveTitle.Text = "Aperte qualquer tecla em um teclado para identificá-lo";
            txtLiveSubtitle.Text = "O teclado que você usar acende na lista abaixo, e o KeyNexus aplica o layout dele.";
            return;
        }

        txtLiveTitle.Text = $"Você está digitando no {active.DisplayName}";
        string subtitle = active.HasLayout
            ? $"Layout: {LayoutDescription(active.SelectedLayoutHkl)}"
            : "Sem layout definido: vale o que estiver ativo no Windows. Escolha um abaixo.";
        txtLiveSubtitle.Text = _monitor.IsPaused ? $"{subtitle} (KeyNexus pausado)" : subtitle;
    }

    private void PulseLiveDot()
    {
        var pulse = new DoubleAnimation(0.9, 0.25, TimeSpan.FromMilliseconds(700))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        liveHalo.BeginAnimation(OpacityProperty, pulse);
    }

    // ══════════════════════════════════════
    // Ações dos cards
    // ══════════════════════════════════════

    private void BtnRename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: KeyboardItemViewModel vm })
            vm.BeginRename();
    }

    private void NameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox { IsVisible: true } box)
        {
            box.Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                Keyboard.Focus(box);
                box.SelectAll();
            }, DispatcherPriority.Input);
        }
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: KeyboardItemViewModel vm })
            return;

        if (e.Key == Key.Enter)
        {
            CommitRename(vm);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelRename();
            e.Handled = true;
        }
    }

    private void NameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: KeyboardItemViewModel vm })
            CommitRename(vm);
    }

    private void CommitRename(KeyboardItemViewModel vm)
    {
        if (!vm.IsEditingName)
            return;

        vm.CommitRename();
        SortKeyboards();
        UpdateLiveStrip();
    }

    private void BtnMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: KeyboardItemViewModel vm } button)
            return;

        var menu = new ContextMenu
        {
            PlacementTarget = button,
            Placement = PlacementMode.Bottom
        };
        menu.Items.Add(MenuAction("Renomear", "\uE70F", vm.BeginRename));
        menu.Items.Add(MenuAction("Detalhes técnicos", "\uE946", () => ShowDeviceInfo(vm)));
        menu.Items.Add(MenuAction("Esquecer este teclado", "\uE74D", () => ForgetKeyboard(vm.GroupKey, vm.DisplayName)));
        menu.IsOpen = true;
    }

    private static MenuItem MenuAction(string header, string glyph, Action action)
    {
        var item = new MenuItem { Header = header, Tag = glyph };
        item.Click += (_, _) => action();
        return item;
    }

    private void ShowDeviceInfo(KeyboardItemViewModel vm)
    {
        var info = new DeviceInfoWindow(vm, _monitor.Config) { Owner = this };
        info.ShowDialog();
    }

    private void ForgetKeyboard(string groupKey, string displayName)
    {
        var answer = MessageBox.Show(
            $"Esquecer \"{displayName}\"?\n\nO apelido, o layout e as teclas personalizadas deste teclado serão apagados.",
            "KeyNexus", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        _monitor.Config.RemoveDevice(groupKey);

        var vm = _keyboards.FirstOrDefault(k => k.GroupKey.Equals(groupKey, StringComparison.OrdinalIgnoreCase));
        if (vm != null)
        {
            vm.LayoutChanged -= OnKeyboardLayoutChanged;
            _keyboards.Remove(vm);
        }

        _ = RefreshKeyboardsAsync();
    }

    private void BtnForgetSaved_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SavedKeyboardViewModel saved })
            ForgetKeyboard(saved.GroupKey, saved.DisplayName);
    }

    private void BtnLayoutAlias_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: KeyboardItemViewModel vm } || !vm.HasLayout)
            return;

        string hkl = vm.SelectedLayoutHkl;
        var info = KeyboardLayoutCatalog.Describe(hkl);
        string current = _monitor.Config.GetLayoutAlias(hkl) ?? string.Empty;

        string? result = TextPromptWindow.Prompt(
            this,
            "Apelido do layout",
            $"Dê um nome fácil de reconhecer para \"{info.LayoutName}\" ({info.LanguageName}). " +
            "O nome do Windows continua aparecendo embaixo.",
            current,
            info.LayoutName,
            allowClear: current.Length > 0);

        if (result == null)
            return;

        _monitor.Config.SetLayoutAlias(hkl, result);
        ReloadLayouts(force: true);
        RefreshSaved();
    }

    private void BtnRemap_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: KeyboardItemViewModel vm })
            return;

        var editor = new RemapEditorWindow(vm.GroupKey, vm.DisplayName, _monitor) { Owner = this };
        editor.ShowDialog();
        vm.RemapRuleCount = _monitor.Config.GetRemapRuleCount(vm.GroupKey);
    }

    // ══════════════════════════════════════
    // Configurações
    // ══════════════════════════════════════

    private void BtnSettings_Click(object sender, RoutedEventArgs e) => settingsPopup.IsOpen = true;

    private void ChkAutoStart_Click(object sender, RoutedEventArgs e)
    {
        #pragma warning disable CA1416
        ConfigManager.SetAutoStart(chkAutoStart.IsChecked == true);
        #pragma warning restore CA1416
    }

    private void ChkPaused_Click(object sender, RoutedEventArgs e) =>
        _monitor.SetPaused(chkPaused.IsChecked == true);

    private void BtnResume_Click(object sender, RoutedEventArgs e) => _monitor.SetPaused(false);

    private void OnPausedChanged(bool paused)
    {
        chkPaused.IsChecked = paused;
        UpdatePausedBanner();
        UpdateLiveStrip();
    }

    private void UpdatePausedBanner() =>
        pausedBanner.Visibility = _monitor.IsPaused ? Visibility.Visible : Visibility.Collapsed;

    private void BtnDismissOnboarding_Click(object sender, RoutedEventArgs e)
    {
        _monitor.Config.DismissOnboarding();
        onboarding.Visibility = Visibility.Collapsed;
    }

    private void BtnAddLayout_Click(object sender, RoutedEventArgs e)
    {
        settingsPopup.IsOpen = false;
        TryStart("ms-settings:regionlanguage");
    }

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
    {
        settingsPopup.IsOpen = false;
        TryStart(Logger.LogDirectory);
    }

    private void BtnReleaseKeys_Click(object sender, RoutedEventArgs e)
    {
        _monitor.ReleaseStuckKeys();
        ShowSettingsStatus("Pronto: Ctrl, Alt, Shift e Win foram soltos.", success: true);
    }

    private static void TryStart(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error($"Falha ao abrir {target}", ex);
        }
    }

    private void ShowSettingsStatus(string text, bool success)
    {
        txtSettingsStatus.Text = text;
        txtSettingsStatus.Foreground = (System.Windows.Media.Brush)FindResource(success ? "SuccessBrush" : "WarningBrush");
        txtSettingsStatus.Visibility = Visibility.Visible;
    }
}
