using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using KeyNexus.Core;

namespace KeyNexus.ViewModels;

public sealed class KeyboardItemViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private string _autoName = "Teclado";
    private string? _alias;
    private KeyboardBusType _busType;
    private string _selectedLayoutHkl;
    private int _remapRuleCount;
    private bool _isActive;
    private bool _isEditingName;
    private string _editName = string.Empty;

    public KeyboardItemViewModel(ConfigManager config, string groupKey, ObservableCollection<LayoutOption> layouts)
    {
        _config = config;
        GroupKey = groupKey;
        AvailableLayouts = layouts;
        _alias = config.GetDeviceAlias(groupKey);
        _selectedLayoutHkl = config.GetLayoutForDevice(groupKey) ?? string.Empty;
        _remapRuleCount = config.GetRemapRuleCount(groupKey);
    }

    /// <summary>O usuário escolheu outro layout para este teclado.</summary>
    public event Action<KeyboardItemViewModel>? LayoutChanged;

    public string GroupKey { get; }
    public string RawDevicePath { get; set; } = string.Empty;
    public List<string> RawPaths { get; set; } = new();
    public ObservableCollection<LayoutOption> AvailableLayouts { get; }

    public string AutoName
    {
        get => _autoName;
        set
        {
            if (Set(ref _autoName, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public KeyboardBusType BusType
    {
        get => _busType;
        set
        {
            if (Set(ref _busType, value))
            {
                OnPropertyChanged(nameof(TypeLabel));
                OnPropertyChanged(nameof(TypeGlyph));
                OnPropertyChanged(nameof(Subtitle));
            }
        }
    }

    public string? Alias => _alias;

    public string DisplayName => string.IsNullOrWhiteSpace(_alias) ? _autoName : _alias!;

    public string TypeLabel => DeviceNameResolver.GetTypeLabel(_busType);

    public string TypeGlyph => _busType switch
    {
        KeyboardBusType.BluetoothHid or KeyboardBusType.BluetoothLeHid => "\uE702",
        KeyboardBusType.UsbHid => "\uE88E",
        KeyboardBusType.Acpi => "\uE7F8",
        _ => "\uE765"
    };

    /// <summary>Tipo de conexão; com apelido, mostra também o nome que o Windows conhece.</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(_alias) ? TypeLabel : $"{TypeLabel} · {_autoName}";

    public string SelectedLayoutHkl
    {
        get => _selectedLayoutHkl;
        set
        {
            if (value is null || LayoutOption.IsRefreshing)
                return;

            if (Set(ref _selectedLayoutHkl, value))
            {
                _config.SetLayoutForDevice(GroupKey, value);
                OnPropertyChanged(nameof(HasLayout));
                LayoutChanged?.Invoke(this);
            }
        }
    }

    public bool HasLayout => !string.IsNullOrEmpty(_selectedLayoutHkl);

    public int RemapRuleCount
    {
        get => _remapRuleCount;
        set
        {
            if (Set(ref _remapRuleCount, value))
                OnPropertyChanged(nameof(RemapSummary));
        }
    }

    public string RemapSummary => _remapRuleCount switch
    {
        0 => "Nenhuma",
        1 => "1 tecla",
        _ => $"{_remapRuleCount} teclas"
    };

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public bool IsEditingName
    {
        get => _isEditingName;
        private set
        {
            if (Set(ref _isEditingName, value))
                OnPropertyChanged(nameof(IsNotEditingName));
        }
    }

    public bool IsNotEditingName => !_isEditingName;

    public string EditName
    {
        get => _editName;
        set => Set(ref _editName, value ?? string.Empty);
    }

    public void BeginRename()
    {
        EditName = DisplayName;
        IsEditingName = true;
    }

    public void CommitRename()
    {
        if (!IsEditingName)
            return;

        string name = EditName.Trim();
        string? alias = name.Length == 0 || string.Equals(name, _autoName, StringComparison.Ordinal) ? null : name;
        _config.SetDeviceAlias(GroupKey, alias ?? string.Empty);
        _alias = alias;
        IsEditingName = false;
        OnPropertyChanged(nameof(Alias));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Subtitle));
    }

    public void CancelRename() => IsEditingName = false;

    /// <summary>Reaplica a seleção depois que a lista de layouts foi reconstruída.</summary>
    public void RefreshSelection() => OnPropertyChanged(nameof(SelectedLayoutHkl));
}
