using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DoorTelnet.Core.Combat;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Wpf.ViewModels;

public class CombatViewModel : ViewModelBase
{
    private readonly CombatTracker _tracker;
    private DateTime _lastCleanup = DateTime.UtcNow;

    public ObservableCollection<ActiveCombatDisplay> ActiveCombats { get; } = new();
    public ObservableCollection<CombatEntryDisplay> CompletedCombats { get; } = new();

    public ICommand ClearHistoryCommand { get; }
    public ICommand ExportCsvCommand { get; }
    public ICommand RefreshStatsCommand { get; }
    public ICommand ShowHistoryCommand { get; }

    // Summary stats
    private int _totalCombats; public int TotalCombats { get => _totalCombats; private set => SetProperty(ref _totalCombats, value); }
    private int _totalExperience; public int TotalExperience { get => _totalExperience; private set => SetProperty(ref _totalExperience, value); }
    private int _totalDamageDealt; public int TotalDamageDealt { get => _totalDamageDealt; private set => SetProperty(ref _totalDamageDealt, value); }
    private int _totalDamageTaken; public int TotalDamageTaken { get => _totalDamageTaken; private set => SetProperty(ref _totalDamageTaken, value); }
    private double _avgDps; public double AvgDps { get => _avgDps; private set => SetProperty(ref _avgDps, value); }
    private double _winRate; public double WinRate { get => _winRate; private set => SetProperty(ref _winRate, value); }

