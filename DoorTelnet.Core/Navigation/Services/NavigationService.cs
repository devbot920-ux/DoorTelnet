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

    // Room detection timeout handling
    private DateTime _lastMovementCommandTime = DateTime.MinValue;
    private readonly TimeSpan _roomDetectionTimeout = TimeSpan.FromSeconds(10);
    private string? _pendingMovementDirection;

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

        // Configure movement queue with room tracker for event-driven movement
        _movementQueue.SetRoomTracker(_roomTracker);
        
        // Set movement mode based on player preferences
        SetOptimalMovementMode();

        // Subscribe to events
        _roomTracker.RoomChanged += OnRoomChanged;
        _movementQueue.CommandExecuted += OnMovementCommandExecuted;
        _movementQueue.ExecutionInterrupted += OnMovementInterrupted;

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
            // Clear room detection timeout - room change detected successfully
            _lastMovementCommandTime = DateTime.MinValue;
            _pendingMovementDirection = null;

            if (CurrentState == NavigationState.Navigating && _currentPath != null)
            {
                // FIRST: Update navigation context BEFORE room matching
                // This ensures the room matching service has the correct context
                UpdateRoomMatchingContext(newRoom);

                // THEN: Use fast path room matching during navigation
                var currentMatch = _roomMatching.FindMatchingNodeFast(newRoom);
                
                // Log simplified matching information for navigation
                _logger.LogDebug("Navigation room change: Step {Step}, Room: '{RoomName}', Match: {MatchId} (confidence: {Confidence:P1}, type: {Type})",
                    _currentStepIndex, newRoom.Name, currentMatch?.Node.Id ?? "none", currentMatch?.Confidence ?? 0.0, currentMatch?.MatchType ?? "none");

                // NOW: Update step progress based on successful room change
                _currentStepIndex++;
                _lastNavigationUpdate = DateTime.UtcNow;

                // Check if we've completed the navigation
                if (_currentStepIndex >= _currentPath.Steps.Count)
                {
                    CompleteNavigation();
                    return;
                }

                // Simple validation: if we got a good match, we're probably on track
                if (currentMatch != null && currentMatch.Confidence > 0.8)
                {
                    _logger.LogDebug("Navigation on track: room {RoomId} matched with high confidence", currentMatch.Node.Id);
                }
                else if (currentMatch != null && currentMatch.Confidence > 0.6)
                {
                    _logger.LogDebug("Navigation proceeding: room {RoomId} matched with acceptable confidence {Confidence:P1}", 
                        currentMatch.Node.Id, currentMatch.Confidence);
                }
                else
                {
                    _logger.LogWarning("Navigation uncertainty: low confidence room match {Confidence:P1} for '{RoomName}'", 
                        currentMatch?.Confidence ?? 0.0, newRoom.Name);
                    NavigationAlert?.Invoke($"Uncertain location during navigation");
                }
            }
            else
            {
                // Not navigating, but still update context for future matches
                UpdateRoomMatchingContext(newRoom);
            }
        }
    }

    private void OnMovementCommandExecuted(string direction)
    {
        lock (_sync)
        {
            _lastMovementCommandTime = DateTime.UtcNow;
            _pendingMovementDirection = direction;
            _logger.LogDebug("Movement command executed: {Direction} at {Time}", direction, _lastMovementCommandTime);
        }
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

        // Check for room detection timeout during navigation
        if (CurrentState == NavigationState.Navigating && 
            _lastMovementCommandTime != DateTime.MinValue &&
            DateTime.UtcNow - _lastMovementCommandTime > _roomDetectionTimeout)
        {
            _logger.LogWarning("Room detection timeout detected - no room change after {Timeout}s from movement command", 
                _roomDetectionTimeout.TotalSeconds);
            
            NavigationAlert?.Invoke($"Room detection timeout - navigation may need manual intervention");
            
            // Reset the timer to avoid spam alerts
            _lastMovementCommandTime = DateTime.UtcNow;
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

        // REMOVED: Combat state check - navigation now allowed during combat
        // This allows players to navigate away from danger even when in combat

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

    /// <summary>
    /// Updates the room matching service with current navigation context
    /// </summary>
    private void UpdateRoomMatchingContext(RoomState newRoom)
    {
        try
        {
            string? previousRoomId = null;
            string? lastDirection = null;
            string? expectedRoomId = null;

            if (_currentPath != null && CurrentState == NavigationState.Navigating)
            {
                // Current step index tells us which step we just completed or are about to complete
                if (_currentStepIndex >= 0 && _currentStepIndex < _currentPath.Steps.Count)
                {
                    var currentStep = _currentPath.Steps[_currentStepIndex];
                    
                    // Previous room is where we came from (FromRoomId of current step)
                    previousRoomId = currentStep.FromRoomId;
                    lastDirection = currentStep.Direction;
                    
                    // Expected room is where this step should take us (ToRoomId of current step)
                    expectedRoomId = currentStep.ToRoomId;
                    
                    _logger.LogDebug("Step {StepIndex}: {From} --{Direction}--> {To} (expected)", 
                        _currentStepIndex, previousRoomId, lastDirection, expectedRoomId);
                }
                else if (_currentStepIndex >= _currentPath.Steps.Count)
                {
                    // We've completed all steps - expected room is the final destination
                    expectedRoomId = _currentDestinationId;
                    
                    if (_currentPath.Steps.Count > 0)
                    {
                        var lastStep = _currentPath.Steps[_currentPath.Steps.Count - 1];
                        previousRoomId = lastStep.FromRoomId;
                        lastDirection = lastStep.Direction;
                    }
                    
                    _logger.LogDebug("Navigation complete: expecting final destination {Destination}", expectedRoomId);
                }
            }

            var context = new NavigationContext
            {
                PreviousRoomId = previousRoomId,
                ExpectedRoomId = expectedRoomId,
                LastDirection = lastDirection,
                CurrentPath = _currentPath,
                CurrentStepIndex = _currentStepIndex,
                PreviousConfidence = 0.8, // TODO: Store actual previous confidence
                LastMovement = DateTime.UtcNow
            };

            _roomMatching.UpdateNavigationContext(context);

            _logger.LogDebug("Updated room matching context: Prev={Previous}, Expected={Expected}, Direction={Direction}, Step={Step}",
                previousRoomId, expectedRoomId, lastDirection, _currentStepIndex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating room matching context");
        }
    }

    /// <summary>
    /// Sets the movement mode for navigation
    /// </summary>
    public void SetMovementMode(MovementMode mode)
    {
        _movementQueue.SetMovementMode(mode);
        _logger.LogInformation("Navigation movement mode set to: {Mode}", mode);
    }

    /// <summary>
    /// Sets optimal movement mode based on current context
    /// </summary>
    private void SetOptimalMovementMode()
    {
        // Default to triggered mode for reliability
        var mode = MovementMode.Triggered;
        
        // Check if in a peaceful region for fast movement
        if (_roomTracker.CurrentRoom != null)
        {
            var currentMatch = _roomMatching.FindMatchingNode(_roomTracker.CurrentRoom);
            if (currentMatch?.Node != null && currentMatch.Node.IsPeaceful)
            {
                mode = MovementMode.FastWithFallback;
                _logger.LogDebug("Peaceful area detected - using fast movement mode");
            }
        }
        
        _movementQueue.SetMovementMode(mode);
    }

    /// <summary>
    /// Enables ultra-fast movement mode for maximum speed (trusts path completely)
    /// </summary>
    public void EnableUltraFastMovement()
    {
        SetMovementMode(MovementMode.UltraFast);
    }

    /// <summary>
    /// Enables fast movement mode for safe areas like towns
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