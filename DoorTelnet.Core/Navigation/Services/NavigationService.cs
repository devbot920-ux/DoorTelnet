using DoorTelnet.Core.Navigation.Models;
using DoorTelnet.Core.Navigation.Services;
using DoorTelnet.Core.Player;
using DoorTelnet.Core.World;
using DoorTelnet.Core.Combat;
using DoorTelnet.Core.Automation;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Core.Navigation.Services;

/// <summary>
/// High-level navigation service that coordinates pathfinding, room matching, safety checks, and command execution
/// </summary>
public class NavigationService
{
    private readonly GraphDataService _graphData;
    private readonly PathfindingService _pathfinding;
    private readonly RoomMatchingService _roomMatching;
    private readonly MovementQueueService _movementQueue;
    private readonly RoomTracker _roomTracker;
    private readonly PlayerProfile _playerProfile;
    private readonly StatsTracker _statsTracker;
    private readonly CombatTracker _combatTracker;
    private readonly ILogger<NavigationService> _logger;

    private readonly object _sync = new();
    private NavigationPath? _currentPath;
    private string? _currentDestinationId;
    private int _currentStepIndex;
    private DateTime _lastNavigationUpdate = DateTime.MinValue;
    private DateTime _lastSafetyCheck = DateTime.MinValue;
    private readonly TimeSpan _safetyCheckInterval = TimeSpan.FromSeconds(2);

    // Safety state
    private bool _isPausedForSafety = false;
    private string? _safetyPauseReason;

    public NavigationService(
        GraphDataService graphData,
        PathfindingService pathfinding,
        RoomMatchingService roomMatching,
        MovementQueueService movementQueue,
        RoomTracker roomTracker,
        PlayerProfile playerProfile,
        StatsTracker statsTracker,
        CombatTracker combatTracker,
        ILogger<NavigationService> logger)
    {
        _graphData = graphData;
        _pathfinding = pathfinding;
        _roomMatching = roomMatching;
        _movementQueue = movementQueue;
        _roomTracker = roomTracker;
        _playerProfile = playerProfile;
        _statsTracker = statsTracker;
        _combatTracker = combatTracker;
        _logger = logger;

        // Subscribe to events
        _roomTracker.RoomChanged += OnRoomChanged;
        _movementQueue.CommandExecuted += OnMovementCommandExecuted;
        _movementQueue.ExecutionInterrupted += OnMovementInterrupted;
        _combatTracker.CombatStarted += OnCombatStarted;
        _combatTracker.CombatCompleted += OnCombatCompleted;

        // Start safety monitoring
        _ = Task.Run(SafetyMonitoringLoop);
    }

    /// <summary>
    /// Gets the current navigation state
    /// </summary>
    public NavigationState CurrentState { get; private set; } = NavigationState.Idle;

    /// <summary>
    /// Gets the current navigation path if one exists
    /// </summary>
    public NavigationPath? CurrentPath
    {
        get
        {
            lock (_sync)
                return _currentPath;
        }
    }

    /// <summary>
    /// Gets the current position confidence based on room matching
    /// </summary>
    public double PositionConfidence
    {
        get
        {
            if (_roomTracker.CurrentRoom == null)
                return 0.0;

            return _roomMatching.GetPositionConfidence(_roomTracker.CurrentRoom);
        }
    }

    /// <summary>
    /// Event fired when navigation state changes
    /// </summary>
    public event Action<NavigationState>? StateChanged;

    /// <summary>
    /// Event fired when navigation encounters an error or needs attention
    /// </summary>
    public event Action<string>? NavigationAlert;

    /// <summary>
    /// Event fired when navigation completes successfully
    /// </summary>
    public event Action<string>? NavigationCompleted;

