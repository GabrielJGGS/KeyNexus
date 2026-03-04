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

    public ConfigManager()
    {
        _deviceLayouts = new ConcurrentDictionary<string, string>();
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

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                var json = File.ReadAllText(ConfigFile);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null)
                {
                    _deviceLayouts = new ConcurrentDictionary<string, string>(dict);
                    Logger.Info($"Configuração carregada: {_deviceLayouts.Count} mapeamentos");
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
            var json = JsonSerializer.Serialize(
                _deviceLayouts,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFile, json);
        }
        catch (Exception ex)
        {
            Logger.Error("Falha ao salvar configuração", ex);
        }
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
