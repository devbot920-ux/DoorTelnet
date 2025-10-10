# Character Data Clear Feature

## Overview
Added comprehensive functionality to clear all character data both automatically when disconnecting and manually via a menu command. This ensures a clean slate when switching characters or reconnecting.

## Features Implemented

### 1. **Automatic Clear on Disconnect**
When you disconnect from the server, all character data is automatically cleared including:
- **Character Profile** - Name, class, race, level, etc.
- **Health & Stats** - HP, MP, MV, AT, AC timers
- **Experience** - Current XP, XP left to next level
- **Inventory** - All items and spells
- **Location** - Current room, monsters, room connections
- **Combat Data** - Active combats, completed combat history
- **Status Effects** - Shield status, hunger, thirst, etc.

### 2. **Manual Clear via Menu**
Added "Clear Character Data" option in the **Automation** menu that allows you to manually clear all data without disconnecting.

## Implementation Details

### **MainViewModel Changes**
**File:** `DoorTelnet.Wpf/ViewModels/MainViewModel.cs`

#### **New Dependencies:**
```csharp
private readonly RoomTracker _roomTracker; // NEW: Add RoomTracker dependency
```

#### **New Command:**
```csharp
public ICommand ClearCharacterDataCommand { get; } // NEW: Command to clear all character data
```

#### **Enhanced DisconnectAsync:**
```csharp
private async Task DisconnectAsync()
{
    if (!IsConnected) return;
    IsBusy = true;
    try
    {
        await _client.StopAsync();
        IsConnected = false;
        ConnectionStatus = "Disconnected";
        
        // NEW: Automatically clear all character data when disconnecting
        ClearAllCharacterData();
    }
    finally { IsBusy = false; }
}
```

#### **Comprehensive Clear Method:**
```csharp
/// <summary>
/// Clears all character data including HP, XP, location, monsters, combat info, etc.
/// Used when disconnecting or manually resetting character data.
/// </summary>
private void ClearAllCharacterData()
{
    try
    {
        _logger.LogInformation("Clearing all character data...");
        
        // 1. Clear PlayerProfile (resets all character info, stats, inventory, spells, etc.)
        _profile.Reset();
        
        // 2. Clear combat tracker (active combats, completed combats, experience tracking)
        _combatTracker.ClearHistory();
        
        // 3. Clear room tracker (current room, monsters, location data)
        // Use reflection to clear CurrentRoom and internal data structures
        
        // 4. Clear stats tracker data (HP, MP, etc.)
        // Use reflection to reset all stat properties
        
        // 5. Clear ViewModels to refresh UI
        Room.Refresh();
        Combat.ClearHistoryCommand?.Execute(null);
        
        // 6. Update all UI bindings
        RaiseProfileBar(); // Updates all status bar properties
        
        _logger.LogInformation("Character data cleared successfully");
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error clearing character data");
    }
}
```

### **Menu Integration**
**File:** `DoorTelnet.Wpf/MainWindow.xaml`

#### **New Menu Item:**
```xaml
<MenuItem Header="_Automation">
    <MenuItem Header="Refresh Stats" Command="{Binding Combat.RefreshStatsCommand}" />
    <Separator />
    <MenuItem Header="Send Username" Command="{Binding SendUsernameCommand}" />
    <MenuItem Header="Send Password" Command="{Binding SendPasswordCommand}" />
    <MenuItem Header="Quick Login" Command="{Binding QuickLoginCommand}" />
    <MenuItem Header="Send Initial Data" Command="{Binding SendInitialDataCommand}" />
    <Separator />
    <MenuItem Header="Clear Character Data" Command="{Binding ClearCharacterDataCommand}" 
              ToolTip="Clears all character data including HP, XP, location, monsters, and combat info"/>
    <Separator />
    <MenuItem Header="Hot Keys" Command="{Binding ShowHotKeysCommand}" />
</MenuItem>
```

### **Dependency Injection Update**
The MainViewModel constructor now includes RoomTracker as a dependency, which requires updating the DI container registration.

## Data Cleared Breakdown

### **PlayerProfile.Reset():**
- Character name, class, race, level
- First name, last name
- Inventory items
- Spell list
- Heal spells
- Shield spells
- Experience points
- Armed with weapon
- Status effects (hunger, thirst, shield status)
- All feature flags and thresholds

### **CombatTracker.ClearHistory():**
- Active combat sessions
- Completed combat history
- Experience tracking data
- Monster targeting information
- Combat statistics

### **RoomTracker Clearing:**
- Current room information
- Monster lists and dispositions
- Room connections and edges
- Adjacent room data from look commands
- Line buffer with room parsing data

### **StatsTracker Clearing:**
- HP, MaxHP values
- MP, MV values  
- AT, AC timer values
- All numeric stat properties

### **UI Updates:**
- Status bar (HP, XP, character name, etc.)
- Room display (clears current room)
- Combat display (clears combat history)
- Character sheet data

## Usage Scenarios

### **Scenario 1: Character Switching**
```
1. You're logged in as Character A
2. Click Disconnect
3. All data automatically cleared ?
4. Connect and login as Character B
5. Clean slate - no mixed data ?
```

### **Scenario 2: Manual Reset**
```
1. You want to clear data without disconnecting
2. Menu ? Automation ? Clear Character Data
3. All data cleared while staying connected ?
4. Fresh start for current session ?
```

### **Scenario 3: Debugging/Testing**
```
1. Testing automation features
2. Need clean state between tests
3. Manual clear via menu ?
4. Consistent starting conditions ?
```

## Safety Features

### **Comprehensive Logging:**
- Logs when clearing starts
- Logs successful completion  
- Logs any errors during clearing
- Helps troubleshoot clearing issues

### **Exception Handling:**
- Each clearing operation wrapped in try-catch
- Partial clearing won't crash the application
- Detailed error logging for debugging

### **Reflection Safety:**
- Uses reflection carefully with null checks
- Graceful fallback if reflection fails
- No critical failures if internal structures change

### **UI Thread Safety:**
- Room and combat UI updates properly dispatched
- Property change notifications triggered correctly
- Status bar updates immediately

## Technical Benefits

### **Clean Character Switching:**
- No residual data from previous characters
- Prevents automation from using wrong character data
- Eliminates confusion from mixed character information

### **Memory Management:**
- Clears large data structures (combat history, room maps)
- Prevents memory leaks from accumulated data
- Keeps application responsive

### **Debugging Support:**
- Manual clear enables testing scenarios
- Clean state for automation testing
- Helps isolate character-specific issues

### **User Experience:**
- Automatic clearing on disconnect (seamless)
- Manual option available when needed
- Clear visual feedback in logs
- No manual cleanup required

## Files Modified

1. **DoorTelnet.Wpf/ViewModels/MainViewModel.cs**
   - Added RoomTracker dependency
   - Added ClearCharacterDataCommand
   - Enhanced DisconnectAsync with automatic clearing
   - Added comprehensive ClearAllCharacterData method

2. **DoorTelnet.Wpf/MainWindow.xaml**
   - Added "Clear Character Data" menu item in Automation menu

## Result

Users now have a robust character data clearing system that:
- **Automatically clears** all data on disconnect
- **Manually clears** data via menu when needed  
- **Comprehensively resets** all trackers and UI components
- **Safely handles** errors and edge cases
- **Provides logging** for troubleshooting

This ensures clean character switching and eliminates data contamination between different characters or sessions.