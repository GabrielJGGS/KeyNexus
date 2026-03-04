using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using KeyNexus.Core;

namespace KeyNexus;

public partial class MainWindow : Window
{
    private ObservableCollection<KeyboardItem> _keyboardItems = new();
    private List<KeyboardLayoutItem> _availableLayouts = new();

    public MainWindow()
    {
        InitializeComponent();
        LoadInstalledLayouts();
        lstKeyboards.ItemsSource = _keyboardItems;
        RefreshKeyboardsList();

        var app = (App)System.Windows.Application.Current;
        var deviceMonitor = (DeviceMonitor)app.GetType().GetField("deviceMonitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(app)!;
        deviceMonitor.OnActiveDeviceChanged += DeviceMonitor_OnActiveDeviceChanged;
    }

    private void DeviceMonitor_OnActiveDeviceChanged(string deviceName, string layout)
    {
        Dispatcher.Invoke(() =>
        {
            txtPreviewStatus.Text = $"Teclado: {deviceName}\nLayout Alvo: {layout}";
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        var app = (App)System.Windows.Application.Current;
        var deviceMonitor = (DeviceMonitor)app.GetType().GetField("deviceMonitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(app)!;
        deviceMonitor.OnActiveDeviceChanged -= DeviceMonitor_OnActiveDeviceChanged;
        base.OnClosed(e);
    }

    private void LoadInstalledLayouts()
    {
        _availableLayouts.Clear();
        int count = NativeMethods.GetKeyboardLayoutList(0, null!);
        if (count > 0)
        {
            IntPtr[] hkls = new IntPtr[count];
            NativeMethods.GetKeyboardLayoutList(count, hkls);

            // Um buffer auxiliar pois o GetKeyboardLayoutName preenche os dados do layout ativo pro thread
            StringBuilder layoutName = new StringBuilder(9); 

            foreach (var hkl in hkls)
            {
                // Embora GetKeyboardLayoutName forneça o código de linguagem (ex: 00000416),
                // para exibir de verdade "Português Brasileiro ABNT2", 
                // precisaríamos acessar o Registry ou usar classes CultureInfo mais avançadas, 
                // mas para simplicidade usaremos o HKL em Hexadecimal para associação direta.
                string hexHkl = hkl.ToInt64().ToString("x8");
                _availableLayouts.Add(new KeyboardLayoutItem { Hkl = hexHkl, Name = $"Layout HKL: {hexHkl.ToUpper()}" });
            }
        }
    }

    private void RefreshKeyboardsList()
    {
        _keyboardItems.Clear();

        // Utilizamos o DeviceMonitor criado via App
        var app = (App)System.Windows.Application.Current;
        var deviceMonitor = (DeviceMonitor)app.GetType().GetField("deviceMonitor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(app)!;

        var devices = deviceMonitor.GetConnectedKeyboardsNames();
        var config = deviceMonitor.Config;

        foreach (var device in devices)
        {
            // Ignorar terminais de root do windows que não configuramos 
            if (device.Contains("RDP") || string.IsNullOrWhiteSpace(device)) continue;

            string? savedHkl = config.GetLayoutForDevice(device);
            var item = new KeyboardItem(deviceMonitor)
            {
                DeviceName = device,
                AvailableLayouts = _availableLayouts,
                SelectedLayoutHkl = savedHkl
            };
            _keyboardItems.Add(item);
        }
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        RefreshKeyboardsList();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        this.Close(); // Apenas esconde/fecha a janela, o App (systray) continua rodando
    }

    private void LayoutComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox cb && cb.DataContext is KeyboardItem item)
        {
            item.SaveLayout();
        }
    }
}

public class KeyboardLayoutItem
{
    public string Name { get; set; } = string.Empty;
    public string Hkl { get; set; } = string.Empty;
}

public class KeyboardItem : INotifyPropertyChanged
{
    private readonly DeviceMonitor _monitor;
    private string? _selectedLayoutHkl;

    public KeyboardItem(DeviceMonitor monitor)
    {
        _monitor = monitor;
    }

    public string DeviceName { get; set; } = string.Empty;
    
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
        if (!string.IsNullOrEmpty(DeviceName) && !string.IsNullOrEmpty(SelectedLayoutHkl))
        {
             _monitor.Config.SetLayoutForDevice(DeviceName, SelectedLayoutHkl);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
