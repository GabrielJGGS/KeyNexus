using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KeyNexus.Core;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using MessageBox = System.Windows.MessageBox;
using TextBox = System.Windows.Controls.TextBox;

namespace KeyNexus;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<KeyboardItem> _keyboardItems = new();
    private readonly List<KeyboardLayoutItem> _availableLayouts = new();
    private readonly DeviceMonitor _monitor;

    public MainWindow()
    {
        InitializeComponent();

        _monitor = ((App)System.Windows.Application.Current).Monitor;

        txtVersion.Text = $"v{UpdateService.GetCurrentVersion()}";

        LoadInstalledLayouts();
        lstKeyboards.ItemsSource = _keyboardItems;
        RefreshKeyboardsList();

        #pragma warning disable CA1416
        chkAutoStart.IsChecked = ConfigManager.IsAutoStartEnabled();
        #pragma warning restore CA1416

        _monitor.OnActiveDeviceChanged += Monitor_OnActiveDeviceChanged;
        _monitor.OnDevicesChanged += Monitor_OnDevicesChanged;

        ShowPendingUpdateIfAny();
    }

    private void ShowPendingUpdateIfAny()
    {
        if (System.Windows.Application.Current is App app && app.PendingUpdate != null)
        {
            txtUpdateStatus.Text = $"Nova versão {app.PendingUpdate.Version} disponível!";
        }
    }

    private void Monitor_OnActiveDeviceChanged(string groupKey, string friendlyName, string? layoutHkl)
    {
        Dispatcher.BeginInvoke(() =>
        {
            txtPreviewDevice.Text = friendlyName;
            txtPreviewLayout.Text = layoutHkl != null
                ? ResolveLayoutDisplayName(layoutHkl)
                : "Nenhum layout vinculado";
            txtStatus.Text = $"Último teclado ativo: {friendlyName}";
        });
    }

    private void Monitor_OnDevicesChanged()
    {
        Dispatcher.BeginInvoke(RefreshKeyboardsList);
    }

    protected override void OnClosed(EventArgs e)
    {
        _monitor.OnActiveDeviceChanged -= Monitor_OnActiveDeviceChanged;
        _monitor.OnDevicesChanged -= Monitor_OnDevicesChanged;
        base.OnClosed(e);
    }

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

    private void RefreshKeyboardsList()
    {
        _keyboardItems.Clear();
        var groups = _monitor.GetConnectedKeyboardGroups();

        foreach (var group in groups)
        {
            string friendlyName = DeviceNameResolver.GetFriendlyName(group.RepresentativePath);
            string shortId = DeviceNameResolver.GetShortId(group.RepresentativePath);
            string? savedHkl = _monitor.Config.GetLayoutForDevice(group.GroupKey);
            string? savedAlias = _monitor.Config.GetDeviceAlias(group.GroupKey);
            int remapCount = _monitor.Config.GetRemapRuleCount(group.GroupKey);

            _keyboardItems.Add(new KeyboardItem(_monitor)
            {
                GroupKey = group.GroupKey,
                RawDevicePath = group.RepresentativePath,
                RawPaths = group.RawPaths,
                DisplayName = friendlyName,
                ShortId = shortId,
                Alias = savedAlias ?? "Sem apelido",
                AvailableLayouts = _availableLayouts,
                SelectedLayoutHkl = savedHkl,
                CollectionCount = group.CollectionCount,
                RemapRuleCount = remapCount
            });
        }
    }

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
        if (sender is ComboBox cb && cb.DataContext is KeyboardItem item)
            item.SaveLayout();
    }

    private void AliasTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is KeyboardItem item)
            item.SaveAlias();
    }

    private void BtnRemap_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is KeyboardItem item)
        {
            string displayName = item.Alias is "Sem apelido" or "" ? item.DisplayName : item.Alias;
            var editor = new RemapEditorWindow(item.GroupKey, displayName, _monitor)
            {
                Owner = this
            };
            if (editor.ShowDialog() == true)
            {
                item.RemapRuleCount = _monitor.Config.GetRemapRuleCount(item.GroupKey);
                item.NotifyRemapChanged();
            }
        }
    }

    private void BtnDeviceInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is KeyboardItem item)
        {
            var info = new DeviceInfoWindow(item, _monitor.Config) { Owner = this };
            info.ShowDialog();
        }
    }

    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        btnCheckUpdate.IsEnabled = false;
        txtUpdateStatus.Text = "Verificando...";

        try
        {
            var info = await UpdateService.CheckForUpdateAsync();
            if (info == null)
            {
                txtUpdateStatus.Text = "Não foi possível verificar.";
                return;
            }

            if (!info.IsNewer)
            {
                txtUpdateStatus.Text = "Você já está na versão mais recente.";
                return;
            }

            var result = MessageBox.Show(
                $"Versão {info.Version} disponível.\n\n{info.ReleaseNotes}\n\nDeseja baixar e instalar agora?",
                "KeyNexus — Atualização",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result != MessageBoxResult.Yes)
            {
                txtUpdateStatus.Text = $"Versão {info.Version} disponível.";
                return;
            }

            txtUpdateStatus.Text = "Baixando...";
            bool success = await UpdateService.DownloadAndApplyUpdateAsync(info);
            if (success)
            {
                MessageBox.Show("Atualização instalada! O aplicativo será reiniciado.",
                    "KeyNexus", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateService.RestartApplication();
            }
            else
            {
                txtUpdateStatus.Text = "Falha ao baixar atualização.";
            }
        }
        finally
        {
            btnCheckUpdate.IsEnabled = true;
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
    private string _alias = "";
    private int _remapRuleCount;

    public KeyboardItem(DeviceMonitor monitor) => _monitor = monitor;

    public string GroupKey { get; set; } = string.Empty;
    public string RawDevicePath { get; set; } = string.Empty;
    public List<string> RawPaths { get; set; } = new();
    public string DisplayName { get; set; } = string.Empty;
    public string ShortId { get; set; } = string.Empty;
    public int CollectionCount { get; set; }
    public List<KeyboardLayoutItem> AvailableLayouts { get; set; } = new();

    public bool HasMultipleCollections => CollectionCount > 1;

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

    public int RemapRuleCount
    {
        get => _remapRuleCount;
        set { if (_remapRuleCount != value) { _remapRuleCount = value; OnPropertyChanged(nameof(RemapRuleCount)); OnPropertyChanged(nameof(RemapSummary)); } }
    }

    public string RemapSummary => RemapRuleCount > 0
        ? $"{RemapRuleCount} regra(s) ativa(s)"
        : "Nenhuma regra";

    public void NotifyRemapChanged()
    {
        OnPropertyChanged(nameof(RemapRuleCount));
        OnPropertyChanged(nameof(RemapSummary));
    }

    public void SaveLayout()
    {
        if (!string.IsNullOrEmpty(GroupKey))
            _monitor.Config.SetLayoutForDevice(GroupKey, SelectedLayoutHkl ?? string.Empty);
    }

    public void SaveAlias()
    {
        if (!string.IsNullOrEmpty(GroupKey))
        {
            string trimmed = Alias?.Trim() ?? "";
            if (trimmed is "Sem apelido" or "")
                _monitor.Config.SetDeviceAlias(GroupKey, "");
            else
                _monitor.Config.SetDeviceAlias(GroupKey, trimmed);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
