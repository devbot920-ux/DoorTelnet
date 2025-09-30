using DoorTelnet.Core.Navigation.Services;
using DoorTelnet.Core.Navigation.Models;
using DoorTelnet.Core.Player;
using DoorTelnet.Core.Automation;
using DoorTelnet.Core.Combat;
using Microsoft.Extensions.Logging;
using System.ComponentModel;

namespace DoorTelnet.Wpf.Services;

/// <summary>
/// Navigation feature service that integrates with the existing automation framework
/// </summary>
public class NavigationFeatureService : IDisposable, INotifyPropertyChanged
{
    private readonly NavigationService _navigationService;
    private readonly PlayerProfile _playerProfile;
    private readonly StatsTracker _statsTracker;
    private readonly CombatTracker _combatTracker;
    private readonly ILogger<NavigationFeatureService> _logger;

    private Timer? _evaluationTimer;
    private readonly TimeSpan _evaluationInterval = TimeSpan.FromSeconds(3);

    // Navigation state
    private string? _pendingDestination;
    private bool _autoNavigationEnabled;

    public event PropertyChangedEventHandler? PropertyChanged;

    public NavigationFeatureService(
        NavigationService navigationService,
        PlayerProfile playerProfile,
        StatsTracker statsTracker,
        CombatTracker combatTracker,
        ILogger<NavigationFeatureService> logger)
    {
        _navigationService = navigationService;
        _playerProfile = playerProfile;
        _statsTracker = statsTracker;
        _combatTracker = combatTracker;
        _logger = logger;

        // Subscribe to navigation events
        _navigationService.StateChanged += OnNavigationStateChanged;
        _navigationService.NavigationAlert += OnNavigationAlert;
        _navigationService.NavigationCompleted += OnNavigationCompleted;

        // Subscribe to profile changes
        _playerProfile.Updated += OnProfileUpdated;

        // Start evaluation timer
        _evaluationTimer = new Timer(EvaluateNavigationFeatures, null, _evaluationInterval, _evaluationInterval);

        _logger.LogInformation("NavigationFeatureService initialized - AutoNavigation: {Enabled}", IsNavigationEnabled);
    }

    /// <summary>
    /// Event fired when navigation status changes
    /// </summary>
    public event Action<string>? NavigationStatusChanged;

