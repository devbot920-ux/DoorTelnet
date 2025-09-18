using DoorTelnet.Core.Automation;
using DoorTelnet.Core.Scripting;
using DoorTelnet.Core.Terminal;
using DoorTelnet.Core.Telnet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using DoorTelnet.Core.Player;
using DoorTelnet.Core.World;
using DoorTelnet.Core.Combat;
using DoorTelnet.Cli;
using System.Windows.Forms;

var builder = Host.CreateApplicationBuilder(args);

// Make config file mandatory so we fail fast if missing.
builder.Configuration
       .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
       .AddEnvironmentVariables();

builder.Logging.ClearProviders();
// I dont want console logging
// builder.Logging.AddConsole();

// Add provider instance registration
var uiLogProviderInstance = new UiLogProvider();
builder.Logging.AddProvider(uiLogProviderInstance);
builder.Services.AddSingleton(uiLogProviderInstance);

builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    int cols = cfg.GetValue<int>("terminal:cols", 80);
    int rows = cfg.GetValue<int>("terminal:rows", 25);
    return new ScreenBuffer(cols, rows);
});

builder.Services.AddSingleton<RuleEngine>();
builder.Services.AddSingleton<StatsTracker>();
builder.Services.AddSingleton<PlayerProfile>();
builder.Services.AddSingleton<CombatTracker>(); // Add CombatTracker service

builder.Services.AddSingleton(sp =>
{
    var screen = sp.GetRequiredService<ScreenBuffer>();
    var rules = sp.GetRequiredService<RuleEngine>();
    return new ScriptEngine(screen, rules);
});

builder.Services.AddSingleton(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var screen = sp.GetRequiredService<ScreenBuffer>();
    var rules = sp.GetRequiredService<RuleEngine>();
    var script = sp.GetRequiredService<ScriptEngine>();
    var logger = sp.GetRequiredService<ILogger<TelnetClient>>();
    int cols = cfg.GetValue<int>("terminal:cols", 80);
    int rows = cfg.GetValue<int>("terminal:rows", 25);
    script.InterKeyDelayMs = cfg.GetValue<int>("client:interKeyDelayMs", 30);
    bool diag = cfg.GetValue<bool>("diagnostics:telnet");
    bool rawEcho = cfg.GetValue<bool>("diagnostics:rawEcho");
    bool dumbMode = cfg.GetValue<bool>("diagnostics:dumbMode");
    if (cfg.GetValue<string>("connection:host") is null)
        throw new InvalidOperationException("Required configuration key 'connection:host' not found.");
    return new TelnetClient(cols, rows, script, rules, logger, diag, rawEcho, dumbMode);
});

builder.Services.AddSingleton(sp => new CredentialStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoorTelnet", "creds.json")));
builder.Services.AddSingleton(sp => new CharacterProfileStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoorTelnet", "characters.json")));
builder.Services.AddSingleton(sp => new PlayerStatsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoorTelnet", "playerstats.json")));
builder.Services.AddSingleton(sp => new SettingsStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoorTelnet", "settings.json")));
builder.Services.AddSingleton<RoomTracker>();

builder.Services.AddHostedService<Runner>();

var app = builder.Build();
await app.RunAsync();

// Settings data structure
public class DebugSettings
{
    public bool TelnetDiagnostics { get; set; } = false;
    public bool RawEcho { get; set; } = false;
    public bool EnhancedStatsLineCleaning { get; set; } = true;
    public bool DumbMode { get; set; } = false;
    public string CursorStyle { get; set; } = "underscore";
    public bool AsciiCompatible { get; set; } = false;
}

// Add settings persistence helper
public class SettingsStore
{
    private readonly string _filePath;
    private readonly object _sync = new();

    public SettingsStore(string filePath)
    {
        _filePath = filePath;
        // Ensure directory exists
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public DebugSettings LoadSettings()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return new DebugSettings();
                }

                var json = File.ReadAllText(_filePath);
                return System.Text.Json.JsonSerializer.Deserialize<DebugSettings>(json) ?? new DebugSettings();
            }
            catch (Exception)
            {
                // If loading fails, return default settings
                return new DebugSettings();
            }
        }
    }

    public void SaveSettings(DebugSettings settings)
    {
        lock (_sync)
        {
            try
            {
                var options = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true
                };
                var json = System.Text.Json.JsonSerializer.Serialize(settings, options);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception)
            {
                // Silently fail - settings just won't be persisted
            }
        }
    }
}

class Runner : IHostedService
{
    private readonly IConfiguration _cfg;
    private readonly TelnetClient _client;
    private readonly ScriptEngine _script;
    private readonly ScreenBuffer _screen;
    private readonly ILogger<Runner> _logger;
    private readonly StatsTracker _stats;
    private readonly RuleEngine _rules;
    private readonly PlayerProfile _profile;
    private readonly CredentialStore _creds;
    private readonly CharacterProfileStore _charStore;
    private readonly PlayerStatsStore _statsStore;
    private readonly RoomTracker _roomTracker;
    private readonly CombatTracker _combatTracker; // Add CombatTracker
    private readonly UiLogProvider _uiLogs;
    private readonly SettingsStore _settingsStore; // Add settings store
    private CancellationTokenSource? _cts;
    private Task? _renderTask;
    private Task? _inputTask;
    private Thread? _uiThread;
    private StatsForm? _statsForm;
    private string? _selectedUser;
    private DateTime _lastStatsCapture = DateTime.MinValue;
    private DebugSettings _currentSettings = new(); // Track current settings

    // Enhanced movement tracking
    private string? _lastCommand;
    private DateTime _lastCommandTime = DateTime.MinValue;
    private bool _expectingRoomChange = false;
    private DateTime _lastGongAction = DateTime.MinValue;
    private bool _inGongCycle = false;
    private DateTime _lastAttackTime = DateTime.MinValue;
    private string? _lastSummonedMobFirstLetter;
    private bool _waitingForTimers = false; // wait for At/Ac timers to reach zero before next cycle

    // Auto-shield tracking
    private DateTime _lastShieldCheck = DateTime.MinValue;
    private DateTime _lastShieldAction = DateTime.MinValue;
    private bool _needsShieldCheck = false;
    
    // Auto-heal tracking
    private DateTime _lastHealAction = DateTime.MinValue;
    private bool _needsHealCheck = false;

    // Add main menu detection variables  
    private string? _currentMenuSnapshot;
    private bool _incarnationMenuActive;
    private bool _mainMenuActive; // Add this line
    private readonly Dictionary<int, (string name, string house)> _parsedMenu = new();

    private static readonly ConsoleColor[] FgMap =
    {
        ConsoleColor.Gray, ConsoleColor.Red, ConsoleColor.Green, ConsoleColor.Yellow,
        ConsoleColor.Blue, ConsoleColor.Magenta, ConsoleColor.Cyan, ConsoleColor.White
    };
    private static readonly ConsoleColor[] BgMap =
    {
        ConsoleColor.Black, ConsoleColor.DarkRed, ConsoleColor.DarkGreen, ConsoleColor.DarkYellow,
        ConsoleColor.DarkBlue, ConsoleColor.DarkMagenta, ConsoleColor.DarkCyan, ConsoleColor.Gray
    };

