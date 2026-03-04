using System;
using System.Collections.Concurrent;
using System.IO;
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

    public ConfigManager()
    {
        _deviceLayouts = new ConcurrentDictionary<string, string>();
        _deviceAliases = new ConcurrentDictionary<string, string>();
        _layoutAliases = new ConcurrentDictionary<string, string>();
        Directory.CreateDirectory(ConfigDir);
        LoadConfig();
    }

    public void SetLayoutForDevice(string deviceName, string layoutHkl)
    {
        if (string.IsNullOrEmpty(layoutHkl))
        {
            _deviceLayouts.TryRemove(deviceName, out _);
            Logger.Info($"Layout desvinculado: {deviceName}");
        }
        else
        {
            _deviceLayouts[deviceName] = layoutHkl;
            Logger.Info($"Layout vinculado: {deviceName} → {layoutHkl}");
        }
        SaveConfig();
    }

    public string? GetLayoutForDevice(string deviceName)
    {
        return _deviceLayouts.TryGetValue(deviceName, out var hkl) ? hkl : null;
    }

    public IReadOnlyDictionary<string, string> GetAllMappings() => _deviceLayouts;

    // ══════════════════════════════════════
    // Apelidos de Dispositivos
    // ══════════════════════════════════════
    public void SetDeviceAlias(string deviceName, string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
            _deviceAliases.TryRemove(deviceName, out _);
        else
            _deviceAliases[deviceName] = alias;
        SaveConfig();
    }

    public string? GetDeviceAlias(string deviceName)
        => _deviceAliases.TryGetValue(deviceName, out var alias) ? alias : null;

    // ══════════════════════════════════════
    // Apelidos de Layouts
    // ══════════════════════════════════════
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

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                var root = JsonSerializer.Deserialize<ConfigData>(json);
                if (root != null)
                {
                    _deviceLayouts = new ConcurrentDictionary<string, string>(root.DeviceLayouts ?? new());
                    _deviceAliases = new ConcurrentDictionary<string, string>(root.DeviceAliases ?? new());
                    _layoutAliases = new ConcurrentDictionary<string, string>(root.LayoutAliases ?? new());
                    Logger.Info($"Configuração carregada: {_deviceLayouts.Count} mapeamentos, {_deviceAliases.Count} apelidos");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao carregar configuração", ex);
        }
    }

    private void SaveConfig()
    {
        try
        {
            var data = new ConfigData
            {
                DeviceLayouts = new Dictionary<string, string>(_deviceLayouts),
                DeviceAliases = new Dictionary<string, string>(_deviceAliases),
                LayoutAliases = new Dictionary<string, string>(_layoutAliases)
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
    }

    // ══════════════════════════════════════
    // Auto-start com Windows
    // ══════════════════════════════════════
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
