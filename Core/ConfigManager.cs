using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using KeyNexus.Core.Profiles;

namespace KeyNexus.Core;

public class ConfigManager
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyNexus");
    private static readonly string ConfigFile = Path.Combine(ConfigDir, "keynexus_config.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private const int CurrentSchemaVersion = 2;
    private const int SaveDelayMs = 400;

    private ConcurrentDictionary<string, string> _deviceLayouts;
    private ConcurrentDictionary<string, string> _deviceAliases;
    private ConcurrentDictionary<string, string> _layoutAliases;
    private ConcurrentDictionary<string, List<RemapRule>> _deviceRemaps;
    private bool _onboardingDismissed;

    private ProfileSnapshot _snapshot = ProfileSnapshot.Empty;
    private readonly object _saveLock = new();
    private readonly System.Threading.Timer _saveTimer;
    private bool _savePending;

    public ConfigManager()
    {
        _deviceLayouts = new ConcurrentDictionary<string, string>();
        _deviceAliases = new ConcurrentDictionary<string, string>();
        _layoutAliases = new ConcurrentDictionary<string, string>();
        _deviceRemaps = new ConcurrentDictionary<string, List<RemapRule>>();
        _saveTimer = new System.Threading.Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);

        Directory.CreateDirectory(ConfigDir);
        IsFirstRun = !File.Exists(ConfigFile);
        LoadConfig();
        RebuildSnapshot();
    }

    /// <summary>Não havia arquivo de configuração quando o app abriu.</summary>
    public bool IsFirstRun { get; }

    public bool ShouldShowOnboarding => !_onboardingDismissed && _deviceLayouts.IsEmpty;

    /// <summary>Perfis compilados que o hook lê sem alocar.</summary>
    internal ProfileSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>Layouts ou regras mudaram e o snapshot foi republicado.</summary>
    internal event Action? SnapshotChanged;

    private static string NormalizeKey(string deviceName)
        => DeviceGrouping.GetGroupKey(deviceName);

    public void SetLayoutForDevice(string deviceName, string layoutHkl)
    {
        string key = NormalizeKey(deviceName);
        if (string.IsNullOrEmpty(layoutHkl))
        {
            if (!_deviceLayouts.TryRemove(key, out _))
                return;
            Logger.Info($"Layout desvinculado: {key}");
        }
        else
        {
            if (_deviceLayouts.TryGetValue(key, out var current)
                && string.Equals(current, layoutHkl, StringComparison.OrdinalIgnoreCase))
                return;
            _deviceLayouts[key] = layoutHkl;
            Logger.Info($"Layout vinculado: {key} → {layoutHkl}");
        }
        RebuildSnapshot();
        ScheduleSave();
    }

    public string? GetLayoutForDevice(string deviceName)
    {
        string key = NormalizeKey(deviceName);
        if (_deviceLayouts.TryGetValue(key, out var hkl))
            return hkl;

        // Fallback: chave legada (caminho cru antes da migração)
        if (!key.Equals(deviceName, StringComparison.OrdinalIgnoreCase)
            && _deviceLayouts.TryGetValue(deviceName, out hkl))
            return hkl;

        return null;
    }

    public IReadOnlyDictionary<string, string> GetAllMappings() => _deviceLayouts;

    public void SetDeviceAlias(string deviceName, string alias)
    {
        string key = NormalizeKey(deviceName);
        if (string.IsNullOrWhiteSpace(alias))
            _deviceAliases.TryRemove(key, out _);
        else
            _deviceAliases[key] = alias.Trim();
        ScheduleSave();
    }

    public string? GetDeviceAlias(string deviceName)
    {
        string key = NormalizeKey(deviceName);
        if (_deviceAliases.TryGetValue(key, out var alias))
            return alias;

        if (!key.Equals(deviceName, StringComparison.OrdinalIgnoreCase)
            && _deviceAliases.TryGetValue(deviceName, out alias))
            return alias;

        return null;
    }

    public void SetLayoutAlias(string hkl, string alias)
    {
        if (string.IsNullOrEmpty(hkl))
            return;

        if (string.IsNullOrWhiteSpace(alias))
            _layoutAliases.TryRemove(hkl, out _);
        else
            _layoutAliases[hkl] = alias.Trim();
        ScheduleSave();
    }

    public string? GetLayoutAlias(string hkl)
        => !string.IsNullOrEmpty(hkl) && _layoutAliases.TryGetValue(hkl, out var alias) ? alias : null;

    public IReadOnlyDictionary<string, string> GetAllLayoutAliases() => _layoutAliases;

    public List<RemapRule> GetRemapRules(string deviceName)
    {
        string key = NormalizeKey(deviceName);
        if (_deviceRemaps.TryGetValue(key, out var rules))
            return rules;

        if (!key.Equals(deviceName, StringComparison.OrdinalIgnoreCase)
            && _deviceRemaps.TryGetValue(deviceName, out rules))
            return rules;

        return new List<RemapRule>();
    }

    public void SetRemapRules(string deviceName, List<RemapRule> rules)
    {
        string key = NormalizeKey(deviceName);
        if (rules == null || rules.Count == 0)
            _deviceRemaps.TryRemove(key, out _);
        else
            _deviceRemaps[key] = rules;
        RebuildSnapshot();
        ScheduleSave();
    }

    public int GetRemapRuleCount(string deviceName) => GetRemapRules(deviceName).Count;

    /// <summary>Teclados com alguma configuração salva, conectados ou não.</summary>
    public IReadOnlyCollection<string> GetConfiguredGroupKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        keys.UnionWith(_deviceLayouts.Keys);
        keys.UnionWith(_deviceAliases.Keys);
        keys.UnionWith(_deviceRemaps.Keys);
        return keys;
    }

    /// <summary>Esquece apelido, layout e regras de um teclado.</summary>
    public void RemoveDevice(string groupKey)
    {
        string key = NormalizeKey(groupKey);
        bool changed = _deviceLayouts.TryRemove(key, out _);
        changed |= _deviceAliases.TryRemove(key, out _);
        changed |= _deviceRemaps.TryRemove(key, out _);
        if (!changed)
            return;

        Logger.Info($"Configuração do teclado removida: {key}");
        RebuildSnapshot();
        ScheduleSave();
    }

    public void DismissOnboarding()
    {
        if (_onboardingDismissed)
            return;
        _onboardingDismissed = true;
        ScheduleSave();
    }

    /// <summary>Grava agora o que estiver pendente (chamado ao sair).</summary>
    public void Flush()
    {
        bool pending;
        lock (_saveLock)
        {
            pending = _savePending;
            _saveTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        if (pending)
            SaveNow();
    }

    private void RebuildSnapshot()
    {
        try
        {
            var snapshot = ProfileCompiler.Compile(_deviceLayouts, _deviceRemaps);
            Volatile.Write(ref _snapshot, snapshot);
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao compilar perfis de teclado", ex);
            return;
        }

        SnapshotChanged?.Invoke();
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigFile))
                return;

            var json = File.ReadAllText(ConfigFile);
            var root = JsonSerializer.Deserialize<ConfigData>(json);
            if (root == null)
                return;

            _deviceLayouts = new ConcurrentDictionary<string, string>(root.DeviceLayouts ?? new());
            _deviceAliases = new ConcurrentDictionary<string, string>(root.DeviceAliases ?? new());
            _layoutAliases = new ConcurrentDictionary<string, string>(root.LayoutAliases ?? new());
            _deviceRemaps = new ConcurrentDictionary<string, List<RemapRule>>(root.DeviceRemaps ?? new());
            _onboardingDismissed = root.OnboardingDismissed;

            bool changed = MigrateLegacyKeys();
            if (root.SchemaVersion < CurrentSchemaVersion)
                changed = true;
            if (changed)
                ScheduleSave();

            Logger.Info($"Configuração carregada: {_deviceLayouts.Count} layouts, {_deviceAliases.Count} apelidos, {_deviceRemaps.Count} remaps");
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao carregar configuração", ex);
        }
    }

    private bool MigrateLegacyKeys()
    {
        bool changed = false;

        changed |= MigrateDictionary(_deviceLayouts);
        changed |= MigrateDictionary(_deviceAliases);

        foreach (var kvp in _deviceRemaps.ToArray())
        {
            string newKey = DeviceGrouping.GetGroupKey(kvp.Key);
            if (string.IsNullOrEmpty(newKey) || newKey.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!_deviceRemaps.ContainsKey(newKey))
                _deviceRemaps[newKey] = kvp.Value;
            _deviceRemaps.TryRemove(kvp.Key, out _);
            changed = true;
        }

        return changed;
    }

    private static bool MigrateDictionary(ConcurrentDictionary<string, string> dict)
    {
        bool changed = false;
        foreach (var kvp in dict.ToArray())
        {
            string newKey = DeviceGrouping.GetGroupKey(kvp.Key);
            if (string.IsNullOrEmpty(newKey) || newKey.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!dict.ContainsKey(newKey))
                dict[newKey] = kvp.Value;
            dict.TryRemove(kvp.Key, out _);
            changed = true;
        }
        return changed;
    }

    private void ScheduleSave()
    {
        lock (_saveLock)
        {
            _savePending = true;
            _saveTimer.Change(SaveDelayMs, Timeout.Infinite);
        }
    }

    private void SaveNow()
    {
        lock (_saveLock)
        {
            _savePending = false;
            try
            {
                var data = new ConfigData
                {
                    SchemaVersion = CurrentSchemaVersion,
                    OnboardingDismissed = _onboardingDismissed,
                    DeviceLayouts = new Dictionary<string, string>(_deviceLayouts),
                    DeviceAliases = new Dictionary<string, string>(_deviceAliases),
                    LayoutAliases = new Dictionary<string, string>(_layoutAliases),
                    DeviceRemaps = _deviceRemaps.ToDictionary(k => k.Key, v => v.Value)
                };

                string json = JsonSerializer.Serialize(data, JsonOptions);
                string temp = ConfigFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, ConfigFile, overwrite: true);
            }
            catch (Exception ex)
            {
                Logger.Error("Falha ao salvar configuração", ex);
            }
        }
    }

    private class ConfigData
    {
        public int SchemaVersion { get; set; }
        public bool OnboardingDismissed { get; set; }
        public Dictionary<string, string>? DeviceLayouts { get; set; }
        public Dictionary<string, string>? DeviceAliases { get; set; }
        public Dictionary<string, string>? LayoutAliases { get; set; }
        public Dictionary<string, List<RemapRule>>? DeviceRemaps { get; set; }
    }

    private const string RegistryRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "KeyNexus";

    public static bool IsAutoStartEnabled()
    {
        try
        {
            #pragma warning disable CA1416
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryRunKey, false);
            return key?.GetValue(AppName) != null;
            #pragma warning restore CA1416
        }
        catch { return false; }
    }

    public static void SetAutoStart(bool enabled)
    {
        try
        {
            #pragma warning disable CA1416
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegistryRunKey, true);
            if (key == null) return;

            if (enabled)
            {
                string exePath = Environment.ProcessPath ?? "";
                key.SetValue(AppName, $"\"{exePath}\"");
                Logger.Info("Auto-start habilitado");
            }
            else
            {
                key.DeleteValue(AppName, false);
                Logger.Info("Auto-start desabilitado");
            }
            #pragma warning restore CA1416
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao configurar auto-start", ex);
        }
    }
}
