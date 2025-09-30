using System.Collections.Concurrent;
using DoorTelnet.Core.Navigation.Models;
using DoorTelnet.Core.Telnet;
using DoorTelnet.Core.World;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Core.Navigation.Services;

/// <summary>
/// Event-driven movement queue that waits for room detection before sending next command
/// </summary>
public class MovementQueueService
{
    private readonly TelnetClient _telnetClient;
    private readonly ILogger<MovementQueueService> _logger;
    private readonly object _sync = new();

    private readonly ConcurrentQueue<MovementCommand> _commandQueue = new();
    private CancellationTokenSource? _executionCts;
    private Task? _executionTask;
    private MovementCommand? _currentCommand;
    private DateTime _lastCommandTime = DateTime.MinValue;

    // Event-driven execution state
    private TaskCompletionSource<bool>? _waitingForRoomChange;
    private bool _isWaitingForRoomDetection = false;
    private DateTime _lastRoomChangeTime = DateTime.MinValue;

    // Configuration
    private MovementMode _movementMode = MovementMode.Triggered; // Default to event-driven
    private TimeSpan _fallbackTimeout = TimeSpan.FromSeconds(8); // Fallback timeout
    private TimeSpan _minCommandInterval = TimeSpan.FromMilliseconds(50); // Minimum time between commands

    // Room tracker integration
    private RoomTracker? _roomTracker;

    public MovementQueueService(TelnetClient telnetClient, ILogger<MovementQueueService> logger)
    {
        _telnetClient = telnetClient;
        _logger = logger;
    }

    /// <summary>
    /// Sets the room tracker for event-driven movement
    /// </summary>
    public void SetRoomTracker(RoomTracker roomTracker)
    {
        if (_roomTracker != null)
        {
            _roomTracker.RoomChanged -= OnRoomChanged;
        }

        _roomTracker = roomTracker;
        if (_roomTracker != null)
        {
            _roomTracker.RoomChanged += OnRoomChanged;
        }
    }

    /// <summary>
    /// Gets the current queue status
    /// </summary>
    public MovementQueueStatus Status
    {
        get
        {
            lock (_sync)
            {
                return new MovementQueueStatus
                {
                    QueueCount = _commandQueue.Count,
                    IsExecuting = _executionTask != null && !_executionTask.IsCompleted,
                    CurrentCommand = _currentCommand?.Direction,
                    LastCommandTime = _lastCommandTime,
                    IsWaitingForRoomDetection = _isWaitingForRoomDetection,
                    MovementMode = _movementMode
                };
            }
        }
    }

    /// <summary>
    /// Sets the movement mode
    /// </summary>
    public void SetMovementMode(MovementMode mode)
    {
        lock (_sync)
        {
            _movementMode = mode;
            _logger.LogInformation("Movement mode set to: {Mode}", mode);
        }
    }

    /// <summary>
    /// Event fired when a command is about to be executed
    /// </summary>
    public event Action<string>? CommandExecuting;

    /// <summary>
    /// Event fired when a command has been executed
    /// </summary>
    public event Action<string>? CommandExecuted;

    /// <summary>
    /// Event fired when command execution is interrupted
    /// </summary>
    public event Action<string>? ExecutionInterrupted;

    /// <summary>
    /// Queues a list of movement commands for execution
    /// </summary>
    public void QueueCommands(IEnumerable<NavigationStep> steps)
    {
        var commands = steps.Select(step => new MovementCommand
        {
            Direction = step.Direction,
            EstimatedDelay = TimeSpan.FromSeconds(step.EstimatedDelaySeconds),
            RequiresDoor = step.RequiresDoor,
            IsHidden = step.IsHidden,
            FromRoomId = step.FromRoomId,
            ToRoomId = step.ToRoomId
        });

        QueueCommands(commands);
    }

    /// <summary>
    /// Queues movement commands for execution
    /// </summary>
    public void QueueCommands(IEnumerable<MovementCommand> commands)
    {
        foreach (var command in commands)
        {
            _commandQueue.Enqueue(command);
        }

        _logger.LogInformation("Queued {Count} movement commands for {Mode} execution", 
            commands.Count(), _movementMode);
        
        // Start execution if not already running
        StartExecution();
    }

    /// <summary>
    /// Queues a single movement command
    /// </summary>
    public void QueueCommand(string direction, TimeSpan? delay = null)
    {
        var command = new MovementCommand
        {
            Direction = direction,
            EstimatedDelay = delay ?? TimeSpan.FromMilliseconds(100)
        };

        _commandQueue.Enqueue(command);
        _logger.LogDebug("Queued movement command: {Direction}", direction);
        
        StartExecution();
    }

    /// <summary>
    /// Starts command execution if not already running
    /// </summary>
    public void StartExecution()
    {
        lock (_sync)
        {
            if (_executionTask != null && !_executionTask.IsCompleted)
            {
                return; // Already running
            }

            if (_commandQueue.IsEmpty)
            {
                return; // Nothing to execute
            }

            _executionCts = new CancellationTokenSource();
            _executionTask = Task.Run(() => ExecuteCommandsAsync(_executionCts.Token));
            _logger.LogDebug("Started movement command execution in {Mode} mode", _movementMode);
        }
    }

