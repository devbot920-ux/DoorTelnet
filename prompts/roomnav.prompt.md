# Room Navigation Implementation Guidelines

## Overview

This document provides specific guidance for implementing auto-navigation functionality in the DoorTelnet MUD client using a comprehensive room graph JSON data source. The navigation system should integrate seamlessly with existing automation features while maintaining the established architectural patterns.

## Data Source Structure

### JSON Graph Schema
The navigation system will load from `graph.json` containing:
- **3,446 nodes** (rooms) with rich metadata
- **7,077 edges** (exits/connections) with directional information  
- **22 regions** for logical area grouping
- **Spawn data** for 8 monster slots per room
- **Room attributes** (peaceful, tavern, store, trainer, quest, etc.)

### Key Data Elements

#### Room Nodes
- `id`: Unique room identifier
- `label`: Human-readable room name
- `x`, `y`: Coordinates for pathfinding heuristics
- `peaceful`: Safety indicator (0/1)
- `is_tavern`, `is_store`, `is_quest`, `is_spell_trainer`: Facility flags
- `spawn1_name` through `spawn8_name`: Monster spawn information
- `spawn_total`: Total monsters that can spawn
- `portal`, `tp`: Special movement indicators
- `has_trap`: Danger indicator

#### Edges (Exits)
- `source`, `target`: Room IDs connected by this exit
- `dir`: Movement command ("n", "s", "e", "w", "ne", "nw", "se", "sw", "u", "d", etc.)
- `door`: Door requirement (0/1) 
- `hidden`: Hidden exit indicator (0/1)

## Architecture Implementation

### Service Layer Structure

```
DoorTelnet.Core.Navigation/
??? Models/
?   ??? GraphNode.cs          # Room node data model ? COMPLETED
?   ??? GraphEdge.cs          # Exit/connection data model ? COMPLETED
?   ??? NavigationPath.cs     # Calculated path with metadata ? COMPLETED
?   ??? NavigationRequest.cs  # Path request parameters ? COMPLETED
?   ??? NavigationStep.cs     # Individual movement step ? COMPLETED
??? Services/
?   ??? GraphDataService.cs   # JSON loading and graph management ? COMPLETED
?   ??? PathfindingService.cs # A* pathfinding implementation ? COMPLETED
?   ??? RoomMatchingService.cs # Correlate parsed rooms with graph data ? COMPLETED
?   ??? MovementQueueService.cs # Event-driven movement execution ? COMPLETED
?   ??? NavigationService.cs  # High-level navigation coordination ? COMPLETED
??? Algorithms/
    ??? AStar.cs             # Pathfinding algorithm implementation ? COMPLETED
```

### WPF Integration

```
DoorTelnet.Wpf.Services/
??? NavigationFeatureService.cs  # AutomationFeatureService integration ? COMPLETED
??? NavigationViewModel.cs       # UI controls for navigation (? INTEGRATED INTO RoomViewModel)
```

## Implementation Status

### ? Phase 1: Foundation (Core Data & Services) - **COMPLETED**
1. **GraphDataService**: ? Load and parse `graph.json` into in-memory structures
2. **Room Models**: ? Create strongly-typed models matching JSON schema
3. **Basic Pathfinding**: ? Implement A* algorithm with coordinate-based heuristics
4. **Room Matching**: ? Bridge between `RoomTracker.CurrentRoom` and graph nodes

### ? Phase 2: Navigation Service - **COMPLETED**
1. **PathfindingService**: ? Calculate optimal routes between rooms
2. **NavigationService**: ? Coordinate movement commands with safety checks
3. **Movement Queue**: ? Event-driven command execution with multiple movement modes
4. **Safety Integration**: ? Pause navigation during combat or low health

### ? Phase 3: Automation Integration - **COMPLETED**
1. **AutomationFeatureService Extension**: ? Add navigation features to existing automation
2. **Combat Integration**: ? Pause/resume navigation based on combat state
3. **Health Monitoring**: ? Respect existing HP thresholds for safety
4. **User Controls**: ? UI elements for destination selection and navigation control

