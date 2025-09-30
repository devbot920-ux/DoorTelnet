using System;
using System.Collections.ObjectModel;
using System.Linq;
using DoorTelnet.Core.World;
using DoorTelnet.Wpf.Services;
using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoorTelnet.Core.Navigation.Services;
using DoorTelnet.Core.Navigation.Models;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.ComponentModel;

namespace DoorTelnet.Wpf.ViewModels;

/// <summary>
/// Stage 4: Room information ViewModel with navigation integration and autocomplete.
/// </summary>
public partial class RoomViewModel : ViewModelBase
{
    private readonly RoomTracker _roomTracker;
    private readonly NavigationFeatureService? _navigationService;
    private readonly RoomMatchingService? _roomMatchingService;
    private readonly DispatcherTimer _searchTimer;

    public class MonsterDisplay : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _disposition = "neutral";
        private bool _targetingYou;
        private int? _count;

        public string Name 
        { 
            get => _name; 
            set 
            { 
                if (_name != value) 
                { 
                    _name = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(DisplayName)); 
                } 
            } 
        }

        public string Disposition 
        { 
            get => _disposition; 
            set 
            { 
                if (_disposition != value) 
                { 
                    _disposition = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(IsAggressive)); 
                } 
            } 
        }

        public bool TargetingYou 
        { 
            get => _targetingYou; 
            set 
            { 
                if (_targetingYou != value) 
                { 
                    _targetingYou = value; 
                    OnPropertyChanged(); 
                } 
            } 
        }

        public int? Count 
        { 
            get => _count; 
            set 
            { 
                if (_count != value) 
                { 
                    _count = value; 
                    OnPropertyChanged(); 
                    OnPropertyChanged(nameof(DisplayName)); 
                } 
            } 
        }

        public bool IsAggressive => string.Equals(Disposition, "aggressive", StringComparison.OrdinalIgnoreCase);
        public string DisplayName => Count.HasValue && Count.Value > 1 ? $"{Name} x{Count}" : Name;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class MovementModeOption
    {
        public MovementMode Mode { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
    }

    public RoomViewModel(RoomTracker roomTracker, ILogger<RoomViewModel> logger, NavigationFeatureService? navigationService = null, RoomMatchingService? roomMatchingService = null) : base(logger)
    {
        _roomTracker = roomTracker;
        _navigationService = navigationService;
        _roomMatchingService = roomMatchingService;
        
        Monsters = new ObservableCollection<MonsterDisplay>();
        Items = new ObservableCollection<string>();
        Exits = new ObservableCollection<string>();
        NavigationSuggestions = new ObservableCollection<NavigationSuggestion>();
        
        // Initialize movement mode options
        InitializeMovementModes();
        
        // Set up search timer for debounced autocomplete
        _searchTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _searchTimer.Tick += OnSearchTimerTick;
        
        // Subscribe to navigation status changes
        if (_navigationService != null)
        {
            _navigationService.NavigationStatusChanged += HandleNavigationStatusChanged;
            NavigationStatus = _navigationService.GetNavigationStatus();
        }
        
        Refresh();
    }

    private void InitializeMovementModes()
    {
        MovementModes = new ObservableCollection<MovementModeOption>
        {
            new() { Mode = MovementMode.UltraFast, Name = "Ultra-Fast", Description = "50ms delays (paste-friendly)", Icon = "?" },
            new() { Mode = MovementMode.FastWithFallback, Name = "Fast", Description = "200ms delays (safe areas)", Icon = "??" },
            new() { Mode = MovementMode.Triggered, Name = "Reliable", Description = "Wait for room detection", Icon = "???" },
            new() { Mode = MovementMode.TimedOnly, Name = "Timed", Description = "Original fixed delays", Icon = "??" }
        };
        
        // Set default to Triggered mode
        SelectedMovementMode = MovementModes.FirstOrDefault(m => m.Mode == MovementMode.Triggered);
    }

    private string _roomName = "Unknown"; 
    public string RoomName { get => _roomName; private set => SetProperty(ref _roomName, value); }
    
    private string _roomId = "Unknown";
    public string RoomId { get => _roomId; private set => SetProperty(ref _roomId, value); }
    
    private DateTime _lastUpdated; 
    public DateTime LastUpdated { get => _lastUpdated; private set => SetProperty(ref _lastUpdated, value); }

    private string? _navigationDestination;
    private bool _suppressSearchUpdate = false; // Flag to prevent search when updating from selection
    private bool _suppressSelectionClear = false; // Flag to prevent selection clearing
    
    public string? NavigationDestination 
    { 
        get => _navigationDestination; 
        set 
        {
            if (SetProperty(ref _navigationDestination, value))
            {
                OnNavigationDestinationChanged(value);
            }
        }
    }

    [ObservableProperty] private bool _isNavigating;
    [ObservableProperty] private string _navigationStatus = "Navigation idle";
    [ObservableProperty] private bool _isDropDownOpen;
    
    private NavigationSuggestion? _selectedSuggestion;
    public NavigationSuggestion? SelectedSuggestion 
    { 
        get => _selectedSuggestion; 
        set 
        {
            if (SetProperty(ref _selectedSuggestion, value))
            {
                OnSelectedSuggestionChanged(value);
            }
        }
    }

    // Movement mode selection properties
    public ObservableCollection<MovementModeOption> MovementModes { get; private set; } = new();
    
    private MovementModeOption? _selectedMovementMode;
    public MovementModeOption? SelectedMovementMode 
    { 
        get => _selectedMovementMode; 
        set 
        {
            if (SetProperty(ref _selectedMovementMode, value))
            {
                OnMovementModeChanged(value);
            }
        }
    }

    private void OnMovementModeChanged(MovementModeOption? selectedMode)
    {
        if (selectedMode != null && _navigationService != null)
        {
            // Set the movement mode in the navigation service
            _navigationService.SetMovementMode(selectedMode.Mode);
            _logger.LogInformation("Movement mode changed to: {Mode} ({Description})", 
                selectedMode.Name, selectedMode.Description);
        }
    }

    public ObservableCollection<string> Exits { get; }
    public ObservableCollection<MonsterDisplay> Monsters { get; }
    public ObservableCollection<string> Items { get; }
    public ObservableCollection<NavigationSuggestion> NavigationSuggestions { get; }

    public int MonsterCount => Monsters.Count;
    public bool HasAggressive => Monsters.Any(m => m.IsAggressive || m.TargetingYou);
    public bool IsNavigationEnabled => _navigationService?.IsNavigationEnabled ?? false;

    private void OnNavigationDestinationChanged(string? value)
    {
        // Don't trigger search if we're updating from selection
        if (_suppressSearchUpdate) return;
        
        // Reset timer when user types
        _searchTimer.Stop();
        
        if (!string.IsNullOrWhiteSpace(value))
        {
            _searchTimer.Start();
        }
        else
        {
            NavigationSuggestions.Clear();
            IsDropDownOpen = false;
            if (!_suppressSelectionClear)
            {
                SelectedSuggestion = null;
            }
        }
        
        // Update command can-execute states when destination changes
        StartNavigationCommand.NotifyCanExecuteChanged();
        SetPendingDestinationCommand.NotifyCanExecuteChanged();
    }

    private void OnSelectedSuggestionChanged(NavigationSuggestion? value)
    {
        if (value != null)
        {
            // Update destination without triggering search
            _suppressSearchUpdate = true;
            _suppressSelectionClear = true;
            try
            {
                NavigationDestination = value.ShortText;
                
                // Don't auto-close dropdown - let user manually close it or take action
                // This prevents the jarring behavior of selections disappearing
            }
            finally
            {
                _suppressSearchUpdate = false;
                _suppressSelectionClear = false;
            }
        }
        
        // Update command can-execute state when selection changes
        StartNavigationCommand.NotifyCanExecuteChanged();
        SetPendingDestinationCommand.NotifyCanExecuteChanged();
    }

    private void OnSearchTimerTick(object? sender, EventArgs e)
    {
        _searchTimer.Stop();
        
        // Only perform search if we're not in the middle of a selection update
        if (!_suppressSearchUpdate)
        {
            PerformSearch();
        }
    }

    private void PerformSearch()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(NavigationDestination) || _navigationService == null)
            {
                NavigationSuggestions.Clear();
                IsDropDownOpen = false;
                return;
            }

            var suggestions = _navigationService.SearchDestinations(NavigationDestination, 8);
            
            App.Current?.Dispatcher.BeginInvoke(() =>
            {
                // Store current text to preserve cursor position
                var currentText = NavigationDestination;
                
                NavigationSuggestions.Clear();
                foreach (var suggestion in suggestions)
                {
                    NavigationSuggestions.Add(suggestion);
                }
                
                // Only open dropdown if we have suggestions and we're not updating from a selection
                IsDropDownOpen = suggestions.Count > 0 && !_suppressSearchUpdate;
                
                // Ensure the text stays the same to prevent unwanted selection/highlighting
                if (NavigationDestination != currentText)
                {
                    _suppressSearchUpdate = true;
                    try
                    {
                        NavigationDestination = currentText;
                    }
                    finally
                    {
                        _suppressSearchUpdate = false;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error performing navigation search for: {Input}", NavigationDestination);
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void StartNavigation()
    {
        if (string.IsNullOrWhiteSpace(NavigationDestination) || _navigationService == null)
            return;

        // Close dropdown when navigation starts
        IsDropDownOpen = false;

        // Check if we have a selected suggestion
        if (SelectedSuggestion != null)
        {
            if (_navigationService.StartNavigationToSuggestion(SelectedSuggestion))
            {
                _logger.LogInformation("Started navigation to: {RoomName} (ID: {RoomId}) using {MovementMode}", 
                    SelectedSuggestion.RoomName, SelectedSuggestion.RoomId, SelectedMovementMode?.Name ?? "Default");
            }
        }
        else
        {
            // Fall back to original text-based navigation
            if (_navigationService.StartNavigation(NavigationDestination))
            {
                _logger.LogInformation("Started navigation to: {Destination} using {MovementMode}", 
                    NavigationDestination, SelectedMovementMode?.Name ?? "Default");
            }
        }
        
        // Don't clear selection - let user see what they navigated to
    }

    [RelayCommand(CanExecute = nameof(CanStopNavigation))]
    private void StopNavigation()
    {
        _navigationService?.StopNavigation();
        _logger.LogInformation("Navigation stopped by user");
        
        // Close dropdown when stopping
        IsDropDownOpen = false;
    }

    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void SetPendingDestination()
    {
        if (string.IsNullOrWhiteSpace(NavigationDestination) || _navigationService == null)
            return;

        // Close dropdown when setting pending
        IsDropDownOpen = false;

        if (SelectedSuggestion != null)
        {
            _navigationService.SetPendingDestination(SelectedSuggestion.ShortText);
            _logger.LogInformation("Set pending destination: {RoomName} (ID: {RoomId})", 
                SelectedSuggestion.RoomName, SelectedSuggestion.RoomId);
        }
        else
        {
            _navigationService.SetPendingDestination(NavigationDestination);
            _logger.LogInformation("Set pending destination: {Destination}", NavigationDestination);
        }
    }

    [RelayCommand]
    private void FindStores()
    {
        try
        {
            if (_navigationService == null) return;
            
            var stores = _navigationService.FindNearbyStores(40, 10);
            
            App.Current?.Dispatcher.BeginInvoke(() =>
            {
                NavigationSuggestions.Clear();
                foreach (var store in stores)
                {
                    NavigationSuggestions.Add(store);
                }
                
                // Update destination to "store" without triggering search
                _suppressSearchUpdate = true;
                try
                {
                    NavigationDestination = "store";
                }
                finally
                {
                    _suppressSearchUpdate = false;
                }
                
                IsDropDownOpen = stores.Count > 0;
            });
            
            _logger.LogInformation("Found {Count} nearby stores", stores.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding nearby stores");
        }
    }

    [RelayCommand]
    private void SelectUltraFastMode()
    {
        SelectedMovementMode = MovementModes.FirstOrDefault(m => m.Mode == MovementMode.UltraFast);
    }

    [RelayCommand]
    private void SelectFastMode()
    {
        SelectedMovementMode = MovementModes.FirstOrDefault(m => m.Mode == MovementMode.FastWithFallback);
    }

    [RelayCommand]
    private void SelectReliableMode()
    {
        SelectedMovementMode = MovementModes.FirstOrDefault(m => m.Mode == MovementMode.Triggered);
    }

    private bool CanNavigate() => 
        !string.IsNullOrWhiteSpace(NavigationDestination) && 
        _navigationService != null && 
        IsNavigationEnabled;

    private bool CanStopNavigation() => 
        _navigationService != null && 
        IsNavigating;

    public void Refresh()
    {
        var room = _roomTracker.CurrentRoom;
        if (room == null)
        {
            RoomName = "Unknown";
            RoomId = "Unknown";
            ClearCollections();
            return;
        }

        RoomName = string.IsNullOrWhiteSpace(room.Name) ? "(No Name)" : room.Name;
        LastUpdated = room.LastUpdated;

        // Try to get room ID from navigation service
        if (_roomMatchingService != null)
        {
            try
            {
                var match = _roomMatchingService.FindMatchingNode(room);
                RoomId = match != null ? match.Node.Id : "Unknown";
            }
            catch
            {
                RoomId = "Unknown";
            }
        }
        else
        {
            RoomId = "Unknown";
        }

        UpdateCollection(Exits, room.Exits);
        UpdateMonsterCollection(room);
        UpdateCollection(Items, room.Items);

        OnPropertyChanged(nameof(MonsterCount));
        OnPropertyChanged(nameof(HasAggressive));
        OnPropertyChanged(nameof(IsNavigationEnabled));
        
        // Update command can-execute states
        StartNavigationCommand.NotifyCanExecuteChanged();
        StopNavigationCommand.NotifyCanExecuteChanged();
        SetPendingDestinationCommand.NotifyCanExecuteChanged();
    }

    private void HandleNavigationStatusChanged(string status)
    {
        App.Current?.Dispatcher.BeginInvoke(() =>
        {
            NavigationStatus = status;
            IsNavigating = status.Contains("Navigating", StringComparison.OrdinalIgnoreCase);
            
            // Update command can-execute states
            StartNavigationCommand.NotifyCanExecuteChanged();
            StopNavigationCommand.NotifyCanExecuteChanged();
            SetPendingDestinationCommand.NotifyCanExecuteChanged();
            
            // Notify that IsNavigationEnabled may have changed
            OnPropertyChanged(nameof(IsNavigationEnabled));
        });
    }

    private void ClearCollections()
    {
        Exits.Clear();
        Monsters.Clear();
        Items.Clear();
    }

    private void UpdateMonsterCollection(RoomState room)
    {
        // remove stale
        for (int i = Monsters.Count - 1; i >= 0; i--)
        {
            var existing = Monsters[i];
            if (!room.Monsters.Any(m => NamesMatch(m.Name, existing.Name)))
            {
                Monsters.RemoveAt(i);
            }
        }
        // add/update
        foreach (var m in room.Monsters)
        {
            var match = Monsters.FirstOrDefault(x => NamesMatch(m.Name, x.Name));
            if (match == null)
            {
                Monsters.Add(new MonsterDisplay
                {
                    Name = m.Name,
                    Disposition = m.Disposition,
                    TargetingYou = m.TargetingYou,
                    Count = m.Count
                });
            }
            else
            {
                match.Disposition = m.Disposition;
                match.TargetingYou = m.TargetingYou;
                match.Count = m.Count;
            }
        }
    }

    private static bool NamesMatch(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void UpdateCollection(ObservableCollection<string> col, System.Collections.Generic.IEnumerable<string> src)
    {
        var arr = src.ToList();
        for (int i = col.Count - 1; i >= 0; i--)
            if (!arr.Contains(col[i])) col.RemoveAt(i);
        foreach (var s in arr)
            if (!col.Contains(s)) col.Add(s);
    }

    protected override void OnDisposing()
    {
        _searchTimer?.Stop();
        
        if (_navigationService != null)
        {
            _navigationService.NavigationStatusChanged -= HandleNavigationStatusChanged;
        }
        base.OnDisposing();
    }
}
