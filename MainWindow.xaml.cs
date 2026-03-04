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

        // Auto-start checkbox
        #pragma warning disable CA1416
        chkAutoStart.IsChecked = ConfigManager.IsAutoStartEnabled();
        #pragma warning restore CA1416

        // Subscrever eventos
        _monitor.OnActiveDeviceChanged += Monitor_OnActiveDeviceChanged;
        _monitor.OnDevicesChanged += Monitor_OnDevicesChanged;
    }

    // ══════════════════════════════════════
    // Event Handlers do DeviceMonitor
    // ══════════════════════════════════════
    private void Monitor_OnActiveDeviceChanged(string rawPath, string friendlyName, string? layoutHkl)
    {
        Dispatcher.BeginInvoke(() =>
        {
            txtPreviewDevice.Text = $"Dispositivo: {friendlyName}";
            txtPreviewLayout.Text = layoutHkl != null
                ? $"Layout alvo: {ResolveLayoutDisplayName(layoutHkl)}"
                : "Layout alvo: Nenhum layout vinculado";
            txtStatus.Text = $"Último teclado ativo: {friendlyName}";
        });
    }

    private void Monitor_OnDevicesChanged()
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshKeyboardsList();
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _monitor.OnActiveDeviceChanged -= Monitor_OnActiveDeviceChanged;
        _monitor.OnDevicesChanged -= Monitor_OnDevicesChanged;
        base.OnClosed(e);
    }

    // ══════════════════════════════════════
    // Carregamento de Layouts do Windows
    // ══════════════════════════════════════
    private void LoadInstalledLayouts()
    {
        _availableLayouts.Clear();

        // Opção de desvincular sempre no topo
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
                string name = LayoutNameResolver.GetLayoutName(hkl64);

                _availableLayouts.Add(new KeyboardLayoutItem
                {
                    Hkl = hexHkl,
                    Name = $"{name}  ({hexHkl})"
                });
            }
        }
    }

    private string ResolveLayoutDisplayName(string hexHkl)
    {
        foreach (var l in _availableLayouts)
        {
            if (l.Hkl.Equals(hexHkl, StringComparison.OrdinalIgnoreCase))
                return l.Name;
        }
        return hexHkl;
    }

    // ══════════════════════════════════════
    // Carregamento da Lista de Teclados
    // ══════════════════════════════════════
    private void RefreshKeyboardsList()
    {
        _keyboardItems.Clear();

        var devices = _monitor.GetConnectedKeyboardsNames();
        var config = _monitor.Config;

        foreach (var rawPath in devices)
        {
            if (rawPath.Contains("RDP") || string.IsNullOrWhiteSpace(rawPath))
                continue;

            string friendlyName = DeviceNameResolver.GetFriendlyName(rawPath);
            string shortId = DeviceNameResolver.GetShortId(rawPath);
            string? savedHkl = config.GetLayoutForDevice(rawPath);

            _keyboardItems.Add(new KeyboardItem(_monitor)
            {
                RawDevicePath = rawPath,
                DisplayName = $"{friendlyName}  [{shortId}]",
                AvailableLayouts = _availableLayouts,
                SelectedLayoutHkl = savedHkl
            });
        }
    }

    // ══════════════════════════════════════
    // Eventos da UI
    // ══════════════════════════════════════
    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => RefreshKeyboardsList();
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
        {
            item.SaveLayout();
        }
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

    public KeyboardItem(DeviceMonitor monitor) => _monitor = monitor;

    public string RawDevicePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<KeyboardLayoutItem> AvailableLayouts { get; set; } = new();

    public string? SelectedLayoutHkl
    {
        get => _selectedLayoutHkl;
        set
        {
            if (_selectedLayoutHkl != value)
            {
                _selectedLayoutHkl = value;
                OnPropertyChanged(nameof(SelectedLayoutHkl));
            }
        }
    }

    public void SaveLayout()
    {
        if (!string.IsNullOrEmpty(RawDevicePath))
            _monitor.Config.SetLayoutForDevice(RawDevicePath, SelectedLayoutHkl ?? string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