    /// <summary>
    /// Starts navigation to a target room by ID
    /// </summary>
    public NavigationResult StartNavigation(string targetRoomId, NavigationConstraints? constraints = null)
    {
        try
        {
            if (!_graphData.IsLoaded)
                return NavigationResult.Failed("Graph data not loaded");

            if (_roomTracker.CurrentRoom == null)
                return NavigationResult.Failed("Current room unknown - position tracking required");

            // Safety check before starting
            if (!IsSafeToNavigate(out var safetyReason))
                return NavigationResult.Failed($"Navigation safety check failed: {safetyReason}");

            // Find current position in graph
            var currentMatch = _roomMatching.FindMatchingNode(_roomTracker.CurrentRoom);
            if (currentMatch == null || currentMatch.Confidence < 0.7)
                return NavigationResult.Failed("Unable to determine current position in graph");

            lock (_sync)
            {
                // Stop any existing navigation
                StopNavigationInternal();

                // Calculate path
                var request = new NavigationRequest
                {
                    FromRoomId = currentMatch.Node.Id,
                    ToRoomId = targetRoomId,
                    Constraints = constraints ?? CreateDefaultConstraints()
                };

                var path = _pathfinding.CalculatePath(request);
                if (!path.IsValid)
                    return NavigationResult.Failed($"No path found: {path.ErrorReason}");

                // Validate path safety
                if (!ValidatePathSafety(path, out var pathSafetyReason))
                    return NavigationResult.Failed($"Path not safe: {pathSafetyReason}");

                // Start navigation
                _currentPath = path;
                _currentDestinationId = targetRoomId;
                _currentStepIndex = 0;
                _lastNavigationUpdate = DateTime.UtcNow;
                _isPausedForSafety = false;
                _safetyPauseReason = null;

                // Queue the movement commands
                _movementQueue.QueueCommands(path.Steps);

                SetState(NavigationState.Navigating);

                _logger.LogInformation("Started navigation from {From} to {To}, {Steps} steps",
                    currentMatch.Node.Id, targetRoomId, path.StepCount);

                return NavigationResult.Success($"Navigation started: {path.StepCount} steps to destination");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting navigation to {TargetId}", targetRoomId);
            return NavigationResult.Failed($"Navigation error: {ex.Message}");
        }
    }

    /// <summary>
    /// Starts navigation to a room by name (performs fuzzy matching)
    /// </summary>
    public NavigationResult StartNavigationByName(string targetRoomName, NavigationConstraints? constraints = null)
    {
        var targetNode = _roomMatching.FindNodeByName(targetRoomName, 0.8);
        if (targetNode == null)
            return NavigationResult.Failed($"Could not find room matching '{targetRoomName}'");

        return StartNavigation(targetNode.Id, constraints);
    }

    /// <summary>
    /// Stops the current navigation
    /// </summary>
    public void StopNavigation()
    {
        lock (_sync)
        {
            StopNavigationInternal();
        }
    }

    /// <summary>
    /// Pauses navigation with a reason
    /// </summary>
    public void PauseNavigation(string reason)
    {
        lock (_sync)
        {
            if (CurrentState == NavigationState.Navigating)
            {
                _movementQueue.PauseExecution(reason);
                SetState(NavigationState.Paused);
                _logger.LogInformation("Navigation paused: {Reason}", reason);
                NavigationAlert?.Invoke($"Navigation paused: {reason}");
            }
        }
    }

    /// <summary>
    /// Resumes paused navigation
    /// </summary>
    public void ResumeNavigation()
    {
        lock (_sync)
        {
            if (CurrentState == NavigationState.Paused)
            {
                // Check safety before resuming
                if (!IsSafeToNavigate(out var safetyReason))
                {
                    _logger.LogWarning("Cannot resume navigation: {SafetyReason}", safetyReason);
                    NavigationAlert?.Invoke($"Cannot resume navigation: {safetyReason}");
                    return;
                }

                _movementQueue.ResumeExecution();
                SetState(NavigationState.Navigating);
                _isPausedForSafety = false;
                _safetyPauseReason = null;
                _logger.LogInformation("Navigation resumed");
            }
        }
    }

    /// <summary>
    /// Finds rooms matching search criteria
    /// </summary>
    public List<GraphNode> FindRooms(string searchTerm, int maxResults = 20)
    {
        if (!_graphData.IsLoaded)
            return new List<GraphNode>();

        return _graphData.FindRooms(node =>
            (!string.IsNullOrEmpty(node.Label) && node.Label.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(node.Sector) && node.Sector.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)),
            maxResults);
    }

