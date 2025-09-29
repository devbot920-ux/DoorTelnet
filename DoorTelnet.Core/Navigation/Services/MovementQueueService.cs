using System.Collections.Concurrent;
using DoorTelnet.Core.Navigation.Models;
using DoorTelnet.Core.Telnet;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Core.Navigation.Services;

/// <summary>
/// Manages queued movement commands with timing and interruption support
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

    // Configuration
    private TimeSpan _defaultCommandDelay = TimeSpan.FromSeconds(1.5);
    private TimeSpan _minCommandDelay = TimeSpan.FromMilliseconds(500);
    private TimeSpan _maxCommandDelay = TimeSpan.FromSeconds(5);

    public MovementQueueService(TelnetClient telnetClient, ILogger<MovementQueueService> logger)
    {
        _telnetClient = telnetClient;
        _logger = logger;
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
                    LastCommandTime = _lastCommandTime
                };
            }
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

        _logger.LogInformation("Queued {Count} movement commands", commands.Count());
        
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
            EstimatedDelay = delay ?? _defaultCommandDelay
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
            _logger.LogDebug("Started movement command execution");
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
    /// Sets the default delay between commands
    /// </summary>
    public void SetDefaultDelay(TimeSpan delay)
    {
        _defaultCommandDelay = TimeSpan.FromMilliseconds(
            Math.Clamp(delay.TotalMilliseconds, _minCommandDelay.TotalMilliseconds, _maxCommandDelay.TotalMilliseconds));
        
        _logger.LogDebug("Default command delay set to {Delay}ms", _defaultCommandDelay.TotalMilliseconds);
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

                // Wait for appropriate delay since last command
                var timeSinceLastCommand = DateTime.UtcNow - _lastCommandTime;
                var requiredDelay = command.EstimatedDelay;
                
                if (timeSinceLastCommand < requiredDelay)
                {
                    var waitTime = requiredDelay - timeSinceLastCommand;
                    _logger.LogDebug("Waiting {WaitTime}ms before executing {Direction}", 
                        waitTime.TotalMilliseconds, command.Direction);
                    
                    await Task.Delay(waitTime, cancellationToken);
                }

                // Execute the command
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
                    // Continue with next command - don't fail the entire queue
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
            }
        }
    }
}

/// <summary>
/// Represents a movement command to be executed
/// </summary>
public class MovementCommand
{
    public string Direction { get; set; } = string.Empty;
    public TimeSpan EstimatedDelay { get; set; } = TimeSpan.FromSeconds(1.5);
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
}