# DoorTelnet Engineering Guidelines

## Project Overview

DoorTelnet is a .NET 8 WPF-based telnet client designed for MUD (Multi-User Dungeon) gaming, featuring automation, scripting, and real-time game state tracking. The architecture follows a clean separation between core business logic and presentation layers.

## Project Structure

The solution consists of two primary projects:

### DoorTelnet.Core
**Business Logic Layer** - Contains all core functionality, game logic, and data processing:

```
DoorTelnet.Core/
├── Automation/          # Rule engine and stats tracking
├── Combat/              # Combat event processing and tracking
├── Player/              # Player profiles, credentials, settings
├── Scripting/           # Lua scripting engine integration
├── Telnet/              # Telnet protocol implementation
├── Terminal/            # Screen buffer and ANSI processing
└── World/               # Room tracking and game world state
```

### DoorTelnet.Wpf
**Presentation Layer** - WPF UI, view models, and UI services:

```
DoorTelnet.Wpf/
├── Controls/            # Custom WPF controls
├── Converters/          # Value converters for data binding
├── Services/            # UI-specific services
├── ViewModels/          # MVVM view models
├── Views/               # XAML views and code-behind
│   └── Dialogs/         # Modal dialog windows
├── Styles/              # WPF styling resources
└── Themes/              # Application themes
```

## Logic Segmentation Principles

### File Size Management for LLM Processing
To ensure files remain manageable for Large Language Model processing:

**Maximum File Sizes:**
- Core logic files: **≤ 500 lines**
- ViewModels: **≤ 300 lines**  
- Views (code-behind): **≤ 200 lines**
- Service classes: **≤ 400 lines**

**When files approach these limits:**

1. **Extract Related Functionality** into separate classes
2. **Create Specialized Services** for complex operations
3. **Use Partial Classes** for large ViewModels when appropriate
4. **Split Complex Parsers** into focused, single-responsibility classes

### Functional Decomposition Examples

**❌ Avoid Monolithic Classes:**
```csharp
// DON'T: One massive CombatProcessor handling everything
public class CombatProcessor 
{
    // 800+ lines handling parsing, tracking, calculations, events...
}
```

**✅ Prefer Focused, Composable Classes:**
```csharp
// DO: Split into focused responsibilities
public class CombatLineParser { }      // 150 lines - parsing only
public class CombatTracker { }         // 200 lines - state tracking
public class CombatEventProcessor { }  // 100 lines - event handling
public class CombatStatistics { }     // 120 lines - calculations
```

### Namespace Organization
```csharp
// Core business logic - no UI dependencies
namespace DoorTelnet.Core.Combat
namespace DoorTelnet.Core.World  
namespace DoorTelnet.Core.Player

// UI layer - depends on Core
namespace DoorTelnet.Wpf.ViewModels
namespace DoorTelnet.Wpf.Services
namespace DoorTelnet.Wpf.Views
```

## Functionality Implementation Rules

### ⚠️ Critical: Do Not Assume Functionality

**Always verify existing functionality before extending:**

1. **Search the codebase** for existing implementations
2. **Check existing patterns** and follow established conventions  
3. **Only implement what is explicitly requested**
4. **Do not add features** that aren't specifically asked for
5. **Ask for clarification** if requirements are ambiguous

### ❌ Common Anti-Patterns to Avoid:
```csharp
// DON'T assume UI elements exist
var button = FindButton("SaveButton"); // May not exist!

// DON'T assume methods exist
player.SaveToDatabase(); // Database layer may not be implemented!

// DON'T add unrequested features  
public void AutoSaveEvery5Minutes() { } // Not requested!
```

### ✅ Proper Implementation Approach:
```csharp
// DO: Check if functionality exists first
var existingService = serviceProvider.GetService<ISaveService>();
if (existingService != null) 
{
    // Use existing service
}

// DO: Follow existing patterns
// Check how similar functionality is implemented elsewhere
```

## Architecture Patterns

### Dependency Injection
The application uses Microsoft.Extensions.DependencyInjection throughout:

```csharp
// Register services in App.xaml.cs
services.AddSingleton<RoomTracker>();
services.AddSingleton<CombatTracker>();
services.AddTransient<SettingsViewModel>();

// Inject dependencies via constructor
public class CombatViewModel(CombatTracker combatTracker, PlayerProfile profile)
{
    private readonly CombatTracker _combatTracker = combatTracker;
    private readonly PlayerProfile _profile = profile;
}
```

### Event-Driven Architecture
Core components communicate through events to maintain loose coupling:

```csharp
// Publishers raise events
public event Action<RoomState>? RoomChanged;

// Subscribers handle events  
roomTracker.RoomChanged += OnRoomChanged;
```

### MVVM Pattern (WPF Layer)
```csharp
// ViewModels extend ViewModelBase and use CommunityToolkit.Mvvm
[ObservableProperty] private string _playerName = "";

[RelayCommand]
private async Task ConnectAsync()
{
    // Command logic here
}
```

## Code Quality Standards

### Method Formatting
**Each statement on its own line for clarity:**

```csharp
// ✅ CORRECT: Each statement on separate line
public void ProcessCombatLine(string line)
{
    var match = _combatRegex.Match(line);
    if (!match.Success) 
        return;
        
    var damage = int.Parse(match.Groups["damage"].Value);
    var target = match.Groups["target"].Value;
    
    UpdateCombatStats(damage, target);
    RaiseCombatEvent(new CombatEvent { Damage = damage, Target = target });
}

// ❌ INCORRECT: Multiple statements per line
public void ProcessCombatLine(string line)
{
    var match = _combatRegex.Match(line); if (!match.Success) return;
    var damage = int.Parse(match.Groups["damage"].Value); var target = match.Groups["target"].Value;
}
```

