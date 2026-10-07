using System;
using System.Threading.Tasks;
using System.Windows;
using KeyNexus.Core;
using KeyNexus.Themes;
using KeyNexus.ViewModels;
using MessageBox = System.Windows.MessageBox;

namespace KeyNexus;

public partial class DeviceInfoWindow : Window
{
    private readonly KeyboardItemViewModel _item;
    private readonly ConfigManager _config;

    public DeviceInfoWindow(KeyboardItemViewModel item, ConfigManager config)
    {
        _item = item;
        _config = config;

        InitializeComponent();
        WindowEffects.Apply(this, useMica: false);

        txtTitle.Text = item.DisplayName;
        Loaded += DeviceInfoWindow_Loaded;
    }

    private async void DeviceInfoWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DeviceInfoWindow_Loaded;

        try
        {
            var report = await Task.Run(() => DeviceInfoCollector.Collect(
                _item.GroupKey,
                _item.DisplayName,
                _item.RawDevicePath,
                _item.RawPaths,
                _config));

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
