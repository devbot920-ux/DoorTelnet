# DoorTelnet Engineering Guidelines

## Project Overview

DoorTelnet is a .NET 8 Windows application that provides a telnet client for connecting to MUD (Multi-User Dungeon) games, specifically designed for Rose COG. The application consists of two main projects:

- **DoorTelnet.Core**: Core functionality library containing business logic, networking, and game mechanics
- **DoorTelnet.Cli**: Windows Forms-based user interface and presentation layer

## Architecture Patterns

### 1. Layered Architecture
The solution follows a clear separation of concerns:

```
┌─────────────────────────┐
│   Presentation Layer    │  ← DoorTelnet.Cli (WinForms UI)
├─────────────────────────┤
│   Business Logic Layer  │  ← DoorTelnet.Core (Game Logic)
├─────────────────────────┤
│   Data Access Layer     │  ← File-based stores & serialization
└─────────────────────────┘
```

### 2. Observer Pattern
Extensive use of events for loose coupling between components:
- `StatsTracker.Updated` event for real-time statistics
- `UiLogProvider.Message` event for logging
- `CombatTracker` events for combat state changes
- `ScriptEngine` events (`OnConnect`, `OnTick`) for automation

### 3. Strategy Pattern
- **AnsiParser**: Different parsing strategies for terminal control sequences
- **TelnetNegotiation**: Configurable negotiation strategies for different terminal types
- **RuleEngine**: Pluggable regex-based rule system for automation

### 4. Factory Pattern
- **UiLogProvider**: Creates logger instances for different categories
- **ScriptEngine**: Creates and configures MoonSharp scripting instances

### 5. Repository Pattern
Data persistence through specialized stores:
- `PlayerStatsStore`: Player statistics persistence
- `CredentialStore`: Encrypted credential management
- `CharacterProfileStore`: Character profile data

### 6. Command Pattern
- **TelnetClient**: Queue-based command processing with `_outQueue`
- **ScriptEngine**: Command queuing system (`_sendQueue`, `_immediateChars`)

## Code Formatting Standards

### 1. Method and Class Documentation
All public methods and classes must have XML documentation:

```csharp
/// <summary>
/// Processes incoming telnet data and updates the screen buffer
/// </summary>
/// <param name="data">Raw telnet data bytes</param>
/// <param name="cancellationToken">Cancellation token for async operations</param>
/// <returns>True if processing was successful</returns>
public async Task<bool> ProcessDataAsync(byte[] data, CancellationToken cancellationToken)
{
    // Implementation
}
```

### 2. Inline Comments for Complex Logic
Precede complex operations with explanatory comments:

```csharp
// Check if this is a stats line and apply enhanced cleaning to prevent timer artifacts
if (isStatsLine)
{
    // Look for common patterns that indicate timer artifacts
    var patterns = new[] { "/Ac=", "/At=", "Ac=", "At=" };
    
    foreach (var pattern in patterns)
    {
        // Find the end of this pattern and clean artifacts
        int patternStart = currentLine.IndexOf(pattern);
        // ... implementation
    }
}
```

### 3. Statement Separation and Line Breaks
Always add newlines after semicolons for better readability:

```csharp
// ❌ BAD: Multiple statements on one line
CursorY++; CursorX = 0; ScrollIfNeeded(); return;

// ✅ GOOD: Each statement on its own line
CursorY++;
CursorX = 0;
ScrollIfNeeded();
return;
```

### 4. Method Spacing
Separate method definitions with blank lines:

```csharp
/// <summary>
/// Queues a line of text for transmission
/// </summary>
/// <param name="text">Text to queue</param>
public void QueueLine(string text)
{
    lock (_sendQueue)
    {
        _sendQueue.Enqueue(text + "\r\n");
    }
}

/// <summary>
/// Queues raw text without line ending
/// </summary>
/// <param name="text">Raw text to queue</param>
public void QueueRaw(string text)
{
    lock (_sendQueue)
    {
        _sendQueue.Enqueue(text);
    }
}
```

### 5. Property and Field Formatting
Group related properties and fields with appropriate spacing:

```csharp
// Core components
private readonly Script _script;
private readonly RuleEngine _ruleEngine;
private readonly ScreenBuffer _screen;

// Queue management
internal readonly Queue<string> _sendQueue = new();
private readonly Queue<char> _immediateChars = new();

// Configuration properties
public int InterKeyDelayMs { get; set; } = 30;

// Events
public event Action? OnConnect;
public event Action? OnTick;
```

