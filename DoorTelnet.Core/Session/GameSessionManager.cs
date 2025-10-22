using System;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Core.Session;

/// <summary>
/// Manages game session state transitions and gates tracking data processing
/// </summary>
public class GameSessionManager
{
    private readonly ILogger<GameSessionManager>? _logger;
    private readonly object _stateLock = new();
    private GameState _currentState = GameState.Disconnected;
    private bool _firstStatsLineAfterConnect = false;
    
    // Detection patterns
    private static readonly Regex UsernamePrompt = new(@"Type your User-ID or ""new"":", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PasswordPrompt = new(@"Type your Password:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StatsPattern = new(@"\[Hp=\d+/Mp=\d+/Mv=\d+(?:/At=\d+)?(?:/Ac=\d+)?\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CharacterMenuPattern = new(@"1\)\s+Enter the Realms of Corinthia", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    
    public GameState CurrentState
    {
        get { lock (_stateLock) return _currentState; }
    }
    
    public event Action<GameState, GameState>? StateChanged;
    public event Action? RequestEnterCommand; // Fired when we detect first stats line after connect
    public event Action? RequestClearCharacterData; // Fired when exiting game
    
    public GameSessionManager(ILogger<GameSessionManager>? logger = null)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Process a line to detect state transitions
    /// </summary>
    public void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        
        lock (_stateLock)
        {
            var previousState = _currentState;
            
            // Check for state transitions based on line content
            if (UsernamePrompt.IsMatch(line))
            {
                TransitionTo(GameState.AtLoginPrompt, previousState);
            }
            else if (PasswordPrompt.IsMatch(line))
            {
                TransitionTo(GameState.AtPasswordPrompt, previousState);
            }
            else if (CharacterMenuPattern.IsMatch(line))
            {
                // Returning to character selection - exit game state
                if (_currentState == GameState.InGame)
                {
                    _logger?.LogInformation("Character menu detected - exiting game state");
                    RequestClearCharacterData?.Invoke();
                }
                TransitionTo(GameState.AtCharacterSelection, previousState);
            }
            else if (StatsPattern.IsMatch(line))
            {
                // Stats line detected - we're in game
                if (_currentState != GameState.InGame)
                {
                    // First stats line after connecting/login
                    if (!_firstStatsLineAfterConnect && 
                        (previousState == GameState.Connected || 
                         previousState == GameState.AtLoginPrompt || 
                         previousState == GameState.AtPasswordPrompt ||
                         previousState == GameState.AtCharacterSelection))
                    {
                        _logger?.LogInformation("First stats line detected after connection - sending enter command");
                        _firstStatsLineAfterConnect = true;
                        
                        // Small delay before sending enter to ensure stats are fully received
                        System.Threading.Tasks.Task.Run(async () =>
                        {
                            await System.Threading.Tasks.Task.Delay(100);
                            RequestEnterCommand?.Invoke();
                        });
                    }
                    
                    TransitionTo(GameState.InGame, previousState);
                }
            }
        }
    }
    
    /// <summary>
    /// Called when TCP connection is established
    /// </summary>
    public void OnConnected()
    {
        lock (_stateLock)
        {
            _logger?.LogInformation("TCP connection established");
            _firstStatsLineAfterConnect = false;
            TransitionTo(GameState.Connected, _currentState);
        }
    }
    
    /// <summary>
    /// Called when TCP connection is lost
    /// </summary>
    public void OnDisconnected()
    {
        lock (_stateLock)
        {
            _logger?.LogInformation("TCP connection lost");
            
            // Clear character data when disconnecting
            if (_currentState == GameState.InGame)
            {
                RequestClearCharacterData?.Invoke();
            }
            
            _firstStatsLineAfterConnect = false;
            TransitionTo(GameState.Disconnected, _currentState);
        }
    }
    
    /// <summary>
    /// Returns true if game data (room/combat/stats) should be processed
    /// </summary>
    public bool ShouldProcessGameData()
    {
        lock (_stateLock)
        {
            return _currentState == GameState.InGame;
        }
    }
    
    private void TransitionTo(GameState newState, GameState previousState)
    {
        if (_currentState == newState) return;
        
        _logger?.LogInformation("Game state transition: {PreviousState} -> {NewState}", _currentState, newState);
        _currentState = newState;
        
        try
        {
            StateChanged?.Invoke(previousState, newState);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in StateChanged event handler");
        }
    }
}

/// <summary>
/// Game session states
/// </summary>
public enum GameState
{
    Disconnected,           // Not connected to server
    Connected,              // TCP connected but haven't seen any prompts yet
    AtLoginPrompt,          // At "Type your User-ID" prompt
    AtPasswordPrompt,       // At "Type your Password" prompt
    AtCharacterSelection,   // At character/incarnation selection menu
    InGame                  // Actually in game (detected via stats line)
}
