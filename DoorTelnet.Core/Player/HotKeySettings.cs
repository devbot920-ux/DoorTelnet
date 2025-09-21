using System.Collections.Generic;
using System.ComponentModel;
using System.Linq; // Added for LINQ operations
using System; // Added for StringComparison

namespace DoorTelnet.Core.Player;

public class HotKeyBinding : INotifyPropertyChanged
{
    private string _key = string.Empty;
    private string _script = string.Empty;

    public string Key 
    { 
        get => _key; 
        set 
        { 
            if (_key != value) 
            { 
                _key = value; 
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Key))); 
            } 
        } 
    }

    public string Script 
    { 
        get => _script; 
        set 
        { 
            if (_script != value) 
            { 
                _script = value; 
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Script))); 
            } 
        } 
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public class HotKeySettings
{
    public List<HotKeyBinding> Bindings { get; set; } = new();

    public HotKeySettings()
    {
        // Initialize F1-F12 keys
        for (int i = 1; i <= 12; i++)
        {
            Bindings.Add(new HotKeyBinding { Key = $"F{i}", Script = "" });
        }
    }

    public string? GetScript(string key)
    {
        var binding = Bindings.FirstOrDefault(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(binding?.Script) ? null : binding.Script;
    }

    public void SetScript(string key, string script)
    {
        var binding = Bindings.FirstOrDefault(b => b.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (binding != null)
        {
            binding.Script = script ?? string.Empty;
        }
        else
        {
            Bindings.Add(new HotKeyBinding { Key = key, Script = script ?? string.Empty });
        }
    }
}