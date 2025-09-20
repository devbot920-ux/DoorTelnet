using System;
using System.Windows.Media;
using DoorTelnet.Core.Automation;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Wpf.ViewModels;

public class StatsViewModel : ViewModelBase
{
    private readonly StatsTracker _stats;

    public StatsViewModel(StatsTracker stats, ILogger<StatsViewModel> logger) : base(logger)
    {
        _stats = stats;
        _stats.Updated += OnStatsUpdated;
        Refresh();
        OnPropertyChanged(nameof(Hp));
        OnPropertyChanged(nameof(MaxHp));
        OnPropertyChanged(nameof(Mp));
        OnPropertyChanged(nameof(Mv));
        OnPropertyChanged(nameof(Ac));
        OnPropertyChanged(nameof(At));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(HpPercent));
        OnPropertyChanged(nameof(HpBrush));
    }

    private int _hp; public int Hp { get => _hp; private set => SetProperty(ref _hp, value); }
    private int _maxHp; public int MaxHp { get => _maxHp; private set => SetProperty(ref _maxHp, value); }
    private int _mp; public int Mp { get => _mp; private set => SetProperty(ref _mp, value); }
    private int _mv; public int Mv { get => _mv; private set => SetProperty(ref _mv, value); }
    private int _ac; public int Ac { get => _ac; private set => SetProperty(ref _ac, value); }
    private int _at; public int At { get => _at; private set => SetProperty(ref _at, value); }
    private string? _state; public string? State { get => _state; private set => SetProperty(ref _state, value); }

    public int HpPercent => MaxHp > 0 ? (int)Math.Round((double)Hp / MaxHp * 100) : 0;

    public Brush HpBrush => GetBarBrush(HpPercent);

    private Brush GetBarBrush(int pct)
    {
        if (pct >= 75) return Brushes.LimeGreen;
        if (pct >= 50) return Brushes.Goldenrod;
        if (pct >= 25) return Brushes.OrangeRed;
        return Brushes.DarkRed;
    }

    private void OnStatsUpdated()
    {
        App.Current.Dispatcher.Invoke(Refresh);
    }

    private void Refresh()
    {
        Hp = _stats.Hp;
        MaxHp = _stats.MaxHp;
        Mp = _stats.Mp;
        Mv = _stats.Mv;
        Ac = _stats.Ac;
        At = _stats.At;
        State = _stats.State;
        OnPropertyChanged(nameof(HpPercent));
        OnPropertyChanged(nameof(HpBrush));
    }

    protected override void OnDisposing()
    {
        _stats.Updated -= OnStatsUpdated;
        base.OnDisposing();
    }
}