### ?? Phase 4: Advanced Features - **PARTIALLY COMPLETED**
1. **Store Route Optimization**: ? `FindNearbyStores()` with distance calculation
2. **Safe Path Calculation**: ? Avoid dangerous areas based on level/attributes
3. **Smart Search**: ? Room ID, name, and special queries like "store"
4. **Movement Modes**: ? Triggered, Fast, UltraFast, and TimedOnly modes
5. **Hunt Route Optimization**: ? **PENDING** - Use spawn data for efficient monster hunting
6. **Quest Route Planning**: ? **PENDING** - Navigate to quest NPCs and locations
7. **Trainer Routing**: ? **PENDING** - Quick access to spell trainers

## Technical Implementation Status

### ? Data Management - **COMPLETED**
- Load graph.json at application startup via `App.xaml.cs`
- Cache parsed data in memory for performance
- Implement fast lookup structures (Dictionary<string, GraphNode>)
- Handle JSON parsing errors gracefully

### ? Pathfinding Algorithm - **COMPLETED**
- Use A* with Euclidean distance heuristic from x,y coordinates
- Consider edge weights: doors (+1), hidden exits (+2), dangerous areas (+5)
- Support pathfinding constraints (avoid non-peaceful rooms, respect level limits)
- Implement path validation and caching

### ? Room Correlation - **COMPLETED**
- Match `RoomTracker.CurrentRoom.Name` with `GraphNode.label`
- Use fuzzy string matching for variations in room names
- Maintain position confidence scoring
- Handle cases where current room is not in graph data

### ? Movement Execution - **COMPLETED**
- Event-driven movement queue with multiple modes:
  - **Triggered**: Wait for room detection events (most reliable)
  - **FastWithFallback**: Fast movement with room detection fallback
  - **UltraFast**: Minimal delays, trust path completely
  - **TimedOnly**: Original timed behavior
- Monitor for movement failures or obstacles
- Detect when player gets lost or off-route
- Implement re-routing and recovery logic

## Safety & Integration Status

### ? Health Safety Integration - **COMPLETED**

```csharp
// Leverage existing health monitoring from AutomationFeatureService
if (_stats.Hp / (double)_stats.MaxHp * 100 < th.NavigationMinHpPercent)
{
    PauseNavigation("Low health - pausing navigation");
    return;
}
```

### ? Combat Awareness - **COMPLETED**

```csharp
// Hook into existing CombatTracker events
_combat.CombatStarted += _ => PauseNavigation("Combat detected");
_combat.CombatCompleted += _ => ResumeNavigation("Combat ended");
```

### ? Room Safety Validation - **COMPLETED**

```csharp
// Check destination safety before pathfinding
var targetNode = _graphData.GetNode(destinationId);
if (!targetNode.Peaceful && _profile.Player.Level < 10)
{
    return PathResult.Unsafe("Destination not safe for current level");
}
```

## Advanced Features Implementation Plan

### Hunt Route Optimization - **PENDING**
Need to implement monster hunting route planning:

```csharp
public class HuntRouteService
{
    /// <summary>
    /// Finds optimal hunting routes based on spawn data and player level
    /// </summary>
    public NavigationPath CalculateHuntRoute(string startRoomId, HuntCriteria criteria)
    {
        // Find rooms with appropriate spawns for player level
        // Calculate circuit that maximizes XP/hour while maintaining safety
        // Consider respawn times and room danger levels
    }
}
```

### Quest Route Planning - **PENDING**
Need to implement quest NPC navigation:

```csharp
public List<NavigationSuggestion> FindQuestNPCs(int maxDistance = 50)
{
    // Search for rooms marked as quest locations
    // Find NPCs based on room metadata
    // Calculate routes to quest givers and objectives
}
```

### Trainer Routing - **PENDING**
Need to implement spell trainer navigation:

```csharp
public List<NavigationSuggestion> FindNearbyTrainers(int maxDistance = 40, string? sphere = null)
{
    // Find rooms with is_spell_trainer = 1
    // Filter by spell sphere if specified
    // Calculate distances and return sorted results
}
```