### Method Separation
**Always separate methods with blank lines:**

```csharp
public class RoomTracker
{
    public void UpdateCurrentRoom(RoomState room)
    {
        CurrentRoom = room;
        RoomChanged?.Invoke(room);
    }

    public void AddMonster(Monster monster)  
    {
        _monsters.Add(monster);
        MonsterAdded?.Invoke(monster);
    }

    private void ValidateRoomState(RoomState room)
    {
        if (string.IsNullOrEmpty(room.Name))
            throw new ArgumentException("Room name required");
    }
}
```

### Regex Compilation
**Always compile frequently-used regex patterns:**

```csharp
public class CombatLineParser 
{
    // ✅ Pre-compiled regex for performance
    private static readonly Regex DamagePattern = new(
        @"You hit (?<target>\w+) for (?<damage>\d+) points of damage", 
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ❌ Avoid creating regex on every call
    public bool ParseDamage(string line)
    {
        var regex = new Regex(@"pattern"); // Creates new regex each time!
        return regex.IsMatch(line);
    }
}
```

### Thread Safety
**Always protect shared mutable state:**

```csharp
public class RoomTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RoomState> _rooms = new();

    public void UpdateRoom(string key, RoomState room)
    {
        lock (_sync)
        {
            _rooms[key] = room;
        }
    }

    public RoomState? GetRoom(string key)
    {
        lock (_sync)
        {
            return _rooms.TryGetValue(key, out var room) ? room : null;
        }
    }
}
```

### Exception Handling
**Graceful degradation with logging:**

```csharp
public bool ProcessLine(string line)
{
    try 
    {
        // Processing logic here
        return true;
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Failed to process line: {Line}", line);
        return false; // Graceful degradation, don't crash
    }
}
```

### Resource Management
**Proper disposal patterns:**

```csharp
public class TelnetClient : IDisposable
{
    private readonly TcpClient _client = new();
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        
        _client?.Close();
        _client?.Dispose();
        _disposed = true;
    }
}
```

## Security Best Practices

### Data Protection
```csharp
/// <summary>
/// Encrypts sensitive data using Windows DPAPI
/// </summary>
/// <param name="plainText">Plain text to encrypt</param>
/// <returns>Encrypted byte array</returns>
private byte[] Protect(string plainText)
{
    var data = Encoding.UTF8.GetBytes(plainText);
    return ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
}

/// <summary>
/// Decrypts protected data
/// </summary>
/// <param name="protectedData">Encrypted byte array</param>
/// <returns>Decrypted plain text</returns>
private string Unprotect(byte[] protectedData)
{
    var data = ProtectedData.Unprotect(protectedData, null, DataProtectionScope.CurrentUser);
    return Encoding.UTF8.GetString(data);
}
```

### Input Validation
```csharp
if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
{
    MessageBox.Show("Please enter both username and password.");
    return;
}
```

### Safe Parsing
```csharp
// No exceptions on invalid input
if (int.TryParse(m.Groups["hp"].Value, out var hp))
{
    // Process valid integer
}
```

## Documentation Standards

### XML Documentation
```csharp
/// <summary>
/// Core combat tracking service that monitors damage, deaths, and experience
/// </summary>
/// <param name="roomTracker">Optional dependency for room monster matching</param>
public CombatTracker(RoomTracker? roomTracker = null)
```

### Inline Comments
```csharp
// Track if we've seen first stats line to trigger initial commands
private bool _hasSeenFirstStats = false;
```

### Region Organization
```csharp
#region Combat Statistics Methods
// Related methods grouped together
#endregion
```

## Dependency Management

### External Packages
- **MoonSharp**: Lua scripting engine for automation
- **Microsoft.Extensions.Logging**: Structured logging
- **System.Security.Cryptography.ProtectedData**: Credential encryption
- **CommunityToolkit.Mvvm**: MVVM helpers for WPF

### Internal Dependencies
- Core project has no UI dependencies
- WPF project references Core project only  
- Clear unidirectional dependency flow: WPF → Core

## Extension Points

### Scripting System
```csharp
_script.Globals["onMatch"] = (Action<string, DynValue>)((pattern, func) => {
    // User-defined automation scripts
});
```

### Rule Engine
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

### Combat Tracking
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

## File Organization Checklist

Before creating or modifying files, ensure:

- [ ] **Single Responsibility**: Each class has one clear purpose
- [ ] **Appropriate Size**: Files stay under recommended line limits
- [ ] **Logical Grouping**: Related functionality is co-located
- [ ] **Clear Dependencies**: Dependencies flow in one direction
- [ ] **Namespace Alignment**: File location matches namespace structure

## Implementation Checklist

Before implementing features:

- [ ] **Verify existing functionality** - search codebase thoroughly
- [ ] **Follow established patterns** - check how similar features are implemented
- [ ] **Implement only what's requested** - no additional unrequested features
- [ ] **Check file sizes** - split large classes into focused components
- [ ] **Use dependency injection** - constructor-based injection preferred
- [ ] **Add proper logging** - use ILogger with structured logging
- [ ] **Handle exceptions gracefully** - never crash the application
- [ ] **Include XML documentation** - document all public APIs
- [ ] **Follow thread safety rules** - protect shared mutable state
- [ ] **Validate inputs** - never trust external data

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
- [ ] File sizes remain manageable for LLM processing
- [ ] No assumptions about non-existent functionality
- [ ] Follows established project patterns

When contributing to this project, follow these established patterns and maintain consistency with the existing codebase architecture. **Always verify existing functionality before implementing new features, and keep file sizes manageable by splitting complex logic into focused, single-responsibility classes.**