    public CombatViewModel(CombatTracker tracker, ILogger<CombatViewModel> logger) : base(logger)
    {
        _tracker = tracker;
        _tracker.CombatStarted += OnCombatStarted;
        _tracker.CombatUpdated += OnCombatUpdated;
        _tracker.CombatCompleted += OnCombatCompleted;

        ClearHistoryCommand = new RelayCommand(ClearHistory);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => CompletedCombats.Count > 0);
        RefreshStatsCommand = new RelayCommand(UpdateStats);
        ShowHistoryCommand = new RelayCommand(ShowHistory, () => CompletedCombats.Count > 0);
        BootstrapExisting();
    }

    private void BootstrapExisting()
    {
        foreach (var ac in _tracker.ActiveCombats)
        {
            ActiveCombats.Add(ActiveCombatDisplay.From(ac));
        }
        foreach (var ce in _tracker.CompletedCombats.TakeLast(100))
        {
            CompletedCombats.Add(CombatEntryDisplay.From(ce));
        }
        UpdateStats();
    }

    private void OnCombatStarted(ActiveCombat combat)
    {
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!ActiveCombats.Any(c => c.MonsterName.Equals(combat.MonsterName, StringComparison.OrdinalIgnoreCase)))
            {
                ActiveCombats.Add(ActiveCombatDisplay.From(combat));
            }
            UpdateStatsThrottled();
        });
    }

    private void OnCombatUpdated(ActiveCombat combat)
    {
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            var match = ActiveCombats.FirstOrDefault(c => c.MonsterName.Equals(combat.MonsterName, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                match.DamageDealt = combat.DamageDealt;
                match.DamageTaken = combat.DamageTaken;
                match.DurationSeconds = combat.DurationSeconds;
                match.DpsDealt = combat.DurationSeconds > 0 ? combat.DamageDealt / combat.DurationSeconds : 0;
                match.DpsTaken = combat.DurationSeconds > 0 ? combat.DamageTaken / combat.DurationSeconds : 0;
                match.IsTargeted = combat.IsTargeted;
            }
            UpdateStatsThrottled();
        });
    }

    private void OnCombatCompleted(CombatEntry entry)
    {
        App.Current.Dispatcher.BeginInvoke(() =>
        {
            var active = ActiveCombats.FirstOrDefault(a => a.MonsterName.Equals(entry.MonsterName, StringComparison.OrdinalIgnoreCase));
            if (active != null)
            {
                ActiveCombats.Remove(active);
            }
            
            // Check if we already have an entry for this combat (might be XP update)
            var existingCompleted = CompletedCombats.FirstOrDefault(c => 
                c.MonsterName.Equals(entry.MonsterName, StringComparison.OrdinalIgnoreCase) &&
                c.StartTime == entry.StartTime);
            
            if (existingCompleted != null)
            {
                // Update the existing entry with new XP information
                existingCompleted.ExperienceGained = entry.ExperienceGained;
                System.Diagnostics.Debug.WriteLine($"?? UI UPDATED: Updated existing combat entry '{entry.MonsterName}' with {entry.ExperienceGained} XP");
            }
            else
            {
                // Add new entry
                CompletedCombats.Add(CombatEntryDisplay.From(entry));
                System.Diagnostics.Debug.WriteLine($"?? UI ADDED: New combat entry '{entry.MonsterName}' with {entry.ExperienceGained} XP");
                
                // cap list size
                while (CompletedCombats.Count > 250) CompletedCombats.RemoveAt(0);
            }
            
            UpdateStats();
            (ExportCsvCommand as RelayCommand)?.NotifyCanExecuteChanged();
            (ShowHistoryCommand as RelayCommand)?.NotifyCanExecuteChanged();
        });
    }

    private void UpdateStatsThrottled()
    {
        if ((DateTime.UtcNow - _lastCleanup).TotalMilliseconds < 500) return;
        UpdateStats();
    }

    private void UpdateStats()
    {
        _lastCleanup = DateTime.UtcNow;
        
        // Call GetStatistics from background thread to prevent UI blocking
        Task.Run(() =>
        {
            try
            {
                var stats = _tracker.GetStatistics();
                
                // Update UI properties on UI thread
                App.Current.Dispatcher.BeginInvoke(() =>
                {
                    TotalCombats = stats.TotalCombats;
                    TotalExperience = stats.TotalExperience;
                    TotalDamageDealt = stats.TotalDamageDealt;
                    TotalDamageTaken = stats.TotalDamageTaken;
                    AvgDps = stats.TotalCombats > 0 ? (double)TotalDamageDealt / stats.TotalCombats : 0;
                    WinRate = stats.WinRate * 100.0;
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating combat statistics");
            }
        });
    }

    private void ClearHistory()
    {
        _tracker.ClearHistory();
        ActiveCombats.Clear();
        CompletedCombats.Clear();
        UpdateStats();
        (ExportCsvCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ShowHistoryCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void ShowHistory()
    {
        // Create and show the combat history dialog
        var dialog = new Views.Dialogs.CombatHistoryDialog(CompletedCombats);
        dialog.Owner = App.Current.MainWindow;
        dialog.ShowDialog();
    }

    private void ExportCsv()
    {
        try
        {
            var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"combat-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            using var sw = new System.IO.StreamWriter(path);
            sw.WriteLine("Monster,DamageDealt,DamageTaken,Experience,DurationSeconds,DpsDealt,DpsTaken,Status,StartTime,EndTime");
            foreach (var e in CompletedCombats)
            {
                sw.WriteLine($"{Escape(e.MonsterName)},{e.DamageDealt},{e.DamageTaken},{e.ExperienceGained},{e.DurationSeconds:F1},{e.DpsDealt:F2},{e.DpsTaken:F2},{e.Status},{e.StartTime:O},{e.EndTime:O}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed exporting combat CSV");
        }
    }

    private static string Escape(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    protected override void OnDisposing()
    {
        _tracker.CombatStarted -= OnCombatStarted;
        _tracker.CombatUpdated -= OnCombatUpdated;
        _tracker.CombatCompleted -= OnCombatCompleted;
        base.OnDisposing();
    }

    public class ActiveCombatDisplay : ObservableObject
    {
        private string _monsterName = string.Empty; public string MonsterName { get => _monsterName; set => SetProperty(ref _monsterName, value); }
        private int _damageDealt; public int DamageDealt { get => _damageDealt; set => SetProperty(ref _damageDealt, value); }
        private int _damageTaken; public int DamageTaken { get => _damageTaken; set => SetProperty(ref _damageTaken, value); }
        private double _duration; public double DurationSeconds { get => _duration; set => SetProperty(ref _duration, value); }
        private double _dpsDealt; public double DpsDealt { get => _dpsDealt; set => SetProperty(ref _dpsDealt, value); }
        private double _dpsTaken; public double DpsTaken { get => _dpsTaken; set => SetProperty(ref _dpsTaken, value); }
        private DateTime _start = DateTime.UtcNow; public DateTime StartTime { get => _start; set => SetProperty(ref _start, value); }
        private bool _isTargeted; public bool IsTargeted { get => _isTargeted; set => SetProperty(ref _isTargeted, value); }
        
        public static ActiveCombatDisplay From(ActiveCombat ac) => new()
        {
            MonsterName = ac.MonsterName,
            DamageDealt = ac.DamageDealt,
            DamageTaken = ac.DamageTaken,
            DurationSeconds = ac.DurationSeconds,
            DpsDealt = ac.DurationSeconds > 0 ? ac.DamageDealt / ac.DurationSeconds : 0,
            DpsTaken = ac.DurationSeconds > 0 ? ac.DamageTaken / ac.DurationSeconds : 0,
            StartTime = ac.StartTime,
            IsTargeted = ac.IsTargeted
        };
    }

    public class CombatEntryDisplay : ObservableObject
    {
        private string _monsterName = string.Empty; 
        public string MonsterName { get => _monsterName; set => SetProperty(ref _monsterName, value); }
        
        private int _damageDealt; 
        public int DamageDealt { get => _damageDealt; set => SetProperty(ref _damageDealt, value); }
        
        private int _damageTaken; 
        public int DamageTaken { get => _damageTaken; set => SetProperty(ref _damageTaken, value); }
        
        private int _experienceGained; 
        public int ExperienceGained { get => _experienceGained; set => SetProperty(ref _experienceGained, value); }
        
        private double _durationSeconds; 
        public double DurationSeconds { get => _durationSeconds; set => SetProperty(ref _durationSeconds, value); }
        
        private double _dpsDealt; 
        public double DpsDealt { get => _dpsDealt; set => SetProperty(ref _dpsDealt, value); }
        
        private double _dpsTaken; 
        public double DpsTaken { get => _dpsTaken; set => SetProperty(ref _dpsTaken, value); }
        
        private string _status = string.Empty; 
        public string Status { get => _status; set => SetProperty(ref _status, value); }
        
        private DateTime _startTime; 
        public DateTime StartTime { get => _startTime; set => SetProperty(ref _startTime, value); }
        
        private DateTime? _endTime; 
        public DateTime? EndTime { get => _endTime; set => SetProperty(ref _endTime, value); }
        
        public static CombatEntryDisplay From(CombatEntry e) => new()
        {
            MonsterName = e.MonsterName,
            DamageDealt = e.DamageDealt,
            DamageTaken = e.DamageTaken,
            ExperienceGained = e.ExperienceGained,
            DurationSeconds = e.DurationSeconds,
            DpsDealt = e.DpsDealt,
            DpsTaken = e.DpsTaken,
            Status = e.Status,
            StartTime = e.StartTime,
            EndTime = e.EndTime
        };
    }
}