    /// <summary>
    /// Stops command execution and clears the queue
    /// </summary>
    public void StopExecution(string reason = "Execution stopped")
    {
        lock (_sync)
        {
            _executionCts?.Cancel();
            
            // Clear the queue
            while (_commandQueue.TryDequeue(out _)) { }
            
            // Cancel any pending room detection wait
            _waitingForRoomChange?.TrySetCanceled();
            _waitingForRoomChange = null;
            _isWaitingForRoomDetection = false;
            
            _currentCommand = null;
            
            _logger.LogInformation("Movement execution stopped: {Reason}", reason);
            ExecutionInterrupted?.Invoke(reason);
        }
    }

    /// <summary>
    /// Pauses execution temporarily (keeps queue intact)
    /// </summary>
    public void PauseExecution(string reason = "Execution paused")
    {
        lock (_sync)
        {
            _executionCts?.Cancel();
            
            // Cancel any pending room detection wait
            _waitingForRoomChange?.TrySetCanceled();
            _waitingForRoomChange = null;
            _isWaitingForRoomDetection = false;
            
            _logger.LogInformation("Movement execution paused: {Reason}", reason);
            ExecutionInterrupted?.Invoke(reason);
        }
    }

    /// <summary>
    /// Resumes paused execution
    /// </summary>
    public void ResumeExecution()
    {
        _logger.LogInformation("Resuming movement execution");
        StartExecution();
    }

    /// <summary>
    /// Clears all queued commands without stopping current execution
    /// </summary>
    public void ClearQueue()
    {
        while (_commandQueue.TryDequeue(out _)) { }
        _logger.LogDebug("Movement command queue cleared");
    }

    /// <summary>
    /// Event handler for room changes - triggers next movement command
    /// </summary>
    private void OnRoomChanged(RoomState newRoom)
    {
        lock (_sync)
        {
            _lastRoomChangeTime = DateTime.UtcNow;
            
            if (_isWaitingForRoomDetection && _waitingForRoomChange != null)
            {
                _logger.LogDebug("Room change detected - triggering next movement command");
                _waitingForRoomChange.TrySetResult(true);
                _waitingForRoomChange = null;
                _isWaitingForRoomDetection = false;
            }
        }
    }

