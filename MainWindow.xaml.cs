using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using KeyNexus.Core;

namespace KeyNexus;

public partial class MainWindow : Window
{
    private ObservableCollection<KeyboardItem> _keyboardItems = new();
    private List<KeyboardLayoutItem> _availableLayouts = new();
    private DeviceMonitor _monitor = null!;

    public MainWindow()
    {
        InitializeComponent();

        _monitor = ((App)System.Windows.Application.Current).Monitor;

        LoadInstalledLayouts();
        lstKeyboards.ItemsSource = _keyboardItems;
        RefreshKeyboardsList();

        #pragma warning disable CA1416
        chkAutoStart.IsChecked = ConfigManager.IsAutoStartEnabled();
        #pragma warning restore CA1416

        _monitor.OnActiveDeviceChanged += Monitor_OnActiveDeviceChanged;
        _monitor.OnDevicesChanged += Monitor_OnDevicesChanged;
    }

    // ══════════════════════════════════════
    // Eventos do DeviceMonitor
    // ══════════════════════════════════════
    private void Monitor_OnActiveDeviceChanged(string rawPath, string friendlyName, string? layoutHkl)
    {
        Dispatcher.BeginInvoke(() =>
        {
            string alias = _monitor.Config.GetDeviceAlias(rawPath) ?? friendlyName;
            txtPreviewDevice.Text = $"Dispositivo: {alias}";
            txtPreviewLayout.Text = layoutHkl != null
                ? $"Layout alvo: {ResolveLayoutDisplayName(layoutHkl)}"
                : "Layout alvo: Nenhum layout vinculado";
            txtStatus.Text = $"Último teclado ativo: {alias}";
        });
    }

    private void Monitor_OnDevicesChanged()
    {
        Dispatcher.BeginInvoke(() => RefreshKeyboardsList());
    }

    protected override void OnClosed(EventArgs e)
    {
        _monitor.OnActiveDeviceChanged -= Monitor_OnActiveDeviceChanged;
        _monitor.OnDevicesChanged -= Monitor_OnDevicesChanged;
        base.OnClosed(e);
    }

    // ══════════════════════════════════════
    // Layouts do Windows
    // ══════════════════════════════════════
    private void LoadInstalledLayouts()
    {
        _availableLayouts.Clear();

        _availableLayouts.Add(new KeyboardLayoutItem
        {
            Hkl = string.Empty,
            Name = "— Nenhum (Desvincular) —"
        });

        int count = NativeMethods.GetKeyboardLayoutList(0, null!);
        if (count > 0)
        {
            IntPtr[] hkls = new IntPtr[count];
            NativeMethods.GetKeyboardLayoutList(count, hkls);

            foreach (var hkl in hkls)
            {
                long hkl64 = hkl.ToInt64();
                string hexHkl = LayoutNameResolver.GetHexHkl(hkl64);
                string systemName = LayoutNameResolver.GetLayoutName(hkl64);

                // Usa apelido do usuário se existir
                string? userAlias = _monitor.Config.GetLayoutAlias(hexHkl);
                string displayName = userAlias != null
                    ? $"✎ {userAlias}  ({hexHkl})"
                    : $"{systemName}  ({hexHkl})";

                _availableLayouts.Add(new KeyboardLayoutItem { Hkl = hexHkl, Name = displayName });
            }
        }
    }

    private string ResolveLayoutDisplayName(string hexHkl)
    {
        foreach (var l in _availableLayouts)
            if (l.Hkl.Equals(hexHkl, StringComparison.OrdinalIgnoreCase))
                return l.Name;
        return hexHkl;
    }

    // ══════════════════════════════════════
    // Teclados Conectados
    // ══════════════════════════════════════
    private void RefreshKeyboardsList()
    {
        _keyboardItems.Clear();

        var devices = _monitor.GetConnectedKeyboardsNames();

        foreach (var rawPath in devices)
        {
            if (rawPath.Contains("RDP") || string.IsNullOrWhiteSpace(rawPath))
                continue;

            string friendlyName = DeviceNameResolver.GetFriendlyName(rawPath);
            string shortId = DeviceNameResolver.GetShortId(rawPath);
            string? savedHkl = _monitor.Config.GetLayoutForDevice(rawPath);
            string? savedAlias = _monitor.Config.GetDeviceAlias(rawPath);

            _keyboardItems.Add(new KeyboardItem(_monitor)
            {
                RawDevicePath = rawPath,
                DisplayName = friendlyName,
                ShortId = shortId,
                Alias = savedAlias ?? "Sem apelido",
                AvailableLayouts = _availableLayouts,
                SelectedLayoutHkl = savedHkl
            });
        }
    }

    // ══════════════════════════════════════
    // Eventos da UI
    // ══════════════════════════════════════
    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoadInstalledLayouts();
        RefreshKeyboardsList();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ChkAutoStart_Click(object sender, RoutedEventArgs e)
    {
        #pragma warning disable CA1416
        ConfigManager.SetAutoStart(chkAutoStart.IsChecked == true);
        #pragma warning restore CA1416
    }

    private void LayoutComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox cb && cb.DataContext is KeyboardItem item)
            item.SaveLayout();
    }

    private void AliasTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox tb && tb.DataContext is KeyboardItem item)
            item.SaveAlias();
    }
}

// ══════════════════════════════════════
// View Models
// ══════════════════════════════════════
public class KeyboardLayoutItem
{
    public string Name { get; set; } = string.Empty;
    public string Hkl { get; set; } = string.Empty;
}

public class KeyboardItem : INotifyPropertyChanged
{
    private readonly DeviceMonitor _monitor;
    private string? _selectedLayoutHkl;
    private string _alias = "";

    public KeyboardItem(DeviceMonitor monitor) => _monitor = monitor;

    public string RawDevicePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string ShortId { get; set; } = string.Empty;
    public List<KeyboardLayoutItem> AvailableLayouts { get; set; } = new();

    public string Alias
    {
        get => _alias;
        set { if (_alias != value) { _alias = value; OnPropertyChanged(nameof(Alias)); } }
    }

    public string? SelectedLayoutHkl
    {
        get => _selectedLayoutHkl;
        set { if (_selectedLayoutHkl != value) { _selectedLayoutHkl = value; OnPropertyChanged(nameof(SelectedLayoutHkl)); } }
    }

    public void SaveLayout()
    {
        if (!string.IsNullOrEmpty(RawDevicePath))
            _monitor.Config.SetLayoutForDevice(RawDevicePath, SelectedLayoutHkl ?? string.Empty);
    }

    public void SaveAlias()
    {
        if (!string.IsNullOrEmpty(RawDevicePath))
        {
            string trimmed = Alias?.Trim() ?? "";
            if (trimmed == "Sem apelido" || trimmed == "")
                _monitor.Config.SetDeviceAlias(RawDevicePath, "");
            else
                _monitor.Config.SetDeviceAlias(RawDevicePath, trimmed);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
