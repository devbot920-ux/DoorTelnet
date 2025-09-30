# Travel Gold Pickup Enhancement

## Overview
Enhanced the gold pickup functionality to work automatically during travel/navigation, not just during combat or when stationary.

## Key Features Added

### 1. **Room Entry Cooldown Management**
- **3-second cooldown** after entering a room before attempting gold pickup
- Prevents immediate pickup attempts that might interfere with navigation
- Tracks room entry time using `_lastRoomEntry` timestamp

### 2. **Smart Room-Based Pickup Logic**
- **Automatic detection** of gold coins in room items
- **Unique room fingerprinting** to avoid repeated pickup attempts in the same room
- **10-second cooldown** between pickup attempts in the same room to prevent spam

### 3. **Travel Context Awareness**
- **Integrates with NavigationFeatureService** to determine if player is currently traveling
- **Enhanced logging** shows whether pickup is during "Travel" or "Room" context
- **Works alongside existing AutoGong gold pickup** without conflicts

### 4. **Memory Management**
- **Automatic cleanup** of old room pickup attempts (older than 5 minutes)
- **Dictionary-based tracking** prevents memory leaks during long travel sessions

## How It Works

### Room Change Detection
```
Room Change ? OnRoomChanged() ? Set _lastRoomEntry = Now ? Start 3s cooldown
```

### Evaluation Loop (Every 500ms)
```
1. Check if PickupGold feature enabled
2. Check if 3+ seconds since room entry
3. Check if room contains "gold coin" items
4. Check if haven't attempted pickup in this room recently (10s cooldown)
5. Execute "get gold" command
6. Log context (Travel vs Room) and timing
7. Cleanup old room tracking data
```

### Room Fingerprinting
Each room is uniquely identified by:
```
"{RoomName}_{ExitCount}_{SortedExitList}"
```
This prevents repeated pickup attempts even if the player leaves and re-enters the same room quickly.

## Integration Points

### Constructor Changes
- **Added NavigationFeatureService dependency** (optional injection)
- **Subscribe to RoomChanged events** for room entry timing
- **Added room tracking dictionaries** for pickup attempt management

### Evaluation Loop Enhancement
- **Added TryPickupTravelGold()** call when PickupGold feature is enabled
- **Runs independently** of AutoGong and AutoAttack cycles
- **Respects cooldowns** to avoid interference with other automation

### Service Dependencies
- **AutomationFeatureService** now optionally depends on **NavigationFeatureService**
- **Updated App.xaml.cs** to inject NavigationFeatureService into AutomationFeatureService
- **Maintains backward compatibility** - works even if NavigationFeatureService is null

## Usage Scenarios

### During Navigation
- Player starts navigation from Town to Store
- As player travels through rooms, any rooms containing gold coins will trigger pickup
- 3-second delay ensures navigation commands aren't interrupted
- "Travel gold pickup attempted" messages in logs

### During Regular Play
- Player exploring manually (not using navigation)
- Same logic applies but context shows "Room gold pickup attempted"
- Works alongside existing gold pickup from coin drop detection

### During Combat (AutoGong)
- **Existing AutoGong gold pickup continues** to work as before
- **Travel pickup logic is independent** and doesn't interfere
- Both systems can operate simultaneously if needed

## Configuration
- **Enabled by default** when `PickupGold` feature flag is enabled
- **No additional settings required** - uses existing gold pickup preference
- **Automatic behavior** - no user intervention needed

## Performance Considerations
- **Lightweight evaluation** - only checks room items when PickupGold is enabled
- **Efficient room fingerprinting** using string concatenation
- **Automatic memory cleanup** prevents long-term memory growth
- **Minimal network traffic** - respects cooldowns to avoid spam

## Logging
Enhanced logging provides visibility into pickup behavior:
```
Travel gold pickup attempted in 'Forest Path' (cooldown: 3.2s)
Room gold pickup attempted in 'Town Square' (cooldown: 4.1s)
```

The travel gold pickup feature now ensures that players automatically collect gold during their journeys, making travel more efficient and profitable while maintaining the 3-second room entry cooldown for optimal game interaction.