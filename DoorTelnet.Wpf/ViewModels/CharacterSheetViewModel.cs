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
    public bool AutoShield { get => _profile.Features.AutoShield; set { if (_profile.Features.AutoShield != value) { _profile.Features.AutoShield = value; OnPropertyChanged(); } } }
    public bool AutoHeal { get => _profile.Features.AutoHeal; set { if (_profile.Features.AutoHeal != value) { _profile.Features.AutoHeal = value; OnPropertyChanged(); } } }
    public bool AutoGong { get => _profile.Features.AutoGong; set { if (_profile.Features.AutoGong != value) { _profile.Features.AutoGong = value; OnPropertyChanged(); } } }
    public bool PickupGold { get => _profile.Features.PickupGold; set { if (_profile.Features.PickupGold != value) { _profile.Features.PickupGold = value; OnPropertyChanged(); } } }
    public bool PickupSilver { get => _profile.Features.PickupSilver; set { if (_profile.Features.PickupSilver != value) { _profile.Features.PickupSilver = value; OnPropertyChanged(); } } }

    // Threshold bindings
    public int ShieldRefreshSec { get => _profile.Thresholds.ShieldRefreshSec; set { if (_profile.Thresholds.ShieldRefreshSec != value) { _profile.Thresholds.ShieldRefreshSec = value; OnPropertyChanged(); } } }
    public int GongMinHpPercent { get => _profile.Thresholds.GongMinHpPercent; set { if (_profile.Thresholds.GongMinHpPercent != value) { _profile.Thresholds.GongMinHpPercent = value; OnPropertyChanged(); } } }
    public int AutoHealHpPercent { get => _profile.Thresholds.AutoHealHpPercent; set { if (_profile.Thresholds.AutoHealHpPercent != value) { _profile.Thresholds.AutoHealHpPercent = value; OnPropertyChanged(); } } }

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
        OnPropertyChanged(nameof(PickupGold));
        OnPropertyChanged(nameof(PickupSilver));
        OnPropertyChanged(nameof(ShieldRefreshSec));
        OnPropertyChanged(nameof(GongMinHpPercent));
        OnPropertyChanged(nameof(AutoHealHpPercent));
    }
}
