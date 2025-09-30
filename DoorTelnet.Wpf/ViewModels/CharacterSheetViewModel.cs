using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoorTelnet.Core.Player;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace DoorTelnet.Wpf.ViewModels;

public class HealDisplay : ObservableObject
{
    public string Short { get; set; } = string.Empty;
    public string Spell { get; set; } = string.Empty;
    public int Heals { get; set; }
}

public class SpellDisplay : ObservableObject
{
    public string Nick { get; set; } = string.Empty;
    public string LongName { get; set; } = string.Empty;
    public string Sphere { get; set; } = string.Empty;
    public int Mana { get; set; }
    public int Diff { get; set; }
}

public partial class CharacterSheetViewModel : ViewModelBase
{
    private readonly PlayerProfile _profile;

    public CharacterSheetViewModel(PlayerProfile profile, ILogger<CharacterSheetViewModel> logger) : base(logger)
    {
        _profile = profile;
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());
        RefreshFromProfile();
        _profile.Updated += () => App.Current.Dispatcher.Invoke(RefreshFromProfile);
    }

    public event System.Action? RequestClose;

    public IRelayCommand CloseCommand { get; }

    private string _name = string.Empty; public string Name { get => _name; set => SetProperty(ref _name, value); }
    private string _class = string.Empty; public string Class { get => _class; set => SetProperty(ref _class, value); }
    public ObservableCollection<HealDisplay> Heals { get; } = new();
    public ObservableCollection<string> Shields { get; } = new();
    public ObservableCollection<SpellDisplay> Spells { get; } = new();
    public ObservableCollection<string> Inventory { get; } = new();

    // Automation toggles
    public bool AutoShield { get => _profile.Features.AutoShield; set { if (_profile.Features.AutoShield != value) { _profile.Features.AutoShield = value; OnPropertyChanged(); TriggerProfileUpdate(); } } }
    public bool AutoHeal { get => _profile.Features.AutoHeal; set { if (_profile.Features.AutoHeal != value) { _profile.Features.AutoHeal = value; OnPropertyChanged(); TriggerProfileUpdate(); } } }
    public bool AutoGong { get => _profile.Features.AutoGong; set { if (_profile.Features.AutoGong != value) { _profile.Features.AutoGong = value; OnPropertyChanged(); TriggerProfileUpdate(); } } }
    public bool AutoAttack { get => _profile.Features.AutoAttack; set { if (_profile.Features.AutoAttack != value) { _profile.Features.AutoAttack = value; OnPropertyChanged(); TriggerProfileUpdate(); } } }
    public bool PickupGold { get => _profile.Features.PickupGold; set { if (_profile.Features.PickupGold != value) { _profile.Features.PickupGold = value; OnPropertyChanged(); TriggerProfileUpdate(); } } }
    public bool PickupSilver { get => _profile.Features.PickupSilver; set { if (_profile.Features.PickupSilver != value) { _profile.Features.PickupSilver = value; OnPropertyChanged(); TriggerProfileUpdate(); } } }

    // Threshold bindings
    public int ShieldRefreshSec { get => _profile.Thresholds.ShieldRefreshSec; set { if (_profile.Thresholds.ShieldRefreshSec != value) { _profile.Thresholds.ShieldRefreshSec = value; OnPropertyChanged(); TriggerProfileUpdate(); } } }
    public int GongMinHpPercent { get => _profile.Thresholds.GongMinHpPercent; set { var v = Math.Max(0, Math.Min(100, value)); if (_profile.Thresholds.GongMinHpPercent != v) { _profile.Thresholds.GongMinHpPercent = v; OnPropertyChanged(); TriggerProfileUpdate(); } } }
    public int AutoHealHpPercent { get => _profile.Thresholds.AutoHealHpPercent; set { var v = Math.Max(0, Math.Min(100, value)); if (_profile.Thresholds.AutoHealHpPercent != v) { _profile.Thresholds.AutoHealHpPercent = v; OnPropertyChanged(); TriggerProfileUpdate(); } } }
    public int WarningHealHpPercent { get => _profile.Thresholds.WarningHealHpPercent; set { var v = Math.Max(0, Math.Min(100, value)); if (_profile.Thresholds.WarningHealHpPercent != v) { _profile.Thresholds.WarningHealHpPercent = v; OnPropertyChanged(); TriggerProfileUpdate(); } } }
    public int CriticalHpPercent { get => _profile.Thresholds.CriticalHpPercent; set { var v = Math.Max(0, Math.Min(100, value)); if (_profile.Thresholds.CriticalHpPercent != v) { _profile.Thresholds.CriticalHpPercent = v; OnPropertyChanged(); TriggerProfileUpdate(); } } }
    public string CriticalAction 
    { 
        get => _profile.Thresholds.CriticalAction; 
        set 
        { 
            if (_profile.Thresholds.CriticalAction != value) 
            { 
                _logger.LogDebug("CriticalAction changing from '{old}' to '{new}'", _profile.Thresholds.CriticalAction, value);
                _profile.Thresholds.CriticalAction = value ?? "disconnect"; // Changed from "stop" to "disconnect"
                OnPropertyChanged(); 
                TriggerProfileUpdate(); // Trigger profile update for automation system
                _logger.LogInformation("CriticalAction updated to '{action}' - automation system notified", _profile.Thresholds.CriticalAction);
            } 
        } 
    }

    // Critical Action options for dropdown
    public ObservableCollection<string> CriticalActionOptions { get; } = new ObservableCollection<string>
    {
        "disconnect",  // Moved to first position as the safest default
        "stop",
        "script:quit",
        "script:heal",
        "script:{DISCONNECT}",
        "script:{custom command}"
    };

    public void RefreshFromProfile()
    {
        Name = _profile.Player.Name;
        Class = _profile.Player.Class;
        Heals.Clear(); foreach (var h in _profile.Player.Heals) Heals.Add(new HealDisplay { Short = h.Short, Spell = h.Spell, Heals = h.Heals });
        Shields.Clear(); foreach (var s in _profile.Player.Shields) Shields.Add(s);
        Spells.Clear(); foreach (var s in _profile.Spells.OrderBy(s=>s.Sphere).ThenBy(s=>s.Nick)) Spells.Add(new SpellDisplay { Nick = s.Nick, LongName = s.LongName, Sphere = s.Sphere, Mana = s.Mana, Diff = s.Diff });
        Inventory.Clear(); foreach (var i in _profile.Player.Inventory) Inventory.Add(i);
        OnPropertyChanged(nameof(AutoShield));
        OnPropertyChanged(nameof(AutoHeal));
        OnPropertyChanged(nameof(AutoGong));
        OnPropertyChanged(nameof(AutoAttack));
        OnPropertyChanged(nameof(PickupGold));
        OnPropertyChanged(nameof(PickupSilver));
        OnPropertyChanged(nameof(ShieldRefreshSec));
        OnPropertyChanged(nameof(GongMinHpPercent));
        OnPropertyChanged(nameof(AutoHealHpPercent));
        OnPropertyChanged(nameof(WarningHealHpPercent));
        OnPropertyChanged(nameof(CriticalHpPercent));
        OnPropertyChanged(nameof(CriticalAction));
    }

    // Helper method to trigger profile updates when nested properties change
    private void TriggerProfileUpdate()
    {
        try 
        { 
            // Use reflection to call the private RaiseUpdated method
            var method = _profile.GetType().GetMethod("RaiseUpdated", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(_profile, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to trigger profile update");
        }
    }
}