### 6. Constructor Formatting
Format constructors with clear parameter alignment:

```csharp
/// <summary>
/// Initializes a new instance of the ScriptEngine
/// </summary>
/// <param name="screen">Screen buffer for display operations</param>
/// <param name="ruleEngine">Rule engine for automation</param>
public ScriptEngine(
    ScreenBuffer screen, 
    RuleEngine ruleEngine)
{
    _screen = screen;
    _ruleEngine = ruleEngine;
    _script = new Script(CoreModules.Preset_Complete);
    
    RegisterApi();
}
```

### 7. Control Structure Formatting
Always use braces and proper indentation:

```csharp
// ❌ BAD: No braces, inline statements
if (func.Type != DataType.Function) return;

// ✅ GOOD: Braces with proper formatting
if (func.Type != DataType.Function)
{
    return;
}

// Complex conditions with proper line breaks
if (currentLine.Contains("Hp=") || 
    currentLine.Contains("Mp=") ||
    currentLine.Contains("Mv="))
{
    // Process stats line
    ProcessStatsLine(currentLine);
}
```

### 8. Collection and LINQ Formatting
Format complex LINQ operations across multiple lines:

```csharp
// ❌ BAD: Long single line
var monsterStats = completedCombats.GroupBy(c => c.MonsterName).Select(g => new { MonsterName = g.Key, Kills = g.Count() }).OrderByDescending(m => m.Kills).ToList();

// ✅ GOOD: Multi-line with proper indentation
var monsterStats = completedCombats
    .GroupBy(c => c.MonsterName)
    .Select(g => new
    {
        MonsterName = g.Key,
        Kills = g.Count(),
        TotalExperience = g.Sum(c => c.ExperienceGained),
        AverageExperience = g.Average(c => c.ExperienceGained)
    })
    .OrderByDescending(m => m.Kills)
    .ThenByDescending(m => m.TotalExperience)
    .ToList();
```

### 9. Switch Statement Formatting
Use consistent formatting for switch statements:

```csharp
switch (mode)
{
    case 0:
        // Erase to end of line
        if (EnhancedStatsLineCleaning)
        {
            EraseToEndOfLineEnhanced();
        }
        else
        {
            EraseToEndOfLine();
        }
        break;
        
    case 1:
        // Erase from start of line
        EraseFromStartOfLine();
        break;
        
    case 2:
        // Erase entire line
        EraseLine();
        break;
        
    default:
        // Handle unexpected mode
        throw new ArgumentException($"Invalid erase mode: {mode}");
}
```

### 10. Expression-Bodied Members
Use expression-bodied members for simple operations:

```csharp
// Simple property accessors
public bool IsConnected => _tcp?.Connected ?? false;

// Simple method implementations
public void RaiseConnect() => OnConnect?.Invoke();
public void RaiseTick() => OnTick?.Invoke();

// But avoid for complex operations - use full method body instead
public void ComplexOperation()
{
    // Complex logic should use full method body
    lock (_sync)
    {
        // Multiple statements
        ValidateState();
        ProcessData();
        UpdateStatus();
    }
}
```

## Coding Standards

### Naming Conventions

#### Classes and Public Members
```csharp
public class PlayerStatsStore          // PascalCase for classes
public void ProcessLine(string line)   // PascalCase for methods
public string Username { get; set; }   // PascalCase for properties
public event Action? Updated;          // PascalCase for events
```

#### Private Fields and Local Variables
```csharp
private readonly ScreenBuffer _screen;     // _camelCase for private fields
private readonly object _sync = new();     // _camelCase with descriptive names
private static readonly Regex NumberRegex  // PascalCase for static readonly
```

#### Constants and Static Fields
```csharp
private const int MaxEntries = 1000;           // PascalCase for constants
private static readonly string[] DeathWords    // PascalCase for static arrays
```

### Code Organization

#### Namespace Structure
```csharp
namespace DoorTelnet.Core.Automation;    // Feature-based organization
namespace DoorTelnet.Core.Combat;        // Domain-driven namespaces
namespace DoorTelnet.Core.Player;        // Clear functional grouping
namespace DoorTelnet.Core.Terminal;      // Technical layer separation
```

