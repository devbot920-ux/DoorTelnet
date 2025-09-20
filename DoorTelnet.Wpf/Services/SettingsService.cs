using System;
using System.IO;
using System.Text.Json;

namespace DoorTelnet.Wpf.Services;

public interface ISettingsService
{
    AppSettings Get();
    void Save();
}

public class AppSettings
{
    public UiSettings UI { get; set; } = new();
    public ConnectionSettings Connection { get; set; } = new();
    public AutomationSettings Automation { get; set; } = new();
}

public class UiSettings
{
    public int Width { get; set; } = 1100;
    public int Height { get; set; } = 640;
    public string Theme { get; set; } = "dark";
}

public class ConnectionSettings
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 23;
    public string LastUsername { get; set; } = string.Empty;
    public string LastCharacter { get; set; } = string.Empty;
}

public class AutomationSettings
{
    public bool AutoReconnect { get; set; }
    public int AutoReconnectDelaySec { get; set; } = 5;
}

public class SettingsService : ISettingsService
{
    private readonly string _file;
    private readonly object _sync = new();
    private AppSettings _cache = new();

    public SettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoorTelnet");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "settings.json");
        Load();
    }

    public AppSettings Get()
    {
        lock (_sync) return _cache;
    }

    public void Save()
    {
        lock (_sync)
        {
            try
            {
                var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_file, json);
            }
            catch { }
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                var json = File.ReadAllText(_file);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) _cache = loaded;
            }
        }
        catch { }
    }
}