    /// <summary>
    /// Searches for rooms by name prefix for autocomplete
    /// </summary>
    public List<NavigationSuggestion> SearchRooms(string searchTerm, int maxResults = 10)
    {
        if (!_graphData.IsLoaded || string.IsNullOrWhiteSpace(searchTerm))
            return new List<NavigationSuggestion>();

        var suggestions = new List<NavigationSuggestion>();

        // First, try exact room ID match
        if (int.TryParse(searchTerm, out var roomIdInt))
        {
            var roomIdString = roomIdInt.ToString();
            var exactMatch = _graphData.GetNode(roomIdString);
            if (exactMatch != null)
            {
                suggestions.Add(new NavigationSuggestion
                {
                    RoomId = exactMatch.Id,
                    RoomName = exactMatch.Label ?? "Unknown",
                    Sector = exactMatch.Sector ?? "",
                    Distance = CalculateDistance(exactMatch), // Calculate actual distance, not 0
                    SuggestionType = NavigationSuggestionType.RoomId
                });
            }
        }

        // Search by room name/label
        var matchingRooms = _graphData.FindRooms(node =>
            (!string.IsNullOrEmpty(node.Label) && node.Label.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(node.Sector) && node.Sector.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)),
            maxResults);

        foreach (var room in matchingRooms)
        {
            if (suggestions.Count >= maxResults) break;
            
            suggestions.Add(new NavigationSuggestion
            {
                RoomId = room.Id,
                RoomName = room.Label ?? "Unknown",
                Sector = room.Sector ?? "",
                Distance = CalculateDistance(room),
                SuggestionType = NavigationSuggestionType.RoomName
            });
        }

