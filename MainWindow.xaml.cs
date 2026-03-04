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

            foreach (var hkl in hkls)
            {
                long hkl64 = hkl.ToInt64();
                // Isola os 32 bits inferiores para evitar FFFFFFFF no começo
                string hexHkl = (hkl64 & 0xFFFFFFFF).ToString("X8");
                
                string layoutName;
                try 
                {
                    // O LCID está nos 16 bits mais baixos do HKL
                    int lcid = (int)(hkl64 & 0xFFFF);
                    var culture = System.Globalization.CultureInfo.GetCultureInfo(lcid);
                    
                    // Nomes conhecidos para melhorar a experiência do usuário baseado nos layouts da sua print
                    string specificName = hexHkl switch {
                        "F0010416" => "Estados Unidos (internacional)",
                        "04160416" => "Brasil ABNT2",
                        "08160416" => "Portugal",
                        "04160816" => "Brasil ABNT",
                        "00000409" => "EUA (Padrão)",
                        _ => "Desconhecido"
                    };

                    if (specificName != "Desconhecido") {
                        layoutName = $"{culture.NativeName} - {specificName} (HKL: {hexHkl})";
                    } else {
                        // Tenta buscar no registro do Windows o nome oficial
                        #pragma warning disable CA1416
                        string? regName = Microsoft.Win32.Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{hexHkl}", "Layout Text", null) as string;
                        if (string.IsNullOrEmpty(regName)) {
                            // Tenta com zero-pad no LCID caso o HKL primario falhe
                            string lowLcid = (hkl64 & 0xFFFF).ToString("X8");
                            regName = Microsoft.Win32.Registry.GetValue($@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{lowLcid}", "Layout Text", null) as string;
                        }
                        #pragma warning restore CA1416
                        
                        if (!string.IsNullOrEmpty(regName)) {
                            layoutName = $"{culture.NativeName} - {regName} (HKL: {hexHkl})";
                        } else {
                            layoutName = $"{culture.NativeName} (HKL: {hexHkl})";
                        }
                    }
                }
                catch 
                {
                    layoutName = $"Layout HKL: {hexHkl}";
                }

                _availableLayouts.Add(new KeyboardLayoutItem { Hkl = hexHkl, Name = layoutName });
            }
            
            // Adicionar a opção de desvincular
            _availableLayouts.Insert(0, new KeyboardLayoutItem { Hkl = string.Empty, Name = "-- Nenhum (Desvincular Layout) --" });
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
        if (!string.IsNullOrEmpty(DeviceName))
        {
             _monitor.Config.SetLayoutForDevice(DeviceName, SelectedLayoutHkl ?? string.Empty);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
