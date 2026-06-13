using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KeyNexus.Core;

public class ConfigManager
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KeyNexus");
    private static readonly string ConfigFile = Path.Combine(ConfigDir, "keynexus_config.json");

    private ConcurrentDictionary<string, string> _deviceLayouts;
    private ConcurrentDictionary<string, string> _deviceAliases;
    private ConcurrentDictionary<string, string> _layoutAliases;
    private ConcurrentDictionary<string, List<RemapRule>> _deviceRemaps;

    public ConfigManager()
    {
        _deviceLayouts = new ConcurrentDictionary<string, string>();
        _deviceAliases = new ConcurrentDictionary<string, string>();
        _layoutAliases = new ConcurrentDictionary<string, string>();
        _deviceRemaps = new ConcurrentDictionary<string, List<RemapRule>>();
        Directory.CreateDirectory(ConfigDir);
        LoadConfig();
    }

    private static string NormalizeKey(string deviceName)
        => DeviceGrouping.GetGroupKey(deviceName);

    public void SetLayoutForDevice(string deviceName, string layoutHkl)
    {
        string key = NormalizeKey(deviceName);
        if (string.IsNullOrEmpty(layoutHkl))
        {
            _deviceLayouts.TryRemove(key, out _);
            Logger.Info($"Layout desvinculado: {key}");
        }
        else
        {
            _deviceLayouts[key] = layoutHkl;
            Logger.Info($"Layout vinculado: {key} → {layoutHkl}");
        }
        SaveConfig();
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
            _deviceAliases[key] = alias;
        SaveConfig();
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
        if (string.IsNullOrWhiteSpace(alias))
            _layoutAliases.TryRemove(hkl, out _);
        else
            _layoutAliases[hkl] = alias;
        SaveConfig();
    }

    public string? GetLayoutAlias(string hkl)
        => _layoutAliases.TryGetValue(hkl, out var alias) ? alias : null;

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
        SaveConfig();
    }

    public int GetRemapRuleCount(string deviceName) => GetRemapRules(deviceName).Count;

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

            MigrateLegacyKeys();
            Logger.Info($"Configuração carregada: {_deviceLayouts.Count} layouts, {_deviceAliases.Count} apelidos, {_deviceRemaps.Count} remaps");
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao carregar configuração", ex);
        }
    }

    private void MigrateLegacyKeys()
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

        if (changed)
            SaveConfig();
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

    private void SaveConfig()
    {
        try
        {
            var data = new ConfigData
            {
                DeviceLayouts = new Dictionary<string, string>(_deviceLayouts),
                DeviceAliases = new Dictionary<string, string>(_deviceAliases),
                LayoutAliases = new Dictionary<string, string>(_layoutAliases),
                DeviceRemaps = new Dictionary<string, List<RemapRule>>(
                    _deviceRemaps.ToDictionary(k => k.Key, v => v.Value))
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFile, json);
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao salvar configuração", ex);
        }
    }

    private class ConfigData
    {
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