#### File Organization
- One public class per file
- Filename matches the primary class name
- Related classes (like inner classes) kept in same file
- Enums and records co-located with related classes

### Defensive Programming

#### Thread Safety
```csharp
private readonly object _sync = new();

/// <summary>
/// Updates player statistics in a thread-safe manner
/// </summary>
public void UpdateStats()
{
    lock (_sync)
    {
        // Thread-safe operations
    }
}
```

#### Null Safety
```csharp
/// <summary>
/// Processes a line of input if it's not null or empty
/// </summary>
/// <param name="line">Line to process</param>
public void ProcessLine(string? line)
{
    if (string.IsNullOrWhiteSpace(line)) 
    {
        return;
    }
    
    // Process non-null line
}
```

#### Exception Handling
```csharp
try 
{ 
    _script.Call(func, m.Value); 
} 
catch (Exception ex)
{ 
    // Log the exception for debugging
    _logger.LogWarning(ex, "Script execution failed");
}
```

### Memory Management

#### Resource Disposal
```csharp
/// <summary>
/// Properly disposes of all resources
/// </summary>
public void Dispose()
{
    _cts?.Cancel();
    _tcp?.Close();
    _stream?.Dispose();
}
```

#### Efficient String Handling
```csharp
// Reuse StringBuilder for multiple operations
private readonly StringBuilder _currentLine = new();

// Local StringBuilder for formatting
var sb = new StringBuilder();
```

#### Collection Management
```csharp
// Prevent unbounded growth with size limits
if (_lstLog.Items.Count > 1000) 
{
    _lstLog.Items.RemoveAt(0);
}
```

## UI Design Patterns

### 1. MVP (Model-View-Presenter) Influence
- **StatsForm**: Acts as both View and Presenter
- **PlayerProfile**: Model containing game state
- Clear separation between UI logic and business logic

### 2. Event-Driven UI Updates
```csharp
/// <summary>
/// Sets up event handlers for real-time updates
/// </summary>
private void SetupEventHandlers()
{
    _timer.Tick += (_, _) => RefreshSummary();
    _stats.Updated += () => BeginInvoke(new Action(RefreshSummary));
}
```

### 3. Thread-Safe UI Updates
```csharp
/// <summary>
/// Logs a message in a thread-safe manner
/// </summary>
/// <param name="line">Message to log</param>
public void Log(string line)
{ 
    if (InvokeRequired)
    { 
        BeginInvoke(new Action<string>(Log), line); 
        return;
    } 
    
    // Update UI on correct thread
}
```

## Data Modeling Standards

### 1. Immutable Data Transfer Objects
```csharp
/// <summary>
/// Represents a log entry with immutable properties
/// </summary>
/// <param name="Timestamp">When the log entry was created</param>
/// <param name="Level">Severity level of the log entry</param>
/// <param name="Message">Log message content</param>
/// <param name="Exception">Optional exception details</param>
public record UiLogEntry(
    DateTime Timestamp, 
    LogLevel Level, 
    string Message, 
    Exception? Exception);
```

### 2. Configuration Classes
```csharp
/// <summary>
/// Debug and diagnostic configuration settings
/// </summary>
public class DebugSettings
{
    public bool TelnetDiagnostics { get; set; } = false;
    public bool RawEcho { get; set; } = false;
    // Default values for all settings
}
```

### 3. Rich Domain Models
```csharp
/// <summary>
/// Complete player profile containing all game state
/// </summary>
public class PlayerProfile
{
    public PlayerState Player { get; set; } = new();
    public StatusEffects Effects { get; set; } = new();
    public FeatureFlags Features { get; set; } = new();
    // Composed of related sub-models
}
```

## Performance Considerations

### 1. Lazy Initialization
```csharp
// Null until needed
private static byte[]? _keyCache;
```

### 2. Object Pooling
```csharp
// Reuse concurrent queue for performance
private readonly ConcurrentQueue<byte> _outQueue = new();
```

### 3. Efficient Parsing
```csharp
// Pre-compiled regex for performance
private static readonly Regex StatsPattern = new(
    @"\[Hp=\d+/Mp=\d+/Mv=\d+(?:/At=\d+)?(?:/Ac=\d+)?\]",
    RegexOptions.Compiled | RegexOptions.IgnoreCase);
```