    private async Task ExecuteCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!_commandQueue.TryDequeue(out var command))
                {
                    break; // Queue is empty
                }

                lock (_sync)
                {
                    _currentCommand = command;
                }

                // Wait for minimum interval since last command
                await WaitForMinimumInterval(cancellationToken);

                // Execute the command
                await ExecuteCommand(command, cancellationToken);

                // Wait for room detection (unless this is the last command)
                if (!_commandQueue.IsEmpty)
                {
                    await WaitForRoomDetection(command, cancellationToken);
                }

                lock (_sync)
                {
                    _currentCommand = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Movement command execution was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in movement command execution loop");
        }
        finally
        {
            lock (_sync)
            {
                _currentCommand = null;
                _executionTask = null;
                _executionCts?.Dispose();
                _executionCts = null;
                _isWaitingForRoomDetection = false;
                _waitingForRoomChange = null;
            }
        }
    }

    private async Task WaitForMinimumInterval(CancellationToken cancellationToken)
    {
        var timeSinceLastCommand = DateTime.UtcNow - _lastCommandTime;
        if (timeSinceLastCommand < _minCommandInterval)
        {
            var waitTime = _minCommandInterval - timeSinceLastCommand;
            _logger.LogDebug("Waiting {WaitTime}ms for minimum command interval", waitTime.TotalMilliseconds);
            await Task.Delay(waitTime, cancellationToken);
        }
    }

    private async Task ExecuteCommand(MovementCommand command, CancellationToken cancellationToken)
    {
        try
        {
            CommandExecuting?.Invoke(command.Direction);
            
            _telnetClient.SendCommand(command.Direction);
            _lastCommandTime = DateTime.UtcNow;
            
            _logger.LogDebug("Executed movement command: {Direction} (from {From} to {To})", 
                command.Direction, command.FromRoomId ?? "unknown", command.ToRoomId ?? "unknown");
            
            CommandExecuted?.Invoke(command.Direction);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing movement command: {Direction}", command.Direction);
            throw; // Re-throw to stop execution on command errors
        }
    }

    private async Task WaitForRoomDetection(MovementCommand command, CancellationToken cancellationToken)
    {
        switch (_movementMode)
        {
            case MovementMode.Triggered:
                await WaitForTriggeredRoomDetection(command, cancellationToken);
                break;
                
            case MovementMode.FastWithFallback:
                await WaitForFastRoomDetection(command, cancellationToken);
                break;
                
            case MovementMode.UltraFast:
                await WaitForUltraFastMovement(command, cancellationToken);
                break;
                
            case MovementMode.TimedOnly:
                await WaitForTimedDelay(command, cancellationToken);
                break;
        }
    }

    private async Task WaitForTriggeredRoomDetection(MovementCommand command, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            _waitingForRoomChange = new TaskCompletionSource<bool>();
            _isWaitingForRoomDetection = true;
        }

        try
        {
            _logger.LogDebug("Waiting for room detection after {Direction} command", command.Direction);
            
            // Wait for room change event or timeout
            var timeoutTask = Task.Delay(_fallbackTimeout, cancellationToken);
            var roomChangeTask = _waitingForRoomChange.Task;
            
            var completedTask = await Task.WhenAny(roomChangeTask, timeoutTask);
            
            if (completedTask == timeoutTask)
            {
                _logger.LogWarning("Room detection timeout after {Direction} - continuing anyway", command.Direction);
            }
            else
            {
                _logger.LogDebug("Room change detected after {Direction} - proceeding to next command", command.Direction);
            }
        }
        finally
        {
            lock (_sync)
            {
                _isWaitingForRoomDetection = false;
                _waitingForRoomChange = null;
            }
        }
    }

    private async Task WaitForFastRoomDetection(MovementCommand command, CancellationToken cancellationToken)
    {
        // Fast mode: minimal delay with room detection fallback
        var fastDelay = TimeSpan.FromMilliseconds(200);
        var timeSinceLastRoomChange = DateTime.UtcNow - _lastRoomChangeTime;
        
        if (timeSinceLastRoomChange < fastDelay)
        {
            // Recent room change, proceed immediately
            _logger.LogDebug("Recent room change detected - proceeding immediately");
            return;
        }
        
        // Wait for fast delay with room detection trigger
        lock (_sync)
        {
            _waitingForRoomChange = new TaskCompletionSource<bool>();
            _isWaitingForRoomDetection = true;
        }

        try
        {
            var fastDelayTask = Task.Delay(fastDelay, cancellationToken);
            var roomChangeTask = _waitingForRoomChange.Task;
            
            var completedTask = await Task.WhenAny(roomChangeTask, fastDelayTask);
            
            if (completedTask == roomChangeTask)
            {
                _logger.LogDebug("Room change triggered fast movement");
            }
            else
            {
                _logger.LogDebug("Fast delay elapsed - proceeding");
            }
        }
        finally
        {
            lock (_sync)
            {
                _isWaitingForRoomDetection = false;
                _waitingForRoomChange = null;
            }
        }
    }

    private async Task WaitForUltraFastMovement(MovementCommand command, CancellationToken cancellationToken)
    {
        // Ultra-fast mode: minimal delay based only on command characteristics
        var delay = TimeSpan.FromMilliseconds(50); // Base ultra-fast delay
        
        // Only add delays for actual server-side actions
        if (command.RequiresDoor)
            delay = delay.Add(TimeSpan.FromMilliseconds(200)); // Door opening takes time
            
        if (command.IsHidden)
            delay = delay.Add(TimeSpan.FromMilliseconds(300)); // Hidden exit searching takes time
        
        _logger.LogDebug("Ultra-fast movement: waiting {Delay}ms before next command", delay.TotalMilliseconds);
        await Task.Delay(delay, cancellationToken);
    }

    private async Task WaitForTimedDelay(MovementCommand command, CancellationToken cancellationToken)
    {
        // Original timed mode
        var delay = command.EstimatedDelay;
        _logger.LogDebug("Waiting {Delay}ms (timed mode) before next command", delay.TotalMilliseconds);
        await Task.Delay(delay, cancellationToken);
    }
}

/// <summary>
/// Movement execution modes
/// </summary>
public enum MovementMode
{
    /// <summary>
    /// Wait for room detection events before sending next command (optimal for reliability)
    /// </summary>
    Triggered,
    
    /// <summary>
    /// Fast movement with room detection fallback (optimal for safe areas)
    /// </summary>
    FastWithFallback,
    
    /// <summary>
    /// Ultra-fast movement with minimal delays (trust the path completely)
    /// </summary>
    UltraFast,
    
    /// <summary>
    /// Use only timed delays (original behavior)
    /// </summary>
    TimedOnly
}

/// <summary>
/// Represents a movement command to be executed
/// </summary>
public class MovementCommand
{
    public string Direction { get; set; } = string.Empty;
    public TimeSpan EstimatedDelay { get; set; } = TimeSpan.FromMilliseconds(100);
    public bool RequiresDoor { get; set; }
    public bool IsHidden { get; set; }
    public string? FromRoomId { get; set; }
    public string? ToRoomId { get; set; }
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Represents the current status of the movement queue
/// </summary>
public class MovementQueueStatus
{
    public int QueueCount { get; set; }
    public bool IsExecuting { get; set; }
    public string? CurrentCommand { get; set; }
    public DateTime LastCommandTime { get; set; }
    public bool IsWaitingForRoomDetection { get; set; }
    public MovementMode MovementMode { get; set; }
}