    private readonly Regex _statsRegex = new(@"\[Hp=(?<hp>\d+)/Mp=(?<mp>\d+)/Mv=(?<mv>\d+)(?:/At=(?<at>\d+))?(?:/Ac=(?<ac>\d+))?(?: \((?<state>resting|healing)\))?\]", RegexOptions.Compiled);
    private readonly Regex _spellHeaderForces = new(@"^Sphere of (?<sphere>[a-zA-Z]+) \[(?<rank>\d+)\] - Total (?<sphere2>[a-zA-Z]+) spells \[(?<known>\d+)/(?:\d+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly Regex _spellsTableSeparator = new(@"^-=-=", RegexOptions.Compiled);
    private readonly Regex _spellLine = new(@"^(?<mana>\d+)\s+(?<diff>\d+)\s+(?<nick>[a-zA-Z][a-zA-Z0-9]*)\s+(?<sphere>[A-Z])\s+(?<long>.+?)\s*$", RegexOptions.Compiled);
    private readonly Regex _boostsLine = new(@"^Boosts\s*:\s*(?<vals>.+?)(?:\s*\[Hp=|\s*$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Regex _drainsLine = new(@"^Drains\s*:\s*(?<vals>.+?)(?:\s*\[Hp=|\s*$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Regex _encumbranceRegex = new(@"^Encumbrance:\s*(?<encumbrance>.+?)\s*$", RegexOptions.Compiled);
    private readonly Regex _itemsRegex = new(@"^Items:\s*(?<items>.+?)\s*$", RegexOptions.Compiled);
    private readonly Regex _armedWithRegex = new(@"^You are armed with (?<weapon>.+?)\.\s*$", RegexOptions.Compiled);

    private (char ch, ScreenBuffer.CellAttribute attr)[,]? _front;
    private (char ch, ScreenBuffer.CellAttribute attr)[,]? _back;
    private bool _cursorInvertState;
    private DateTime _lastCursorToggle = DateTime.UtcNow;
    private (int x, int y) _lastCursorPos = (-1, -1);
    private string _cursorStyle = "underscore";
    private PlayerStatsStore.StatSnapshot? _lastSnap;

    public Runner(IConfiguration cfg, TelnetClient client, ScriptEngine script, ScreenBuffer screen, StatsTracker stats, RuleEngine rules, PlayerProfile profile, CredentialStore creds, CharacterProfileStore charStore, PlayerStatsStore statsStore, RoomTracker roomTracker, CombatTracker combatTracker, UiLogProvider uiLogs, SettingsStore settingsStore, ILogger<Runner> logger)
    { _cfg = cfg; _client = client; _script = script; _logger = logger; _screen = screen; _stats = stats; _rules = rules; _profile = profile; _creds = creds; _charStore = charStore; _statsStore = statsStore; _roomTracker = roomTracker; _combatTracker = combatTracker; _uiLogs = uiLogs; _settingsStore = settingsStore; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Load saved settings first
        try
        {
            _currentSettings = _settingsStore.LoadSettings();
            _logger.LogInformation("Loaded saved settings: TelnetDiag={telnetDiag}, RawEcho={rawEcho}, CursorStyle={cursorStyle}", 
                _currentSettings.TelnetDiagnostics, _currentSettings.RawEcho, _currentSettings.CursorStyle);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load saved settings, using defaults");
            _currentSettings = new DebugSettings();
        }

        // Create a ManualResetEventSlim to signal when the form is ready
        var formReadyEvent = new ManualResetEventSlim(false);

        // Remove the initial credential prompt - credentials will be managed in the UI
        _uiThread = new Thread(() =>
        {
            try
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                _statsForm = new StatsForm(_stats, _profile, _uiLogs, _screen, _logger, _creds); // Pass credential store

                // Signal that the form is created and ready
                formReadyEvent.Set();

                System.Windows.Forms.Application.Run(_statsForm);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UI thread failed");
                formReadyEvent.Set(); // Ensure we don't hang waiting for the event
            }
        })
        { IsBackground = true };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();

        // Wait for the form to be ready before hooking up events
        // Use a timeout to prevent hanging indefinitely
        if (formReadyEvent.Wait(TimeSpan.FromSeconds(10)))
        {
            // Give the form a moment to fully initialize its UI components
            await Task.Delay(100);

            // Initialize the form with loaded settings
            _statsForm?.InitializeWithSettings(_currentSettings);

            // Now it's safe to hook up the events
            HookStatsFormCredentialButtons();
            HookCombatTrackerEvents(); // Add combat tracker event hooks
        }
        else
        {
            _logger.LogError("Form initialization timed out");
            throw new InvalidOperationException("UI form failed to initialize within timeout period");
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var host = _cfg.GetValue<string>("connection:host") ?? throw new InvalidOperationException("connection:host missing");
        var port = _cfg.GetValue<int>("connection:port");
        var scriptPath = _cfg.GetValue<string>("script:file") ?? "scripts/sample.lua";
        if (File.Exists(scriptPath)) { _script.DoFile(scriptPath); _logger.LogInformation("Loaded script {path}", scriptPath); }
        else _logger.LogWarning("Script file '{path}' not found.", scriptPath);

        Console.CursorVisible = false;

        // Configure CP437 character mapping mode (prefer saved settings over config)
        bool asciiCompatible = _currentSettings.AsciiCompatible;
        Cp437Map.UseAsciiCompatibleMode = asciiCompatible;
        if (asciiCompatible)
        {
            _logger.LogInformation("Using ASCII-compatible character mapping for better console font support");
        }

        // Configure cursor style (prefer saved settings over config)
        _cursorStyle = _currentSettings.CursorStyle;
        _logger.LogInformation("Using cursor style: {cursorStyle}", _cursorStyle);

        // Configure enhanced stats line cleaning (prefer saved settings over config)
        bool enhancedCleaning = _currentSettings.EnhancedStatsLineCleaning;
        ScreenBuffer.EnhancedStatsLineCleaning = enhancedCleaning;
        _logger.LogInformation("Enhanced stats line cleaning: {enabled}", enhancedCleaning ? "enabled" : "disabled");

        // Ensure screen buffer matches actual console size
        try
        {
            int actualCols = Math.Max(Console.WindowWidth - 1, 80);
            int actualRows = Math.Max(Console.WindowHeight - 2, 24);
            _screen.Resize(actualCols, actualRows);
            _logger.LogInformation("Resized screen buffer to {cols}x{rows} (with status bar at top)", actualCols, actualRows);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resize screen buffer to console size, using default");
        }

        await _client.ConnectAsync(host, port, cancellationToken);

        // Connect the TelnetClient's LineReceived event to both RoomTracker and CombatTracker
        _client.LineReceived += line => 
        {
            _roomTracker.AddLine(line);
            _combatTracker.ProcessLine(line); // Add combat tracking to line processing
        };

        await _client.StartAsync(cancellationToken);
        _renderTask = Task.Run(RenderLoop);
        _inputTask = Task.Run(InputLoop);
    }

    private void HookCombatTrackerEvents()
    {
        try
        {
            // Set the combat tracker reference in the stats form
            _statsForm?.SetCombatTracker(_combatTracker);
            
            // Hook up combat tracker events for logging
            _combatTracker.CombatStarted += combat =>
            {
                _statsForm?.Log($"[Combat] Started fight with '{combat.MonsterName}'");
            };

            _combatTracker.CombatUpdated += combat =>
            {
                // Only log occasionally to avoid spam
                if ((DateTime.UtcNow - combat.StartTime).TotalSeconds % 10 < 1)
                {
                    _statsForm?.Log($"[Combat] Fighting '{combat.MonsterName}' - Dealt: {combat.DamageDealt}, Taken: {combat.DamageTaken}, Duration: {combat.DurationSeconds:F1}s");
                }
            };

            _combatTracker.MonsterDeath += message =>
            {
                _statsForm?.Log($"[Combat] {message}");
            };

            _combatTracker.CombatCompleted += entry =>
            {
                var expText = entry.ExperienceGained > 0 ? $", gained {entry.ExperienceGained} XP" : ", no XP";
                _statsForm?.Log($"[Combat] Completed fight with '{entry.MonsterName}' ({entry.Status}) - Dealt: {entry.DamageDealt}, Taken: {entry.DamageTaken}, Duration: {entry.DurationSeconds:F1}s{expText}");
            };

            _logger.LogInformation("Successfully hooked up combat tracker events");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to hook up combat tracker events");
        }
    }

    private void HookStatsFormCredentialButtons()
    {
        if (_statsForm == null)
        {
            _logger.LogWarning("Cannot hook stats form events - form is null");
            return;
        }

        try
        {
            // Use Invoke to ensure this runs on the UI thread
            if (_statsForm.InvokeRequired)
            {
                _statsForm.Invoke(new Action(HookStatsFormCredentialButtons));
                return;
            }

            // Hook up credential events
            _statsForm.UserSelected += (user) => { _selectedUser = user; _logger.LogInformation("User selected: {user}", user); };
            _statsForm.SendUsernameRequested += () =>
            {
                _logger.LogInformation("SendUsernameRequested event fired. SelectedUser: {user}", _selectedUser);

                // Auto-select first user if none selected and credentials exist
                if (string.IsNullOrEmpty(_selectedUser))
                {
                    var availableUsers = _creds.ListUsernames().OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
                    if (availableUsers.Count > 0)
                    {
                        _selectedUser = availableUsers[0];
                        _logger.LogInformation("Auto-selected first available user: {user}", _selectedUser);
                        _statsForm?.Log($"Auto-selected user: {_selectedUser}");

                        // Update the UI to reflect the selection
                        _statsForm?.SetSelectedUser(_selectedUser);
                    }
                }

                if (!string.IsNullOrEmpty(_selectedUser))
                {
                    _logger.LogInformation("Sending username: {user}", _selectedUser);
                    foreach (var c in _selectedUser) _script.EnqueueImmediate(c);
                    _script.EnqueueImmediate('\r');
                    _logger.LogInformation("Username sent successfully: {user}", _selectedUser);
                }
                else
                {
                    _logger.LogWarning("Cannot send username - no user selected and no saved credentials found");
                }
            };
            _statsForm.SendPasswordRequested += () =>
            {
                _logger.LogInformation("SendPasswordRequested event fired. SelectedUser: {user}", _selectedUser);

                // Auto-select first user if none selected and credentials exist
                if (string.IsNullOrEmpty(_selectedUser))
                {
                    var availableUsers = _creds.ListUsernames().OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
                    if (availableUsers.Count > 0)
                    {
                        _selectedUser = availableUsers[0];
                        _logger.LogInformation("Auto-selected first available user for password: {user}", _selectedUser);
                        _statsForm?.Log($"Auto-selected user for password: {_selectedUser}");

                        // Update the UI to reflect the selection
                        _statsForm?.SetSelectedUser(_selectedUser);
                    }
                }

                if (!string.IsNullOrEmpty(_selectedUser))
                {
                    var pass = _creds.GetPassword(_selectedUser);
                    if (pass != null)
                    {
                        _logger.LogInformation("Sending password for user: {user} (length: {length})", _selectedUser, pass.Length);
                        foreach (var c in pass) _script.EnqueueImmediate(c);
                        _script.EnqueueImmediate('\r');
                        _logger.LogInformation("Password sent successfully for user: {user}", _selectedUser);
                    }
                    else
                    {
                        _logger.LogWarning("No password found for user: {user}", _selectedUser);
                    }
                }
                else
                {
                    _logger.LogWarning("Cannot send password - no user selected and no saved credentials found");
                }
            };
            
            // Hook up settings change events
            _statsForm.DebugSettingsChanged += (settings) =>
            {
                _logger.LogInformation("Debug settings changed: TelnetDiag={telnetDiag}, RawEcho={rawEcho}, Enhanced={enhanced}", 
                    settings.TelnetDiagnostics, settings.RawEcho, settings.EnhancedStatsLineCleaning);
                
                try
                {
                    // Update current settings
                    _currentSettings = settings;
                    
                    // Save settings to persist across sessions
                    _settingsStore.SaveSettings(_currentSettings);
                    _logger.LogInformation("Settings saved to disk");
                    
                    // Apply telnet diagnostics changes immediately
                    ApplyTelnetDiagnosticsChange(settings.TelnetDiagnostics);
                    
                    // Apply raw echo changes immediately  
                    ApplyRawEchoChange(settings.RawEcho);
                    
                    // Apply enhanced stats cleaning changes immediately
                    ApplyEnhancedStatsCleaningChange(settings.EnhancedStatsLineCleaning);
                    
                    // Apply cursor style changes immediately
                    _cursorStyle = settings.CursorStyle;
                    _logger.LogInformation("Cursor style updated to: {cursorStyle}", _cursorStyle);
                    
                    // Apply ASCII compatibility changes immediately
                    Cp437Map.UseAsciiCompatibleMode = settings.AsciiCompatible;
                    _logger.LogInformation("ASCII compatibility mode: {enabled}", settings.AsciiCompatible ? "enabled" : "disabled");
                    
                    _logger.LogInformation("All debug settings applied successfully");
                    _statsForm?.Log("[Settings] All settings applied and saved");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to apply debug settings changes");
                    _statsForm?.Log($"[Settings] Error applying changes: {ex.Message}");
                }
            };
            
            _statsForm.DisconnectRequested += OnDisconnectRequested;
            _statsForm.ReconnectRequested += OnReconnectRequested;

            _logger.LogInformation("Successfully hooked up stats form events");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to hook up stats form events");
        }
    }
    
    private void ApplyTelnetDiagnosticsChange(bool enabled)
    {
        try
        {
            // Note: The TelnetClient diagnostics are set during construction and can't be changed at runtime
            // This would require a more advanced implementation to restart the telnet client with new settings
            // For now, we'll just log that the setting was changed and will take effect on next connection
            _logger.LogInformation("Telnet diagnostics setting changed to {enabled}. Change will take effect on next connection.", enabled);
            _statsForm?.Log($"[Settings] Telnet diagnostics {(enabled ? "enabled" : "disabled")} - will take effect on next connection");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply telnet diagnostics change");
        }
    }
    
    private void ApplyRawEchoChange(bool enabled)
    {
        try
        {
            // Note: Similar to telnet diagnostics, raw echo is set during TelnetClient construction
            // This would require a more advanced implementation to change at runtime
            _logger.LogInformation("Raw echo setting changed to {enabled}. Change will take effect on next connection.", enabled);
            _statsForm?.Log($"[Settings] Raw echo {(enabled ? "enabled" : "disabled")} - will take effect on next connection");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply raw echo change");
        }
    }
    
    private void ApplyEnhancedStatsCleaningChange(bool enabled)
    {
        try
        {
            // This can be changed immediately as it's a static property
            ScreenBuffer.EnhancedStatsLineCleaning = enabled;
            _logger.LogInformation("Enhanced stats line cleaning changed to {enabled}", enabled);
            _statsForm?.Log($"[Settings] Enhanced stats cleaning {(enabled ? "enabled" : "disabled")}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply enhanced stats cleaning change");
        }
    }
    
    private async void OnDisconnectRequested()
    {
        _logger.LogInformation("Disconnect requested via UI");
        try
        {
            await _client.StopAsync();
            _logger.LogInformation("Telnet client disconnected successfully");
            _statsForm?.Log("Disconnected from server");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during disconnect");
            _statsForm?.Log($"Error during disconnect: {ex.Message}");
        }
    }
    
    private async void OnReconnectRequested()
    {
        _logger.LogInformation("Reconnect requested via UI");
        try
        {
            // First disconnect if connected
            try
            {
                await _client.StopAsync();
                _logger.LogInformation("Disconnected existing connection");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during disconnect before reconnect");
            }
            
            // Wait a moment for cleanup
            await Task.Delay(1000);
            
            // Reconnect
            var host = _cfg.GetValue<string>("connection:host") ?? throw new InvalidOperationException("connection:host missing");
            var port = _cfg.GetValue<int>("connection:port");
            
            await _client.ConnectAsync(host, port, _cts?.Token ?? CancellationToken.None);
            await _client.StartAsync(_cts?.Token ?? CancellationToken.None);
            
            _logger.LogInformation("Reconnected to {host}:{port}", host, port);
            _statsForm?.Log($"Reconnected to {host}:{port}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during reconnect");
            _statsForm?.Log($"Error during reconnect: {ex.Message}");
        }
    }

    private ConsoleColor HpToColor()
    {
        var r = _stats.HpRatio;
        return r >= 0.75 ? ConsoleColor.Green : r >= 0.5 ? ConsoleColor.Yellow : r >= 0.25 ? ConsoleColor.DarkYellow : ConsoleColor.Red;
    }

    private void ParseStatsFromScreen()
    {
        // Use the line buffer to get unprocessed lines for stats
        var unprocessedLines = _roomTracker.GetUnprocessedLinesForStats();
        foreach (var bufferedLine in unprocessedLines)
        {
            var line = bufferedLine.Content;
            if (line.Contains("Hp=") && _stats.TryParseLine(line, _statsRegex))
            {
                // Mark this line as processed for stats
                _roomTracker.MarkLineProcessedForStats(bufferedLine);
                return;
            }
        }

        // Fallback to screen-based parsing if buffer is empty
        for (int y = _screen.Rows - 1; y >= 0; y--)
        {
            var line = _screen.GetLine(y);
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.Contains("Hp=")) continue;
            if (_stats.TryParseLine(line, _statsRegex)) return;
        }
    }

    private void DetectIncarnationsScreen()
    {
        var text = _screen.ToText();
        if (!text.Contains("Incarnations"))
        {
            _incarnationMenuActive = false; 
            return;
        }
        if (_currentMenuSnapshot == text) 
            return;
        _currentMenuSnapshot = text;
        _incarnationMenuActive = true;
        _parsedMenu.Clear();
        foreach (var e in _charStore.ParseIncarnations(text)) 
            _parsedMenu[e.index] = (e.name, e.house);
        if (_parsedMenu.Count > 0)
        {
            var msg = $"Detected {_parsedMenu.Count} incarnations: {string.Join(", ", _parsedMenu.Select(p => $"{p.Key}:{p.Value.name}"))}";
            _logger.LogInformation(msg);

            // Log to UI safely without blocking the render loop
            _ = Task.Run(() =>
            {
                try
                {
                    _statsForm?.Log(msg);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to log incarnations to UI");
                }
            });
        }
    }

    private void DetectMainMenuScreen()
    {
        var text = _screen.ToText();
        
        // Detect main menu by looking for key phrases from the menu you provided
        bool isMainMenu = text.Contains("The Rose: Council of Guardians") && 
                         text.Contains("Enter the Realms") && 
                         text.Contains("Selection or 'X' to exit:");
        
        if (!isMainMenu)
        {
            _mainMenuActive = false;
            return;
        }
        
        // If we just detected the main menu for the first time
        if (!_mainMenuActive)
        {
            _mainMenuActive = true;
            
            // Reset character stats when returning to main menu
            ResetCharacterStats();
            
            var msg = "Detected main menu - resetting character stats";
            _logger.LogInformation(msg);
            
            // Log to UI safely without blocking the render loop
            _ = Task.Run(() =>
            {
                try
                {
                    _statsForm?.Log($"[Menu Detection] {msg}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to log main menu detection to UI");
                }
            });
        }
    }

    private void ResetCharacterStats()
    {
        try
        {
            // Reset the stats tracker
            _stats.Reset();
            
            // Reset the player profile
            _profile.Reset();
            
            // Clear the last player stats snapshot
            _lastSnap = null;
            
            // Update UI to reflect the reset
            _ = Task.Run(() =>
            {
                try
                {
                    _statsForm?.ClearPlayerStats();
                    _statsForm?.Log("[Reset] Character stats and profile cleared");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update UI after stats reset");
                }
            });
            
            _logger.LogInformation("Character stats reset successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reset character stats");
        }
    }

    private void TryCaptureStatsBlock()
    {
        var text = _screen.ToText();
        if (!text.Contains("Hitpoints:") || !text.Contains("Experience")) return;
        if ((DateTime.UtcNow - _lastStatsCapture).TotalSeconds < 5) return;
        var lines = text.Split('\n');
        var block = string.Join('\n', lines.TakeLast(40));
        var character = _charStore.GetLastCharacter(_selectedUser ?? string.Empty) ?? string.Empty;
        var snap = _statsStore.TryParseBlock(_selectedUser ?? "", character, block);
        if (snap != null)
        {
            _statsStore.AddOrUpdate(snap);
            _lastStatsCapture = DateTime.UtcNow;

            // Update UI safely without blocking the render loop
            _ = Task.Run(() =>
            {
                try
                {
                    _statsForm?.UpdatePlayerStats(snap);
                    _statsForm?.Log($"Captured stats {snap.Character} HP {snap.HP}/{snap.MaxHP}");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update player stats in UI");
                }
            });
        }
    }

    private void ParseProfileRelatedLines()
    {
        // Scan recent buffered lines for hunger message or start of spells / st2 blocks
        // We reuse room tracker's line buffer via screen text (simpler) for now
        var text = _screen.ToText();
        if (text.Contains("Your stomach growls hungrily."))
        {
            if (_profile.Effects.HungerState != "hungry")
            {
                _profile.Effects.HungerState = "hungry";
                _profile.Effects.LastUpdated = DateTime.UtcNow;
                _statsForm?.Log("[Parser] Detected hunger state: hungry");
            }
        }
        // Detect satiated lines from st2 snapshot when present
        if (text.Contains("entirely satiated"))
        {
            if (_profile.Effects.HungerState != "satiated")
            {
                _profile.Effects.HungerState = "satiated";
                _profile.Effects.LastUpdated = DateTime.UtcNow;
                _statsForm?.Log("[Parser] Hunger state: satiated");
            }
        }
        if (text.Contains("not in the least bit thirsty"))
        {
            if (_profile.Effects.ThirstState != "not thirsty")
            {
                _profile.Effects.ThirstState = "not thirsty";
                _profile.Effects.LastUpdated = DateTime.UtcNow;
                _statsForm?.Log("[Parser] Thirst state: not thirsty");
            }
        }
        if (text.Contains("You are shielded."))
        {
            if (!_profile.Effects.Shielded)
            {
                _profile.Effects.Shielded = true; _profile.Effects.LastUpdated = DateTime.UtcNow; _statsForm?.Log("[Parser] Shield detected");
            }
        }
        // Detect shield dissipation for auto-shield feature
        if (text.Contains("shield dissipated") || text.Contains("shield disipated"))
        {
            if (_profile.Effects.Shielded)
            {
                _profile.Effects.Shielded = false;
                _profile.Effects.LastUpdated = DateTime.UtcNow;
                _statsForm?.Log("[Parser] Shield dissipated");
                
                // Trigger immediate shield check if auto-shield is enabled
                if (_profile.Features.AutoShield)
                {
                    _needsShieldCheck = true;
                    _statsForm?.Log("[AutoShield] Shield lost - checking to recast");
                }
            }
        }
        // Basic poison detection phrase
        if (text.Contains("You are poisoned"))
        {
            if (!_profile.Effects.Poisoned) { _profile.Effects.Poisoned = true; _profile.Effects.LastUpdated = DateTime.UtcNow; _statsForm?.Log("[Parser] Poisoned status detected"); }
        }
        // Start parsing spells table if we see the header line pattern and the column header "Nickname"
        if (text.Contains("Nickname") && text.Contains("Sphere of"))
        {
            TryParseSpellsBlock(text);
        }
        // Parse boosts/drains lines from st2 output when present
        if (text.Contains("Body and effects sheet"))
        {
            TryParseEffectsBlock(text);
        }
        
        // Parse inventory when 'inv' command is detected
        if (text.Contains("Encumbrance:") && text.Contains("Items:"))
        {
            TryParseInventoryBlock(text);
        }
    }

    private void TryParseSpellsBlock(string screenText)
    {
        // Extract lines with the spells table header until a blank line or prompt
        var lines = screenText.Split('\n');
        var tableStart = Array.FindIndex(lines, l => l.Contains("Mana") && l.Contains("Nickname"));
        if (tableStart < 0) return;
        var newSpells = new List<DoorTelnet.Core.Player.SpellInfo>();
        for (int i = tableStart + 1; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd();
            if (string.IsNullOrWhiteSpace(raw)) break;
            if (_spellsTableSeparator.IsMatch(raw)) continue;
            var m = _spellLine.Match(raw);
            if (!m.Success) continue;
            if (!int.TryParse(m.Groups["mana"].Value, out var mana)) continue;
            if (!int.TryParse(m.Groups["diff"].Value, out var diff)) diff = 0;
            var nick = m.Groups["nick"].Value;
            var sphereCode = m.Groups["sphere"].Value.FirstOrDefault();
            var longName = m.Groups["long"].Value.Trim();
            newSpells.Add(new DoorTelnet.Core.Player.SpellInfo { Nick = nick, LongName = longName, Mana = mana, Diff = diff, SphereCode = sphereCode });
        }
        if (newSpells.Count > 0)
        {
            // Replace list if changed by nickname snapshot
            bool changed = newSpells.Count != _profile.Spells.Count || newSpells.Any(ns => !_profile.Spells.Any(os => os.Nick == ns.Nick));
            if (changed)
            {
                _profile.Spells = newSpells.OrderBy(s => s.Nick).ToList();
                _profile.Effects.LastUpdated = DateTime.UtcNow;
                _statsForm?.Log($"[Parser] Parsed {newSpells.Count} spells");
                _statsForm?.RefreshProfileExtras();
            }
        }
    }
    private void TryParseEffectsBlock(string screenText)
    {
        var lines = screenText.Split('\n');
        var idx = Array.FindIndex(lines, l => l.Contains("Body and effects sheet"));
        if (idx < 0) return;
        // Limit to next ~15 lines for safety
        var end = Math.Min(lines.Length, idx + 20);
        List<string> boosts = new(); List<string> drains = new(); bool shielded = false; bool poisoned = false; string hunger = _profile.Effects.HungerState; string thirst = _profile.Effects.ThirstState;
        for (int i = idx; i < end; i++)
        {
            var l = lines[i].Trim(); if (l.Length == 0) continue;
            var bm = _boostsLine.Match(l); if (bm.Success) { var val = bm.Groups["vals"].Value.Trim(); if (!val.Equals("none", System.StringComparison.OrdinalIgnoreCase)) boosts = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); continue; }
            var dm = _drainsLine.Match(l); if (dm.Success) { var val = dm.Groups["vals"].Value.Trim(); if (!val.Equals("none", System.StringComparison.OrdinalIgnoreCase)) drains = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(); continue; }
            if (l.Contains("You are shielded")) shielded = true;
            if (l.Contains("You are poisoned")) poisoned = true;
            if (l.Contains("satiated")) hunger = "satiated";
            if (l.Contains("hungr")) hunger = "hungry";
            if (l.Contains("thirsty"))
            {
                if (l.Contains("not") || l.Contains("least bit thirsty")) thirst = "not thirsty";
                else thirst = "thirsty";
            }
        }
        bool changedEffects = shielded != _profile.Effects.Shielded || poisoned != _profile.Effects.Poisoned || hunger != _profile.Effects.HungerState || thirst != _profile.Effects.ThirstState || !boosts.SequenceEqual(_profile.Effects.Boosts) || !drains.SequenceEqual(_profile.Effects.Drains);
        if (changedEffects)
        {
            _profile.Effects.Shielded = shielded;
            _profile.Effects.Poisoned = poisoned;
            _profile.Effects.HungerState = hunger;
            _profile.Effects.ThirstState = thirst;
            _profile.Effects.Boosts = boosts;
            _profile.Effects.Drains = drains;
            _profile.Effects.LastUpdated = DateTime.UtcNow;
            _statsForm?.Log("[Parser] Effects updated: " + ($"Shielded={shielded}, Poisoned={poisoned}, Hunger={hunger}, Thirst={thirst}, Boosts={boosts.Count}, Drains={drains.Count}"));
            _statsForm?.RefreshProfileExtras();
            
            // If we just detected we're not shielded and auto-shield is enabled, trigger reshield attempt
            if (!shielded && _profile.Features.AutoShield && (DateTime.UtcNow - _lastShieldAction).TotalSeconds >= 5)
            {
                _statsForm?.Log("[AutoShield] Detected no shield in st2 - attempting to cast shield");
                _ = Task.Delay(1000).ContinueWith(_ => TryReshield());
            }
        }
    }
    private void TryParseInventoryBlock(string screenText)
    {
        var lines = screenText.Split('\n');
        var encumbranceIdx = Array.FindIndex(lines, l => l.Contains("Encumbrance:"));
        var itemsIdx = Array.FindIndex(lines, l => l.Contains("Items:"));
        var armedIdx = Array.FindIndex(lines, l => l.Contains("You are armed with"));
        
        if (encumbranceIdx < 0 || itemsIdx < 0) return;
        
        string encumbrance = "";
        List<string> items = new();
        string armedWith = "";
        
        // Parse encumbrance line
        var encMatch = _encumbranceRegex.Match(lines[encumbranceIdx].Trim());
        if (encMatch.Success)
        {
            encumbrance = encMatch.Groups["encumbrance"].Value.Trim();
        }
        
        // Parse items - they might span multiple lines
        var itemsText = "";
        for (int i = itemsIdx; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("You are armed with") || line.Contains("[Hp="))
                break;
                
            if (i == itemsIdx)
            {
                // First line with "Items:"
                var itemMatch = _itemsRegex.Match(line);
                if (itemMatch.Success)
                {
                    itemsText = itemMatch.Groups["items"].Value.Trim();
                }
            }
            else
            {
                // Continuation lines
                if (!string.IsNullOrWhiteSpace(line) && !line.Contains("Encumbrance:"))
                {
                    if (!string.IsNullOrEmpty(itemsText)) itemsText += " ";
                    itemsText += line;
                }
            }
        }
        
        // Parse armed with line
        if (armedIdx >= 0)
        {
            var armedMatch = _armedWithRegex.Match(lines[armedIdx].Trim());
            if (armedMatch.Success)
            {
                armedWith = armedMatch.Groups["weapon"].Value.Trim();
            }
        }
        
        // Split items into list
        if (!string.IsNullOrEmpty(itemsText))
        {
            // Remove trailing period and split by comma
            itemsText = itemsText.TrimEnd('.');
            items = itemsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        
        // Update profile if changed
        bool inventoryChanged = !items.SequenceEqual(_profile.Player.Inventory) || 
                               encumbrance != _profile.Player.Encumbrance || 
                               armedWith != _profile.Player.ArmedWith;
        
        if (inventoryChanged)
        {
            _profile.Player.Inventory = items;
            _profile.Player.Encumbrance = encumbrance;
            _profile.Player.ArmedWith = armedWith;
            
            _statsForm?.Log($"[Parser] Inventory updated: {items.Count} items, armed with {(string.IsNullOrEmpty(armedWith) ? "bare hands" : armedWith)}");
            _statsForm?.RefreshProfileExtras();
        }
    }

    private async Task RenderLoop()
    {
        _logger.LogInformation("RenderLoop started");

        while (!_cts!.IsCancellationRequested)
        {
            try
            {
                var snap = _screen.Snapshot();
                ParseStatsFromScreen();
                // After stats parsing attempt automation
                TryRunAutomation();
                TryRunAutoShield(); // Add auto-shield automation
                TryRunAutoHeal(); // Add auto-heal automation
                ParseProfileRelatedLines();
                DetectIncarnationsScreen();
                DetectMainMenuScreen();
                TryCaptureStatsBlock();

                // Add periodic combat tracker cleanup
                _combatTracker.CleanupStaleCombats();

                // Use improved room detection with change detection and movement tracking
                if (_selectedUser != null)
                {
                    var ch = _charStore.GetLastCharacter(_selectedUser) ?? string.Empty;
                    if (!string.IsNullOrEmpty(ch))
                    {
                        // Check for dynamic events that might require immediate room updates
                        var hasDynamicEvents = _roomTracker.HasUnprocessedDynamicEvents();

                        // Enhanced room detection: more aggressive after movement commands OR dynamic events
                        var forceUpdate = (_expectingRoomChange && (DateTime.UtcNow - _lastCommandTime).TotalSeconds < 3) ||
                                         hasDynamicEvents;

                        try
                        {
                            if (_roomTracker.TryUpdateRoom(_selectedUser, ch, _screen.ToText()) || forceUpdate)
                            {
                                var currentRoom = _roomTracker.CurrentRoom;
                                if (currentRoom != null)
                                {
                                    try
                                    {
                                        // Update UI safely without blocking the render loop
                                        _ = Task.Run(() =>
                                        {
                                            try
                                            {
                                                // Use the combined method to show both current room details AND grid
                                                var gridData = _roomTracker.GetRoomGrid(_selectedUser, ch);
                                                _statsForm?.UpdateRoomWithGrid(currentRoom, gridData);
                                            }
                                            catch (Exception ex)
                                            {
                                                _logger.LogWarning(ex, "Failed to update room display in UI");
                                            }
                                        });
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogWarning(ex, "Failed to update room display for room: {roomName}", currentRoom.Name);
                                    }

                                    if (_expectingRoomChange)
                                    {
                                        _logger.LogInformation("Room change detected after movement command: {roomName} (Exits: {exits}, NPCs: {npcs})",
                                                             currentRoom.Name,
                                                             string.Join(", ", currentRoom.Exits),
                                                             currentRoom.Monsters.Count);
                                        _expectingRoomChange = false; // Reset flag
                                    }
                                    else
                                    {
                                        _logger.LogInformation("Room updated: {roomName} (Exits: {exits}, NPCs: {npcs}, Items: {items})",
                                                             currentRoom.Name,
                                                             string.Join(", ", currentRoom.Exits),
                                                             currentRoom.Monsters.Count,
                                                             currentRoom.Items.Count);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to update room tracking for user: {user}, character: {character}", _selectedUser, ch);
                        }
                    }
                }

                var (cx, cy) = _screen.GetCursor();
                if (_front == null || _front.GetLength(0) != snap.GetLength(0) || _front.GetLength(1) != snap.GetLength(1))
                {
                    _front = new (char, ScreenBuffer.CellAttribute)[snap.GetLength(0), snap.GetLength(1)];
                    _back = snap;
                    Console.Clear();
                    _client.NotifyResize(_screen.Columns, _screen.Rows);
                    DrawStatusBar();
                    FullBlit(_back);
                }
                else
                {
                    _back = snap;
                    DrawStatusBar();
                    DiffBlit(_front, _back);
                }

                if ((DateTime.UtcNow - _lastCursorToggle).TotalMilliseconds >= 500)
                { _cursorInvertState = !_cursorInvertState; _lastCursorToggle = DateTime.UtcNow; }

                // Clean up previous cursor position if it changed
                if (_lastCursorPos.x != cx || _lastCursorPos.y != cy)
                {
                    if (_lastCursorPos.x >= 0 && _lastCursorPos.y >= 0 && _front != null &&
                        _lastCursorPos.y < _front.GetLength(0) && _lastCursorPos.x < _front.GetLength(1))
                    {
                        var oldCell = _front[_lastCursorPos.y, _lastCursorPos.x];
                        SafeSetCursor(_lastCursorPos.x, _lastCursorPos.y + 1);
                        ApplyAttr(oldCell.attr, true, oldCell.ch == ' ');
                        Console.Write(oldCell.ch == '\0' ? ' ' : oldCell.ch);
                    }
                    _lastCursorPos = (cx, cy);
                }

                try
                {
                    if (_cursorInvertState)
                    {
                        var saveFg = Console.ForegroundColor; var saveBg = Console.BackgroundColor;
                        Console.ForegroundColor = ConsoleColor.White; Console.BackgroundColor = ConsoleColor.Black;
                        Console.SetCursorPosition(cx, cy + 1);

                        char cursorChar = _cursorStyle.ToLowerInvariant() switch
                        {
                            "block" => '?',
                            "underscore" => '_',
                            "pipe" => '|',
                            "hash" => '#',
                            "dot" => '•',
                            "plus" => '+',
                            _ => '_'
                        };

                        Console.Write(cursorChar);
                        Console.ForegroundColor = saveFg; Console.BackgroundColor = saveBg;
                    }
                    else
                    {
                        if (cx >= 0 && cy >= 0 && _front != null && cy < _front.GetLength(0) && cx < _front.GetLength(1))
                        {
                            var cell = _front[cy, cx];
                            Console.SetCursorPosition(cx, cy + 1);
                            ApplyAttr(cell.attr, true, cell.ch == ' ');
                            Console.Write(cell.ch == '\0' ? ' ' : cell.ch);
                        }
                    }
                }
                catch { }

                await Task.Delay(33, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RenderLoop");
                // Continue the loop even after an error
                await Task.Delay(100, _cts.Token);
            }
        }

        _logger.LogInformation("RenderLoop stopped");
    }

    private void FullBlit((char ch, ScreenBuffer.CellAttribute attr)[,] frame)
    {
        for (int y = 0; y < frame.GetLength(0); y++)
        {
            for (int x = 0; x < frame.GetLength(1); x++)
            {
                var cell = frame[y, x]; if (cell.ch == '\0') cell.ch = ' ';
                SafeSetCursor(x, y + 1); ApplyAttr(cell.attr, true, cell.ch == ' '); Console.Write(cell.ch);
            }
        }
    }

    private void DiffBlit((char ch, ScreenBuffer.CellAttribute attr)[,] front, (char ch, ScreenBuffer.CellAttribute attr)[,] back)
    {
        for (int y = 0; y < back.GetLength(0); y++)
        {
            for (int x = 0; x < back.GetLength(1); x++)
            {
                var n = back[y, x]; var o = front[y, x]; if (n.ch == '\0') n.ch = ' ';
                bool diff = n.ch != o.ch || n.attr.Fg != o.attr.Fg || n.attr.Bg != o.attr.Bg || n.attr.Bold != o.attr.Bold || n.attr.Inverse != o.attr.Inverse || n.attr.Underline != o.attr.Underline;
                if (diff)
                {
                    SafeSetCursor(x, y + 1); ApplyAttr(n.attr, true, n.ch == ' '); Console.Write(n.ch); front[y, x] = n;
                }
            }
        }
    }

    private void SafeSetCursor(int x, int y) { try { Console.SetCursorPosition(x, y); } catch { } }

    private void DrawStatusBar()
    {
        try
        {
            SafeSetCursor(0, 0);
            var server = _cfg.GetValue<string>("connection:host") ?? "?";
            var statsStr = _stats.ToStatusString();
            var hpColor = HpToColor();
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = ConsoleColor.DarkYellow;
            var baseText = $" {server} | "; Console.Write(baseText);
            var hpSegment = statsStr.Split(' ')[0]; Console.ForegroundColor = hpColor; Console.Write(hpSegment);
            Console.ForegroundColor = ConsoleColor.Black; var rest = statsStr.Substring(hpSegment.Length); Console.Write(rest);
            int written = baseText.Length + statsStr.Length;
            if (written < _screen.Columns) Console.Write(new string(' ', _screen.Columns - written));
            Console.ResetColor();
        }
        catch { }
    }

    private async Task InputLoop()
    {
        _logger.LogInformation("InputLoop started");

        while (!_cts!.IsCancellationRequested)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    _logger.LogDebug("Key pressed: {key}", key.Key);

                    if (_incarnationMenuActive && char.IsDigit(key.KeyChar))
                    {
                        int d = key.KeyChar - '0';
                        if (_parsedMenu.TryGetValue(d, out var info) && _selectedUser != null)
                        {
                            _charStore.RecordSelection(_selectedUser, info.name, info.house);
                        }
                        _script.EnqueueImmediate(key.KeyChar);
                        _script.EnqueueImmediate('\r');
                        continue;
                    }

                    // Track movement commands for better room detection
                    string? commandToTrack = null;
                    if (key.Key == ConsoleKey.Enter)
                    {
                        _logger.LogDebug("Enter key pressed, command: '{command}'", _lastCommand ?? "EMPTY");
                        _script.EnqueueImmediate('\r');

                        // If we have a pending command, mark it as sent
                        if (!string.IsNullOrEmpty(_lastCommand))
                        {
                            commandToTrack = _lastCommand;
                            _lastCommand = null;
                            _lastCommandTime = DateTime.UtcNow;

                            // Check if this is a movement command
                            if (IsMovementCommand(commandToTrack))
                            {
                                _expectingRoomChange = true;
                                _logger.LogInformation("Movement command detected: {command} - expecting room change", commandToTrack);
                            }
                        }
                    }
                    else if (key.Key == ConsoleKey.Backspace)
                    {
                        _script.EnqueueImmediate('\b');
                        _script.EnqueueImmediate((char)0x7F);
                        // Remove last character from tracked command
                        if (!string.IsNullOrEmpty(_lastCommand) && _lastCommand.Length > 0)
                        {
                            _lastCommand = _lastCommand.Substring(0, _lastCommand.Length - 1);
                        }
                    }
                    else if (key.Key == ConsoleKey.Tab)
                    {
                        _script.EnqueueImmediate('\t');
                    }
                    else if (key.Key == ConsoleKey.F5)
                    {
                        try { _screen.Resize(Console.WindowWidth - 1, Console.WindowHeight - 2); _client.NotifyResize(_screen.Columns, _screen.Rows); } catch { }
                    }
                    else if (key.Key == ConsoleKey.F9)
                    {
                        var snapText = _screen.ToText();
                        var preview = string.Join("\\n", snapText.Split('\n').Take(10));
                        _logger.LogInformation("SCREEN DUMP:\n{preview}");

                        // Log to UI safely
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                _statsForm?.Log("[Dump]" + preview.Replace('\n', ' '));
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to log screen dump to UI");
                            }
                        });
                    }
                    else if (key.Key == ConsoleKey.F10)
                    {
                        var screenText = _screen.ToText();
                        var debugInfo = RoomParser.DebugParse(screenText);
                        _logger.LogInformation("ROOM PARSER DEBUG:\n{debugInfo}", debugInfo);

                        // Log to UI safely
                        _ = Task.Run(() =>
                        {
                            try
                            {
                                _statsForm?.Log("[Room Debug] Check logs for detailed room parsing information");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to log room debug to UI");
                            }
                        });
                    }
                    else if (key.Key == ConsoleKey.Escape)
                    {
                        _script.EnqueueImmediate('\x1B');
                        _lastCommand = null; // Clear command on escape
                    }
                    else
                    {
                        var ch = key.KeyChar;
                        if (!char.IsControl(ch))
                        {
                            _script.EnqueueImmediate(ch);
                            // Build up the command being typed
                            _lastCommand += ch;
                        }
                    }
                }

                await Task.Delay(4, _cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in InputLoop");
                // Continue the loop even after an error
                await Task.Delay(100, _cts.Token);
            }
        }

        _logger.LogInformation("InputLoop stopped");
    }

    /// <summary>
    /// Check if a command is likely a movement command
    /// </summary>
    private bool IsMovementCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        var cmd = command.Trim().ToLowerInvariant();

        // Direct movement commands
        if (new[] { "n", "s", "e", "w", "ne", "nw", "se", "sw", "u", "d",
                   "north", "south", "east", "west", "northeast", "northwest",
                   "southeast", "southwest", "up", "down" }.Contains(cmd))
        {
            return true;
        }

        // Commands that start with movement
        if (cmd.StartsWith("go ") || cmd.StartsWith("walk ") || cmd.StartsWith("run "))
        {
            return true;
        }

        return false;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    { 
        if (_cts != null) 
        { 
            _cts.Cancel(); 
            try { if (_renderTask != null) await _renderTask; } catch { } 
            try { if (_inputTask != null) await _inputTask; } catch { } 
            Console.ResetColor(); 
            Console.CursorVisible = true; 
        } 
        await _client.StopAsync(); 
    }

    private void ApplyAttr(ScreenBuffer.CellAttribute attr, bool forceReadable = false, bool space = false)
    {
        int fgIndex = attr.Fg >= 0 && attr.Fg < FgMap.Length ? attr.Fg : 7;
        int bgIndex = attr.Bg >= 0 && attr.Bg < BgMap.Length ? attr.Bg : 0;
        var fg = FgMap[fgIndex];
        var bg = BgMap[bgIndex];

        if (attr.Inverse) (fg, bg) = (bg, fg);

        if (forceReadable && !space && fg == bg)
        {
            fg = (bg == ConsoleColor.Black || bg == ConsoleColor.DarkBlue || bg == ConsoleColor.DarkGreen ||
                 bg == ConsoleColor.DarkCyan || bg == ConsoleColor.DarkRed || bg == ConsoleColor.DarkMagenta ||
                 bg == ConsoleColor.DarkYellow) ? ConsoleColor.White : ConsoleColor.Black;
        }

        Console.ForegroundColor = fg;
        Console.BackgroundColor = bg;
    }

    private void TryRunAutomation()
    {
        if (!_profile.Features.AutoGong) return;
        if (_stats.MaxHp <= 0) return;
        var hpPercent = (_stats.Hp * 100) / Math.Max(1, _stats.MaxHp);
        if (hpPercent < _profile.Thresholds.GongMinHpPercent)
        {
            if (_inGongCycle)
            {
                _inGongCycle = false; _waitingForTimers = false; _lastSummonedMobFirstLetter = null;
                _statsForm?.Log($"[AutoGong] Paused - HP {hpPercent}% below threshold {_profile.Thresholds.GongMinHpPercent}%");
            }
            return;
        }
        // We also require attack/action timers to be zero before starting a new cycle
        bool timersReady = _stats.At == 0 && _stats.Ac == 0;
        var room = _roomTracker.CurrentRoom;
        if (room == null) return;
        var now = DateTime.UtcNow;
        var attackCooldownMs = 800;
        var minGongIntervalMs = 2000; // minimal guard interval between gongs even if timers already zero

        // If we finished a cycle and are waiting for timers, keep waiting
        if (_waitingForTimers)
        {
            if (timersReady)
            {
                _waitingForTimers = false;
                _inGongCycle = false;
                _statsForm?.Log("[AutoGong] Timers reset, ready for next gong cycle");
            }
            return;
        }

        if (!_inGongCycle)
        {
            if (!timersReady) return; // don't ring while timers active
            if ((now - _lastGongAction).TotalMilliseconds < minGongIntervalMs) return; // respect minimal interval
            // Initiate cycle
            _inGongCycle = true;
            _lastSummonedMobFirstLetter = null;
            _lastGongAction = now;
            SendCommand("r g");
            _statsForm?.Log("[AutoGong] Rung gong (r g)");
            return;
        }

        // We are in active cycle
        if (_lastSummonedMobFirstLetter == null)
        {
            // Look for summoned mob marker or any new aggressive monster appended with (summoned)
            var mob = room.Monsters.FirstOrDefault(m => m.Name.Contains("(summoned)", StringComparison.OrdinalIgnoreCase));
            if (mob != null)
            {
                var firstLetter = mob.Name.TrimStart().FirstOrDefault(ch => char.IsLetter(ch));
                if (firstLetter != '\0' && (now - _lastAttackTime).TotalMilliseconds > attackCooldownMs)
                {
                    _lastSummonedMobFirstLetter = firstLetter.ToString().ToLowerInvariant();
                    SendCommand($"a {_lastSummonedMobFirstLetter}");
                    _lastAttackTime = now;
                    _statsForm?.Log($"[AutoGong] Attacking summoned mob with 'a {_lastSummonedMobFirstLetter}'");
                }
            }
            return; // wait until attack sent then monitor for death
        }

        // After attack sent, watch for room cleared
        if (!room.Monsters.Any())
        {
            // Only loot once per cycle
            if (!_waitingForTimers)
            {
                if (_profile.Features.PickupGold)
                {
                    SendCommand("g gold");
                    _statsForm?.Log("[AutoGong] Loot gold (g gold)");
                }
                if (_profile.Features.PickupSilver)
                {
                    SendCommand("g sil");
                    _statsForm?.Log("[AutoGong] Loot silver (g sil)");
                }
                // Now wait for attack/action timers to reset to zero before next gong
                _waitingForTimers = true;
                _statsForm?.Log("[AutoGong] Waiting for attack/action timers to reset before next gong");
            }
        }
    }

    private void SendCommand(string text)
    {
        foreach (var c in text)
            _script.EnqueueImmediate(c);
        _script.EnqueueImmediate('\r');
    }

    private void TryRunAutoShield()
    {
        if (!_profile.Features.AutoShield) return;
        
        var now = DateTime.UtcNow;
        bool shouldCheck = false;
        
        // Check immediately if shield was lost
        if (_needsShieldCheck)
        {
            shouldCheck = true;
            _needsShieldCheck = false;
        }
        // Check every 2 minutes for shield status
        else if ((now - _lastShieldCheck).TotalMinutes >= 2)
        {
            shouldCheck = true;
        }
        
        if (shouldCheck)
        {
            _lastShieldCheck = now;
            
            // Check if we have enough time since last shield action (don't spam)
            if ((now - _lastShieldAction).TotalSeconds >= 10)
            {
                // Send st2 to check shield status
                SendCommand("st2");
                _statsForm?.Log("[AutoShield] Checking shield status (st2)");
                
                // Schedule shield casting check after a brief delay to let st2 process
                _ = Task.Delay(2000).ContinueWith(_ => TryReshield());
            }
        }
    }
    
    private void TryReshield()
    {
        if (!_profile.Features.AutoShield) return;
        if (_profile.Effects.Shielded) return; // Already shielded
        
        var now = DateTime.UtcNow;
        
        // Don't spam shield attempts
        if ((now - _lastShieldAction).TotalSeconds < 10) return;
        
        // Find the best shield spell available (priority: aaura > gshield > shield > ppaura)
        var shieldSpells = new[] { "aaura", "gshield", "shield", "ppaura" };
        SpellInfo? bestShield = null;
        
        foreach (var spellNick in shieldSpells)
        {
            bestShield = _profile.Spells.FirstOrDefault(s => s.Nick.Equals(spellNick, StringComparison.OrdinalIgnoreCase));
            if (bestShield != null) break;
        }
        
        if (bestShield == null)
        {
            _statsForm?.Log("[AutoShield] No shield spells found in spell list");
            return;
        }
        
        // Check if we have enough mana (use current MP from stats tracker)
        if (_stats.Mp < bestShield.Mana)
        {
            _statsForm?.Log($"[AutoShield] Not enough mana for {bestShield.Nick} (need {bestShield.Mana}, have {_stats.Mp})");
            return;
        }
        
        // Get character name for targeting
        var characterName = _lastSnap?.Character ?? _charStore.GetLastCharacter(_selectedUser ?? "") ?? "";
        if (string.IsNullOrEmpty(characterName))
        {
            _statsForm?.Log("[AutoHeal] No character name available for heal targeting");
            return;
        }
        
        // Cast the shield spell
        var castCommand = $"cast {bestShield.Nick} {characterName}";
        SendCommand(castCommand);
        _lastShieldAction = now;
        
        _statsForm?.Log($"[AutoShield] Casting {bestShield.Nick} on {characterName} (mana: {bestShield.Mana})");
    }
    
    private void TryRunAutoHeal()
    {
        if (!_profile.Features.AutoHeal) return;
        if (_stats.MaxHp == 0) return;
        if (_stats.Ac > 0) return; // Only heal when not in combat (AC > 0 means in combat)
        if (_stats.At > 0) return; // Only heal when not in combat (AT > 0 means in combat)

        var now = DateTime.UtcNow;
        var hpPercent = (_stats.Hp * 100) / Math.Max(1, _stats.MaxHp);
        
        // Don't spam heal attempts
        if ((now - _lastHealAction).TotalSeconds < 5) return;
        
        // Only heal if we're below the auto-heal threshold
        if (hpPercent >= _profile.Thresholds.AutoHealHpPercent) return;
        
        // Calculate how much HP we need to heal
        var hpDeficit = _stats.MaxHp - _stats.Hp;
        
        // Find the best healing spell available based on deficit
        string[] healSpells;
        if (hpDeficit >= 500)
        {
            healSpells = new[] { "tolife", "superheal", "minheal" };
        }
        else if (hpDeficit >= 200)
        {
            healSpells = new[] { "superheal", "minheal", "tolife" };
        }
        else
        {
            healSpells = new[] { "minheal", "superheal", "tolife" };
        }
        
        SpellInfo? bestHeal = null;
        foreach (var spellNick in healSpells)
        {
            bestHeal = _profile.Spells.FirstOrDefault(s => s.Nick.Equals(spellNick, StringComparison.OrdinalIgnoreCase));
            if (bestHeal != null) break;
        }
        
        if (bestHeal == null)
        {
            _statsForm?.Log("[AutoHeal] No healing spells found in spell list");
            return;
        }
        
        // Check if we have enough mana
        if (_stats.Mp < bestHeal.Mana)
        {
            _statsForm?.Log($"[AutoHeal] Not enough mana for {bestHeal.Nick} (need {bestHeal.Mana}, have {_stats.Mp})");
            return;
        }
        
        // Get character name for targeting
        var characterName = _lastSnap?.Character ?? _charStore.GetLastCharacter(_selectedUser ?? "") ?? "";
        if (string.IsNullOrEmpty(characterName))
        {
            _statsForm?.Log("[AutoHeal] No character name available for heal targeting");
            return;
        }
        
        // Cast the healing spell
        var castCommand = $"cast {bestHeal.Nick} {characterName}";
        SendCommand(castCommand);
        _lastHealAction = now;
        
        var criticalStatus = hpPercent <= _profile.Thresholds.CriticalHpPercent ? " [CRITICAL]" : "";
        _statsForm?.Log($"[AutoHeal] Casting {bestHeal.Nick} on {characterName} (HP: {_stats.Hp}/{_stats.MaxHp} - {hpPercent:F1}%){criticalStatus}");
    }

    private void SaveDebugSettings(DebugSettings settings)
    {
        // Save to settings store (JSON file)
        try
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var settingsFile = Path.Combine(appDataPath, "DoorTelnet", "debug_settings.json");
            var store = new SettingsStore(settingsFile);
            store.SaveSettings(settings);
            _logger.LogInformation("Debug settings saved to: {path}", settingsFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save debug settings");
        }
    }
}