## File Organization Status

Following established project patterns, navigation is split into focused components:

- **GraphDataService**: ~200 lines (JSON loading) ?
- **PathfindingService**: ~300 lines (A* algorithm + caching) ?  
- **NavigationService**: ~600 lines (coordination logic) ?
- **NavigationFeatureService**: ~300 lines (automation integration) ?
- **MovementQueueService**: ~400 lines (event-driven execution) ?
- **Model Classes**: ~100 lines each ?

## Current Features Available

### ? Basic Navigation
- Navigate by room ID: `nav 1234`
- Navigate by room name: `nav "Town Square"`
- Smart search with autocomplete
- Multiple movement modes for different scenarios

### ? Store Finding
- Search for nearby stores: `nav store`
- Distance-based filtering
- Automatic path calculation

### ? Safety Features
- Health-based navigation pausing
- Combat detection and pause/resume
- Dangerous room avoidance
- Configurable safety thresholds

### ? Movement Modes
- **Triggered Mode**: Maximum reliability, waits for room detection
- **Fast Mode**: Optimized for safe areas with fallback
- **Ultra-Fast Mode**: Maximum speed for trusted paths
- **Timed Mode**: Original delay-based behavior

## Remaining Work Items

### High Priority
1. **Hunt Route Optimization**: Implement spawn-based hunting circuits
2. **Quest Route Planning**: Add quest NPC and objective navigation
3. **Trainer Routing**: Implement spell trainer finding and navigation

### Medium Priority
1. **Route Sharing**: Export/import popular routes between users
2. **Map Visualization**: 2D map display with navigation overlay
3. **Performance Optimizations**: Pre-calculate common routes

### Low Priority
1. **Machine Learning**: Learn optimal paths based on player behavior
2. **Dynamic Obstacle Detection**: Adapt to temporary blockages
3. **Multi-Character Coordination**: Group navigation for multiple players
4. **Voice Commands**: Speech recognition integration

## Integration Points

### ? Settings Integration - **COMPLETED**
Navigation settings are integrated into existing `FeatureFlags` and `Thresholds`:

```csharp
public class FeatureFlags
{
    public bool AutoNavigation { get; set; }
    public bool AvoidDangerousRooms { get; set; }
    public bool PauseNavigationInCombat { get; set; }
}

public class Thresholds  
{
    public int NavigationMinHpPercent { get; set; } = 30;
    public int MaxRoomDangerLevel { get; set; } = 5;
}
```

### ? UI Integration - **COMPLETED**
Navigation controls are integrated into `RoomViewModel`:
- Destination search with autocomplete
- Movement mode selection
- Navigation status display
- Safety indicator

### ? Event Integration - **COMPLETED**
- Room tracking for position updates
- Combat events for safety pausing
- Health monitoring for safety checks
- Movement queue coordination

## Performance Considerations

### ? Memory Usage - **COMPLETED**
- Graph data loaded once at startup
- Efficient data structures (HashSet, Dictionary)
- Path caching for frequently accessed routes

### ? Pathfinding Optimization - **COMPLETED**
- A* algorithm with coordinate-based heuristics
- Path caching with expiration
- Constraint-based path filtering
- Search depth limiting

### ? UI Responsiveness - **COMPLETED**
- Async pathfinding operations
- Debounced search input
- Background thread execution
- Cancellation support

## Summary

The room navigation system has been successfully implemented as a natural extension of the existing automation framework. **Phases 1-3 are complete** with all core functionality operational:

- ? Complete pathfinding infrastructure with A* algorithm
- ? Event-driven movement execution with multiple modes
- ? Safety integration with combat and health monitoring
- ? UI integration with autocomplete and status display
- ? Store finding and smart search capabilities

**Phase 4 remains partially complete** with advanced features like hunt route optimization, quest routing, and trainer navigation still pending implementation. The foundation is solid and extensible, making these remaining features straightforward to add when needed.

The system follows established patterns for dependency injection, event handling, and service organization, maintaining the project's architectural consistency while providing powerful navigation capabilities.