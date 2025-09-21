using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoorTelnet.Core.Player;
using DoorTelnet.Wpf.Services;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;
using System; // Added for Environment
using System.Linq; // Added for LINQ operations

namespace DoorTelnet.Wpf.ViewModels;

public partial class HotKeysViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;

    public ObservableCollection<HotKeyBinding> HotKeys { get; } = new();

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand ReloadCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public event System.Action? RequestClose;

    public HotKeysViewModel(ISettingsService settingsService, ILogger<HotKeysViewModel> logger) : base(logger)
    {
        _settingsService = settingsService;
        SaveCommand = new RelayCommand(Save);
        ReloadCommand = new RelayCommand(Load);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());
        Load();
    }

    private string HotKeysPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoorTelnet", "hotkeys.json");

    private void Load()
    {
        HotKeys.Clear();
        
        // Initialize F1-F12 with empty scripts
        var settings = new HotKeySettings();
        
        // Load from file if exists
        if (File.Exists(HotKeysPath))
        {
            try
            {
                var json = File.ReadAllText(HotKeysPath);
                var loaded = JsonSerializer.Deserialize<HotKeySettings>(json);
                if (loaded?.Bindings != null)
                {
                    settings = loaded;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load hotkeys from {path}", HotKeysPath);
            }
        }

        // Ensure we have F1-F12
        for (int i = 1; i <= 12; i++)
        {
            var key = $"F{i}";
            var existing = settings.Bindings.FirstOrDefault(b => b.Key == key);
            if (existing == null)
            {
                settings.Bindings.Add(new HotKeyBinding { Key = key, Script = "" });
            }
        }

        // Sort by key and add to collection
        foreach (var binding in settings.Bindings.OrderBy(b => b.Key))
        {
            HotKeys.Add(binding);
        }
    }

    private void Save()
    {
        try
        {
            var settings = new HotKeySettings
            {
                Bindings = HotKeys.ToList()
            };

            var directory = Path.GetDirectoryName(HotKeysPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(HotKeysPath, json);
            
            _logger.LogInformation("HotKeys saved to {path}", HotKeysPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save hotkeys to {path}", HotKeysPath);
        }
    }
}