## Testing Strategy

### Test Structure (Inferred)
- Unit tests for core business logic
- Integration tests for telnet communication
- UI tests for form interactions
- Performance tests for real-time parsing

### Testable Design
- Dependency injection ready (constructor parameters)
- Interface-based abstractions where needed
- Pure functions for parsing logic
- Event-driven architecture enables testing

## Security Practices

### 1. Credential Protection
```csharp
/// <summary>
/// Encrypts sensitive data using Windows DPAPI
/// </summary>
/// <param name="plain">Plain text to encrypt</param>
/// <returns>Encrypted byte array</returns>
private byte[] Protect(string plain)

/// <summary>
/// Decrypts protected data
/// </summary>
/// <param name="protectedData">Encrypted byte array</param>
/// <returns>Decrypted plain text</returns>
private string Unprotect(byte[] protectedData)
```

### 2. Input Validation
```csharp
if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
{
    MessageBox.Show("Please enter both username and password.");
    return;
}
```

### 3. Safe Parsing
```csharp
// No exceptions on invalid input
if (int.TryParse(m.Groups["hp"].Value, out var hp))
{
    // Process valid integer
}
```

## Documentation Standards

### 1. XML Documentation
```csharp
/// <summary>
/// Core combat tracking service that monitors damage, deaths, and experience
/// </summary>
/// <param name="roomTracker">Optional dependency for room monster matching</param>
public CombatTracker(RoomTracker? roomTracker = null)
```

### 2. Inline Comments
```csharp
// Track if we've seen first stats line to trigger initial commands
private bool _hasSeenFirstStats = false;
```

### 3. Region Organization
```csharp
#region Combat Statistics Methods
// Related methods grouped together
#endregion
```

## Dependency Management

### 1. External Packages
- **MoonSharp**: Lua scripting engine for automation
- **Microsoft.Extensions.Logging**: Structured logging
- **System.Security.Cryptography.ProtectedData**: Credential encryption

### 2. Internal Dependencies
- Core project has no UI dependencies
- CLI project references Core project only
- Clear unidirectional dependency flow

## Extension Points

### 1. Scripting System
```csharp
_script.Globals["onMatch"] = (Action<string, DynValue>)((pattern, func) => {
    // User-defined automation scripts
});
```

### 2. Rule Engine
```csharp
/// <summary>
/// Adds a new automation rule
/// </summary>
/// <param name="pattern">Regex pattern to match</param>
/// <param name="callback">Action to execute on match</param>
public void Add(string pattern, Action<Match> callback)
{
    // Extensible regex-based automation
}
```

### 3. Combat Tracking
```csharp
/// <summary>
/// Processes a line for combat events
/// </summary>
/// <param name="line">Line to analyze</param>
/// <returns>True if combat event was detected</returns>
public bool ProcessLine(string line)
{
    // Pluggable combat event detection
}
```

## Best Practices Summary

1. **Code Formatting**: Use consistent spacing, commenting, and line breaks
2. **Documentation**: XML docs for all public APIs, inline comments for complex logic
3. **Immutability**: Prefer readonly fields and immutable data structures
4. **Thread Safety**: Always use locks for shared mutable state
5. **Error Handling**: Graceful degradation with logging, avoid crashes
6. **Resource Management**: Proper disposal patterns and cleanup
7. **Event-Driven**: Loose coupling through events and observers
8. **Separation of Concerns**: Clear boundaries between layers
9. **Performance**: Pre-compile regex, reuse objects, efficient collections
10. **Security**: Encrypt sensitive data, validate all inputs
11. **Testability**: Constructor injection, pure functions, mockable interfaces
12. **Readability**: Clear method names, proper spacing, meaningful comments

## Code Review Checklist

Before submitting code, ensure:

- [ ] All public methods have XML documentation
- [ ] Complex logic has explanatory comments
- [ ] Each statement is on its own line (no semicolon chaining)
- [ ] Methods are separated by blank lines
- [ ] Proper exception handling with logging
- [ ] Thread-safe operations where needed
- [ ] Consistent naming conventions followed
- [ ] Resource disposal implemented where applicable
- [ ] Input validation for public methods
- [ ] Performance considerations addressed

When contributing to this project, follow these established patterns and maintain consistency with the existing codebase architecture.