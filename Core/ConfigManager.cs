using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace KeyNexus.Core;

public class ConfigManager
{
    private const string ConfigFile = "keynexus_config.json";
    
    // Key: DeviceName, Value: Layout HKL (string format ex: "00000416" for ABNT2)
    private ConcurrentDictionary<string, string> _deviceLayouts;

    public ConfigManager()
    {
        _deviceLayouts = new ConcurrentDictionary<string, string>();
        LoadConfig();
    }

    public void SetLayoutForDevice(string deviceName, string layoutHkl)
    {
        if (string.IsNullOrEmpty(layoutHkl))
        {
            _deviceLayouts.TryRemove(deviceName, out _);
        }
        else
        {
            _deviceLayouts[deviceName] = layoutHkl;
        }
        SaveConfig();
    }

    public string? GetLayoutForDevice(string deviceName)
    {
        if (_deviceLayouts.TryGetValue(deviceName, out var layoutHkl))
        {
            return layoutHkl;
        }
        return null;
    }

    public IReadOnlyDictionary<string, string> GetAllMappings()
    {
        return _deviceLayouts;
    }

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
                }
            }
        }
        catch (Exception ex)
        {
            // Log error in a real app
            System.Diagnostics.Debug.WriteLine($"Failed to load config: {ex.Message}");
        }
    }

    private void SaveConfig()
    {
        try
        {
            var json = JsonSerializer.Serialize(_deviceLayouts, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFile, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save config: {ex.Message}");
        }
    }
}