        return suggestions.OrderBy(s => s.Distance).Take(maxResults).ToList();
    }

    /// <summary>
    /// Finds nearby rooms with stores within specified distance
    /// </summary>
    public List<NavigationSuggestion> FindNearbyStores(int maxDistance = 40, int maxResults = 10)
    {
        if (!_graphData.IsLoaded || _roomTracker.CurrentRoom == null)
            return new List<NavigationSuggestion>();

        var currentMatch = _roomMatching.FindMatchingNode(_roomTracker.CurrentRoom);
        if (currentMatch == null)
            return new List<NavigationSuggestion>();

        var suggestions = new List<NavigationSuggestion>();
        var currentRoomId = currentMatch.Node.Id;

        // Find all store rooms
        var storeRooms = _graphData.FindRooms(node =>
            node.IsStore == 1 || 
            (!string.IsNullOrEmpty(node.Label) && 
             (node.Label.Contains("store", StringComparison.OrdinalIgnoreCase) ||
              node.Label.Contains("shop", StringComparison.OrdinalIgnoreCase) ||
              node.Label.Contains("market", StringComparison.OrdinalIgnoreCase) ||
              node.Label.Contains("merchant", StringComparison.OrdinalIgnoreCase))),
            maxResults * 3); // Get more to filter by distance

        foreach (var storeRoom in storeRooms)
        {
            if (suggestions.Count >= maxResults) break;

            // Calculate actual path distance using absolute shortest path
            var path = _pathfinding.FindAbsoluteShortestPath(currentRoomId, storeRoom.Id);
            if (path.IsValid && path.StepCount <= maxDistance)
            {
                suggestions.Add(new NavigationSuggestion
                {
                    RoomId = storeRoom.Id,
                    RoomName = storeRoom.Label ?? "Unknown Store",
                    Sector = storeRoom.Sector ?? "",
                    Distance = path.StepCount,
                    SuggestionType = NavigationSuggestionType.Store
                });
            }
        }

        return suggestions.OrderBy(s => s.Distance).Take(maxResults).ToList();
    }

    /// <summary>
    /// Searches for rooms based on input - handles room names, IDs, and special queries like "store"
    /// </summary>
    public List<NavigationSuggestion> SmartSearch(string input, int maxResults = 10)
    {
        if (string.IsNullOrWhiteSpace(input))
            return new List<NavigationSuggestion>();

        var suggestions = new List<NavigationSuggestion>();

        // Special case: "store" search
        if (input.Equals("store", StringComparison.OrdinalIgnoreCase) || 
            input.Equals("stores", StringComparison.OrdinalIgnoreCase))
        {
            return FindNearbyStores(40, maxResults);
        }

        // Try as room ID first
        if (int.TryParse(input, out var roomId))
        {
            var roomIdString = roomId.ToString();
            var exactMatch = _graphData.GetNode(roomIdString);
            if (exactMatch != null)
            {
                suggestions.Add(new NavigationSuggestion
                {
                    RoomId = exactMatch.Id,
                    RoomName = exactMatch.Label ?? "Unknown",
                    Sector = exactMatch.Sector ?? "",
                    Distance = CalculateDistance(exactMatch),
                    SuggestionType = NavigationSuggestionType.RoomId
                });
            }
        }

        // Search by name/sector
        var nameMatches = SearchRooms(input, maxResults - suggestions.Count);
        suggestions.AddRange(nameMatches.Where(s => s.SuggestionType != NavigationSuggestionType.RoomId));

        return suggestions.Take(maxResults).ToList();
    }

    private int CalculateDistance(GraphNode targetRoom)
    {
        if (_roomTracker.CurrentRoom == null)
            return int.MaxValue;

        var currentMatch = _roomMatching.FindMatchingNode(_roomTracker.CurrentRoom);
        if (currentMatch == null)
            return int.MaxValue;

        try
        {
            // For distance calculation in suggestions, use absolute shortest path with no constraints
            // This gives users the true shortest distance, not filtered by safety constraints
            var path = _pathfinding.FindAbsoluteShortestPath(currentMatch.Node.Id, targetRoom.Id);
            return path.IsValid ? path.StepCount : int.MaxValue;
        }
        catch
        {
            return int.MaxValue;
        }
    }

    /// <summary>
    /// Gets navigation statistics and status
    /// </summary>
    public NavigationStatus GetStatus()
    {
        lock (_sync)
        {
            var queueStatus = _movementQueue.Status;
            
            return new NavigationStatus
            {
                State = CurrentState,
                PositionConfidence = PositionConfidence,
                CurrentPath = _currentPath,
                CurrentStepIndex = _currentStepIndex,
                StepsRemaining = _currentPath?.Steps.Count - _currentStepIndex ?? 0,
                EstimatedTimeRemaining = CalculateRemainingTime(),
                LastUpdate = _lastNavigationUpdate,
                IsPausedForSafety = _isPausedForSafety,
                SafetyPauseReason = _safetyPauseReason,
                QueuedCommands = queueStatus.QueueCount,
                CurrentCommand = queueStatus.CurrentCommand
            };
        }
    }

    private void OnRoomChanged(RoomState newRoom)
    {
        lock (_sync)
        {
            if (CurrentState == NavigationState.Navigating && _currentPath != null)
            {
                // Update step progress based on room change
                _currentStepIndex++;
                _lastNavigationUpdate = DateTime.UtcNow;

                // Check if we've completed the navigation
                if (_currentStepIndex >= _currentPath.Steps.Count)
                {
                    CompleteNavigation();
                    return;
                }

                // Verify we're still on the correct path
                var currentMatch = _roomMatching.FindMatchingNode(newRoom);
                if (currentMatch != null && currentMatch.Confidence > 0.7)
                {
                    var expectedRoomId = _currentStepIndex < _currentPath.Steps.Count
                        ? _currentPath.Steps[_currentStepIndex].FromRoomId
                        : _currentDestinationId;

                    if (currentMatch.Node.Id != expectedRoomId)
                    {
                        _logger.LogWarning("Navigation off course: expected {Expected}, found {Actual}",
                            expectedRoomId, currentMatch.Node.Id);
                        
                        // For Phase 2, we'll just alert - re-routing can be added in Phase 3
                        NavigationAlert?.Invoke("Navigation may be off course");
                    }
                }
            }
        }
    }

    private void OnMovementCommandExecuted(string direction)
    {
        _logger.LogDebug("Movement command executed: {Direction}", direction);
    }

    private void OnMovementInterrupted(string reason)
    {
        lock (_sync)
        {
            if (CurrentState == NavigationState.Navigating)
            {
                SetState(NavigationState.Paused);
                _logger.LogInformation("Navigation interrupted: {Reason}", reason);
            }
        }
    }

    private void OnCombatStarted(ActiveCombat combat)
    {
        if (_playerProfile.Features.PauseNavigationInCombat && CurrentState == NavigationState.Navigating)
        {
            PauseNavigation($"Combat detected with {combat.MonsterName}");
        }
    }

    private void OnCombatCompleted(CombatEntry combat)
    {
        if (_playerProfile.Features.PauseNavigationInCombat && CurrentState == NavigationState.Paused)
        {
            // Wait a moment before resuming to ensure safety
            Task.Run(async () =>
            {
                await Task.Delay(2000); // Wait 2 seconds
                if (CurrentState == NavigationState.Paused && IsSafeToNavigate(out _))
                {
                    ResumeNavigation();
                }
            });
        }
    }

    private async Task SafetyMonitoringLoop()
    {
        while (true)
        {
            try
            {
                await Task.Delay(_safetyCheckInterval);

                if (CurrentState == NavigationState.Navigating || CurrentState == NavigationState.Paused)
                {
                    CheckNavigationSafety();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in safety monitoring loop");
            }
        }
    }

    private void CheckNavigationSafety()
    {
        if (DateTime.UtcNow - _lastSafetyCheck < _safetyCheckInterval)
            return;

        _lastSafetyCheck = DateTime.UtcNow;

        var wasSafe = !_isPausedForSafety;
        var isSafe = IsSafeToNavigate(out var safetyReason);

        if (wasSafe && !isSafe)
        {
            // Became unsafe - pause navigation
            _isPausedForSafety = true;
            _safetyPauseReason = safetyReason;
            PauseNavigation(safetyReason);
        }
        else if (!wasSafe && isSafe && CurrentState == NavigationState.Paused)
        {
            // Became safe again - resume if paused for safety
            if (_isPausedForSafety)
            {
                ResumeNavigation();
            }
        }
    }

    private bool IsSafeToNavigate(out string reason)
    {
        reason = string.Empty;

        // Check health
        if (_statsTracker != null)
        {
            var hpPercent = _statsTracker.Hp / (double)Math.Max(_statsTracker.MaxHp, 1) * 100;
            if (hpPercent < _playerProfile.Thresholds.NavigationMinHpPercent)
            {
                reason = $"Health too low: {hpPercent:F1}% (minimum: {_playerProfile.Thresholds.NavigationMinHpPercent}%)";
                return false;
            }
        }

        // Check combat state
        if (_playerProfile.Features.PauseNavigationInCombat)
        {
            var activeCombats = _combatTracker.ActiveCombats;
            if (activeCombats.Count > 0)
            {
                reason = $"In combat with {activeCombats.Count} enemy(ies)";
                return false;
            }
        }

        return true;
    }

    private bool ValidatePathSafety(NavigationPath path, out string reason)
    {
        reason = string.Empty;

        foreach (var step in path.Steps)
        {
            var targetNode = _graphData.GetNode(step.ToRoomId);
            if (targetNode != null)
            {
                // Check if room is safe for player level
                if (!targetNode.IsPeaceful && targetNode.SpawnTotal > 0)
                {
                    var dangerLevel = targetNode.SpawnTotal + (targetNode.HasTrap == 1 ? 2 : 0);
                    if (dangerLevel > _playerProfile.Thresholds.MaxRoomDangerLevel)
                    {
                        reason = $"Path contains dangerous room: {targetNode.Sector} (danger level: {dangerLevel})";
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private NavigationConstraints CreateDefaultConstraints()
    {
        return new NavigationConstraints
        {
            AvoidDangerousRooms = _playerProfile.Features.AvoidDangerousRooms,
            AvoidTraps = true,
            PlayerLevel = _playerProfile.Player.Level,
            MaxDangerLevel = _playerProfile.Thresholds.MaxRoomDangerLevel
        };
    }

    private void StopNavigationInternal()
    {
        _movementQueue.StopExecution("Navigation stopped");
        _currentPath = null;
        _currentDestinationId = null;
        _currentStepIndex = 0;
        _isPausedForSafety = false;
        _safetyPauseReason = null;
        SetState(NavigationState.Idle);
        _logger.LogInformation("Navigation stopped");
    }

    private void CompleteNavigation()
    {
        var destination = _currentDestinationId;
        StopNavigationInternal();
        SetState(NavigationState.Completed);
        _logger.LogInformation("Navigation completed to {Destination}", destination);
        NavigationCompleted?.Invoke($"Navigation completed successfully");
        
        // Auto-transition back to idle after a short delay
        Task.Run(async () =>
        {
            await Task.Delay(3000);
            if (CurrentState == NavigationState.Completed)
            {
                SetState(NavigationState.Idle);
            }
        });
    }

    private void SetState(NavigationState newState)
    {
        if (CurrentState != newState)
        {
            CurrentState = newState;
            StateChanged?.Invoke(newState);
        }
    }

    private TimeSpan CalculateRemainingTime()
    {
        if (_currentPath == null || CurrentState != NavigationState.Navigating)
            return TimeSpan.Zero;

        var remainingSteps = _currentPath.Steps.Skip(_currentStepIndex);
        var remainingSeconds = remainingSteps.Sum(step => step.EstimatedDelaySeconds);
        return TimeSpan.FromSeconds(remainingSeconds);
    }
}

/// <summary>
/// Represents the state of the navigation system
/// </summary>
public enum NavigationState
{
    Idle,
    Navigating,
    Paused,
    Completed,
    Error
}

/// <summary>
/// Represents the result of a navigation operation
/// </summary>
public class NavigationResult
{
    public bool IsSuccess { get; }
    public string Message { get; }

    private NavigationResult(bool isSuccess, string message)
    {
        IsSuccess = isSuccess;
        Message = message;
    }

    public static NavigationResult Success(string message) => new(true, message);
    public static NavigationResult Failed(string message) => new(false, message);
}

/// <summary>
/// Represents the current status of navigation
/// </summary>
public class NavigationStatus
{
    public NavigationState State { get; set; }
    public double PositionConfidence { get; set; }
    public NavigationPath? CurrentPath { get; set; }
    public int CurrentStepIndex { get; set; }
    public int StepsRemaining { get; set; }
    public TimeSpan EstimatedTimeRemaining { get; set; }
    public DateTime LastUpdate { get; set; }
    public bool IsPausedForSafety { get; set; }
    public string? SafetyPauseReason { get; set; }
    public int QueuedCommands { get; set; }
    public string? CurrentCommand { get; set; }
}