    /// <summary>
    /// Starts navigation to a destination
    /// </summary>
    public bool StartNavigation(string destination)
    {
        try
        {
            var result = _navigationService.StartNavigationByName(destination);
            if (result.IsSuccess)
            {
                _logger.LogInformation("Navigation started to: {Destination}", destination);
                NavigationStatusChanged?.Invoke($"Navigating to {destination}");
                return true;
            }
            else
            {
                _logger.LogWarning("Failed to start navigation to {Destination}: {Error}", destination, result.Message);
                NavigationStatusChanged?.Invoke($"Navigation failed: {result.Message}");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting navigation to {Destination}", destination);
            NavigationStatusChanged?.Invoke($"Navigation error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Stops current navigation
    /// </summary>
    public void StopNavigation()
    {
        _navigationService.StopNavigation();
        _pendingDestination = null;
        NavigationStatusChanged?.Invoke("Navigation stopped");
    }

    /// <summary>
    /// Sets a pending destination for when conditions are safe
    /// </summary>
    public void SetPendingDestination(string destination)
    {
        _pendingDestination = destination;
        _logger.LogInformation("Pending destination set: {Destination}", destination);
        NavigationStatusChanged?.Invoke($"Will navigate to {destination} when safe");
    }

    /// <summary>
    /// Gets the current navigation status
    /// </summary>
    public string GetNavigationStatus()
    {
        var status = _navigationService.GetStatus();
        
        return status.State switch
        {
            NavigationState.Idle => _pendingDestination != null 
                ? $"Waiting to navigate to {_pendingDestination}" 
                : "Navigation idle",
            NavigationState.Navigating => $"Navigating ({status.StepsRemaining} steps remaining)",
            NavigationState.Paused => $"Navigation paused: {status.SafetyPauseReason ?? "Unknown reason"}",
            NavigationState.Completed => "Navigation completed",
            NavigationState.Error => "Navigation error",
            _ => "Unknown state"
        };
    }

    /// <summary>
    /// Checks if navigation features are enabled
    /// </summary>
    public bool IsNavigationEnabled => _playerProfile.Features.AutoNavigation;

    /// <summary>
    /// Searches for navigation suggestions based on user input
    /// </summary>
    public List<NavigationSuggestion> SearchDestinations(string input, int maxResults = 10)
    {
        try
        {
            return _navigationService.SmartSearch(input, maxResults);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching destinations for input: {Input}", input);
            return new List<NavigationSuggestion>();
        }
    }

    /// <summary>
    /// Finds nearby stores within the specified distance
    /// </summary>
    public List<NavigationSuggestion> FindNearbyStores(int maxDistance = 40, int maxResults = 10)
    {
        try
        {
            return _navigationService.FindNearbyStores(maxDistance, maxResults);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding nearby stores");
            return new List<NavigationSuggestion>();
        }
    }

    /// <summary>
    /// Starts navigation using a suggestion
    /// </summary>
    public bool StartNavigationToSuggestion(NavigationSuggestion suggestion)
    {
        try
        {
            var result = _navigationService.StartNavigation(suggestion.RoomId);
            if (result.IsSuccess)
            {
                _logger.LogInformation("Started navigation to: {RoomName} (ID: {RoomId})", 
                    suggestion.RoomName, suggestion.RoomId);
                NavigationStatusChanged?.Invoke($"Navigating to {suggestion.RoomName}");
                return true;
            }
            else
            {
                _logger.LogWarning("Failed to start navigation to {RoomName}: {Error}", 
                    suggestion.RoomName, result.Message);
                NavigationStatusChanged?.Invoke($"Navigation failed: {result.Message}");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting navigation to {RoomName}", suggestion.RoomName);
            NavigationStatusChanged?.Invoke($"Navigation error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Sets the movement mode for navigation
    /// </summary>
    public void SetMovementMode(MovementMode mode)
    {
        try
        {
            _navigationService.SetMovementMode(mode);
            _logger.LogInformation("Movement mode set to: {Mode}", mode);
            NavigationStatusChanged?.Invoke($"Movement mode: {mode}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting movement mode to {Mode}", mode);
        }
    }

    /// <summary>
    /// Enables ultra-fast movement mode for maximum speed
    /// </summary>
    public void EnableUltraFastMovement()
    {
        SetMovementMode(MovementMode.UltraFast);
    }

    /// <summary>
    /// Enables fast movement mode for safe areas
    /// </summary>
    public void EnableFastMovement()
    {
        SetMovementMode(MovementMode.FastWithFallback);
    }

    /// <summary>
    /// Enables triggered movement mode for maximum reliability
    /// </summary>
    public void EnableTriggeredMovement()
    {
        SetMovementMode(MovementMode.Triggered);
    }

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void EvaluateNavigationFeatures(object? state)
    {
        try
        {
            var previouslyEnabled = _autoNavigationEnabled;
            var currentlyEnabled = _playerProfile.Features.AutoNavigation;
            
            if (previouslyEnabled != currentlyEnabled)
            {
                _autoNavigationEnabled = currentlyEnabled;
                _logger.LogDebug("Auto-navigation state changed: {Enabled}", currentlyEnabled);
                OnPropertyChanged(nameof(IsNavigationEnabled));
            }

            if (!currentlyEnabled)
            {
                return;
            }

            // Check if we have a pending destination and conditions are safe
            if (!string.IsNullOrEmpty(_pendingDestination))
            {
                var currentState = _navigationService.CurrentState;
                
                if (currentState == NavigationState.Idle)
                {
                    // Try to start navigation to pending destination
                    if (IsSafeToStartNavigation())
                    {
                        var destination = _pendingDestination;
                        _pendingDestination = null; // Clear pending before attempting
                        
                        if (StartNavigation(destination))
                        {
                            _logger.LogInformation("Started navigation to pending destination: {Destination}", destination);
                        }
                        else
                        {
                            // If it failed, restore the pending destination
                            _pendingDestination = destination;
                        }
                    }
                }
            }

            // Future: Add other navigation features here
            // - Hunt route optimization
            // - Quest routing
            // - Store/trainer routing
            // - Patrol routes
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in navigation feature evaluation");
        }
    }

    private bool IsSafeToStartNavigation()
    {
        // Check health
        if (_statsTracker != null)
        {
            var hpPercent = _statsTracker.Hp / (double)Math.Max(_statsTracker.MaxHp, 1) * 100;
            if (hpPercent < _playerProfile.Thresholds.NavigationMinHpPercent)
            {
                return false;
            }
        }

        // REMOVED: Combat state check - navigation now allowed during combat
        // This allows players to navigate away from danger even when in combat

        return true;
    }

    private void OnNavigationStateChanged(NavigationState newState)
    {
        var status = GetNavigationStatus();
        NavigationStatusChanged?.Invoke(status);
        
        _logger.LogDebug("Navigation state changed to: {State}", newState);
    }

    private void OnNavigationAlert(string alert)
    {
        NavigationStatusChanged?.Invoke($"Alert: {alert}");
        _logger.LogWarning("Navigation alert: {Alert}", alert);
    }

    private void OnNavigationCompleted(string message)
    {
        NavigationStatusChanged?.Invoke(message);
        _logger.LogInformation("Navigation completed: {Message}", message);
    }

    private void OnProfileUpdated()
    {
        // React to profile changes - for example, safety thresholds
        _logger.LogDebug("Player profile updated - re-evaluating navigation settings");
        OnPropertyChanged(nameof(IsNavigationEnabled));
    }

    public void Dispose()
    {
        _evaluationTimer?.Dispose();
        
        // Unsubscribe from events
        if (_navigationService != null)
        {
            _navigationService.StateChanged -= OnNavigationStateChanged;
            _navigationService.NavigationAlert -= OnNavigationAlert;
            _navigationService.NavigationCompleted -= OnNavigationCompleted;
        }

        if (_playerProfile != null)
        {
            _playerProfile.Updated -= OnProfileUpdated;
        }

        _logger.LogInformation("NavigationFeatureService disposed");
    }
}