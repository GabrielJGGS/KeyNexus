using System;
using System.Windows;
using KeyNexus.Core;
using MessageBox = System.Windows.MessageBox;

namespace KeyNexus;

public partial class DeviceInfoWindow : Window
{
    private readonly KeyboardItem _item;
    private readonly ConfigManager _config;

    public DeviceInfoWindow(KeyboardItem item, ConfigManager config)
    {
        _item = item;
        _config = config;

        InitializeComponent();

        string title = item.Alias is "Sem apelido" or "" ? item.DisplayName : item.Alias;
        txtTitle.Text = title;

        Loaded += DeviceInfoWindow_Loaded;
    }

    private void DeviceInfoWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DeviceInfoWindow_Loaded;

        try
        {
            var report = DeviceInfoCollector.Collect(
                _item.GroupKey,
                _item.DisplayName,
                _item.RawDevicePath,
                _item.RawPaths,
                _config);

            lstSections.ItemsSource = report.Sections;
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao abrir detalhes do dispositivo", ex);
            MessageBox.Show(
                $"Não foi possível coletar os detalhes do teclado.\n\n{ex.Message}",
                "KeyNexus",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Close();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
