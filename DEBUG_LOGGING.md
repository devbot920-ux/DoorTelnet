# Debug Logging Configuration

## Overview
The DoorTelnet application includes various debug logging options to help diagnose issues with text processing, telnet communication, combat parsing, and navigation.

## Duplicate Debug Messages Fix

### Issue
You may have noticed duplicate debug messages like:
```
?? COMBAT PUNCTUATION CLEANING: 'Exits: north.' -> 'Exits: north' (TelnetClient should have handled this!)
?? COMBAT PUNCTUATION CLEANING: 'Exits: north.' -> 'Exits: north' (TelnetClient should have handled this!)
```

And excessive RAW INPUT messages like:
```
?? RAW INPUT: 'some room description text'
    HEX: 73 6F 6D 65 20 72 6F 6F 6D 20 64 65 73 63 72 69 70 74 69 6F 6E 20 74 65 78 74
```

### Root Cause
- **Combat duplicates** occurred because both `TryParsePlayerDamage` and `TryParseMonsterDamage` methods were performing the same punctuation cleaning and logging separately.
- **RAW INPUT spam** occurred because `RoomTextProcessor` was logging every input line regardless of debug settings.

### Solution
The issues have been fixed by:
1. **Centralizing punctuation cleaning** into a shared `CleanPunctuation` method
2. **Making debug logging conditional** based on configuration settings
3. **Adding separate configuration controls** for combat and room text debugging
4. **Using emojis** to distinguish different types of debug messages

## Configuration Options

### appsettings.json
Control debug logging through the `diagnostics` section in `appsettings.json`:

```json
{
  "diagnostics": {
    "telnet": false,
    "rawEcho": false, 
    "dumbMode": false,
    "combatCleaning": false,
    "roomCleaning": false
  }
}
```

### Debug Options

| Setting | Description | Default | Emoji |
|---------|-------------|---------|-------|
| `telnet` | Enables telnet protocol diagnostic logging | `false` | N/A |
| `rawEcho` | Enables raw input/output logging | `false` | N/A |
| `dumbMode` | Enables dumb terminal mode | `false` | N/A |
| `combatCleaning` | Enables combat text cleaning debug messages | `false` | ?? |
| `roomCleaning` | Enables room text cleaning and RAW INPUT messages | `false` | ???? |

### Enabling Debug Logging

**To see combat punctuation cleaning messages:**
```json
{
  "diagnostics": {
    "combatCleaning": true
  }
}
```

**To see room text processing and RAW INPUT messages:**
```json
{
  "diagnostics": {
    "roomCleaning": true
  }
}
```

**To see both:**
```json
{
  "diagnostics": {
    "combatCleaning": true,
    "roomCleaning": true
  }
}
```

### Programmatic Control

You can also control debug logging programmatically:

```csharp
// Enable/disable combat debug logging
DoorTelnet.Core.Combat.CombatLineParser.SetDebugLogging(true);

// Enable/disable room text debug logging  
DoorTelnet.Core.World.RoomTextProcessor.SetDebugLogging(true);
```

## Debug Message Format

### Combat Messages
When enabled, combat cleaning debug messages use this format:
```
?? COMBAT PUNCTUATION CLEANING: 'original text' -> 'cleaned text' (TelnetClient should have handled this!)
```

### Room Text Messages
When enabled, room text processing shows:
```
?? RAW INPUT: 'readable representation'
    HEX: 48 45 58 20 72 65 70 72 65 73 65 6E 74 61 74 69 6F 6E
?? ROOM TEXT CLEANING APPLIED:
   RAW: 'original text'
   CLEAN: 'cleaned text'
?? BACKUP ANSI CLEANING TRIGGERED: 'before' -> 'after' (TelnetClient should have handled this!)
?? MOVEMENT FILTERED IN ROOMTRACKER: 'n' (TelnetClient should have filtered this)
?? FILTERED TOO SHORT: 'ab' (from original: 'some longer text')
```

The emojis make it easy to identify different types of diagnostic messages:
- ?? = Combat processing warnings
- ?? = Raw input inspection 
- ?? = Text cleaning operations

## Build Configuration

In DEBUG builds, both combat and room debug logging are enabled by default.
In RELEASE builds, both are disabled by default.

This can be overridden by the `appsettings.json` configuration.

## Performance Impact

When debug logging is disabled, there is minimal performance impact as the logging checks are very fast. When enabled, debug logging may impact performance slightly due to string operations and debug output.

For production use, keep all diagnostic options set to `false`.

## Navigation System Status (Phase 2 Complete)

The navigation system has been successfully implemented through Phase 2:

### ? Phase 1: Foundation (Complete)
- GraphDataService for loading `graph.json`
- Room models with strongly-typed data structures
- Basic A* pathfinding with coordinate-based heuristics
- Room matching between parsed rooms and graph nodes

### ? Phase 2: Navigation Service (Complete)
- **MovementQueueService**: Manages timed command execution with interruption support
- **NavigationService**: High-level navigation coordination with safety checks
- **Safety Integration**: Automatic pause/resume during combat or low health
- **UI Integration**: Navigation controls added to RoomView with:
  - Destination input field
  - Go/Stop buttons
  - Pending destination support
  - Real-time status display
- **Configuration**: Navigation feature flags and thresholds in PlayerProfile

### ?? Phase 3: Automation Integration (Planned)
- Enhanced AutomationFeatureService integration
- Advanced hunt route optimization
- Quest routing features
- Store/trainer routing

### ?? Phase 4: Advanced Features (Planned)
- Route sharing and optimization
- Map visualization
- Voice command integration
- Machine learning path optimization

### Navigation UI Controls
The Room panel now includes navigation controls:
- **Destination field**: Enter target room name
- **Go button**: Start immediate navigation
- **Stop button**: Stop current navigation
- **Pending destination**: Navigate when conditions are safe
- **Status display**: Shows current navigation state and progress

### Safety Features
- Health monitoring (pauses below configurable HP threshold)
- Combat detection (pauses during active combat)
- Path validation (avoids dangerous rooms based on player level)
- Real-time safety monitoring with automatic resume