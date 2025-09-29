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
?   ??? GraphNode.cs          # Room node data model
?   ??? GraphEdge.cs          # Exit/connection data model
?   ??? NavigationPath.cs     # Calculated path with metadata
?   ??? NavigationRequest.cs  # Path request parameters
??? Services/
?   ??? GraphDataService.cs   # JSON loading and graph management
?   ??? PathfindingService.cs # A* pathfinding implementation
?   ??? RoomMatchingService.cs # Correlate parsed rooms with graph data
?   ??? NavigationService.cs  # High-level navigation coordination
??? Algorithms/
    ??? AStar.cs             # Pathfinding algorithm implementation
```

### WPF Integration

```
DoorTelnet.Wpf.Services/
??? NavigationFeatureService.cs  # AutomationFeatureService integration
??? NavigationViewModel.cs       # UI controls for navigation
```

## Implementation Phases

### Phase 1: Foundation (Core Data & Services)
1. **GraphDataService**: Load and parse `graph.json` into in-memory structures
2. **Room Models**: Create strongly-typed models matching JSON schema
3. **Basic Pathfinding**: Implement A* algorithm with coordinate-based heuristics
4. **Room Matching**: Bridge between `RoomTracker.CurrentRoom` and graph nodes

### Phase 2: Navigation Service
1. **PathfindingService**: Calculate optimal routes between rooms
2. **NavigationService**: Coordinate movement commands with safety checks
3. **Movement Queue**: Timed command execution with interruption support
4. **Safety Integration**: Pause navigation during combat or low health

### Phase 3: Automation Integration  
1. **AutomationFeatureService Extension**: Add navigation features to existing automation
2. **Combat Integration**: Pause/resume navigation based on combat state
3. **Health Monitoring**: Respect existing HP thresholds for safety
4. **User Controls**: UI elements for destination selection and navigation control

### Phase 4: Advanced Features
1. **Hunt Route Optimization**: Use spawn data for efficient monster hunting
2. **Safe Path Calculation**: Avoid dangerous areas based on level/attributes
3. **Quest Route Planning**: Navigate to quest NPCs and locations
4. **Store/Trainer Routing**: Quick access to facilities

## Technical Requirements

### Data Management
- Load graph.json at application startup
- Cache parsed data in memory for performance
- Implement fast lookup structures (Dictionary<string, GraphNode>)
- Handle JSON parsing errors gracefully

### Pathfinding Algorithm
- Use A* with Euclidean distance heuristic from x,y coordinates
- Consider edge weights: doors (+1), hidden exits (+2), dangerous areas (+5)
- Support pathfinding constraints (avoid non-peaceful rooms, respect level limits)
- Implement path validation before execution

### Room Correlation
- Match `RoomTracker.CurrentRoom.Name` with `GraphNode.label`
- Use fuzzy string matching for variations in room names
- Maintain position confidence scoring
- Handle cases where current room is not in graph data

### Movement Execution
- Queue directional commands with appropriate delays
- Monitor for movement failures or obstacles
- Detect when player gets lost or off-route
- Implement re-routing and recovery logic

## Safety & Integration Guidelines

### Health Safety Integration
```csharp
// Leverage existing health monitoring from AutomationFeatureService
if (_stats.Hp / (double)_stats.MaxHp * 100 < th.NavigationMinHpPercent)
{
    PauseNavigation("Low health - pausing navigation");
    return;
}
```

### Combat Awareness
```csharp
// Hook into existing CombatTracker events
_combat.CombatStarted += _ => PauseNavigation("Combat detected");
_combat.CombatCompleted += _ => ResumeNavigation("Combat ended");
```

### Room Safety Validation
```csharp
// Check destination safety before pathfinding
var targetNode = _graphData.GetNode(destinationId);
if (!targetNode.Peaceful && _profile.Player.Level < 10)
{
    return PathResult.Unsafe("Destination not safe for current level");
}
```

## File Size Management

Following established project patterns, split navigation into focused components:

- **GraphDataService**: ? 200 lines (JSON loading only)
- **PathfindingService**: ? 300 lines (A* algorithm)  
- **NavigationService**: ? 400 lines (coordination logic)
- **NavigationFeatureService**: ? 300 lines (automation integration)
- **Model Classes**: ? 100 lines each

## Error Handling Patterns

```csharp
public NavigationResult CalculatePath(string from, string to)
{
    try
    {
        // Pathfinding logic
        return NavigationResult.Success(path);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Pathfinding failed from {From} to {To}", from, to);
        return NavigationResult.Failed("Pathfinding error");
    }
}
```

## Event Integration

### RoomTracker Integration
```csharp
// Subscribe to room changes for position tracking
_roomTracker.RoomChanged += OnRoomChanged;

private void OnRoomChanged(RoomState newRoom)
{
    var graphNode = _roomMatching.FindMatchingNode(newRoom);
    if (graphNode != null)
    {
        UpdateNavigationPosition(graphNode.Id);
    }
}
```

### AutomationFeatureService Integration  
```csharp
// Add navigation controls to existing automation evaluation
private void EvaluateNavigation()
{
    var feats = _profile.Features;
    if (feats.AutoNavigation && _activeRoute != null)
    {
        ExecuteNextNavigationStep();
    }
}
```

## UI Integration Guidelines

### Settings Integration
Add navigation settings to existing `FeatureFlags` and `Thresholds` classes:

```csharp
public class FeatureFlags
{
    // ...existing properties...
    public bool AutoNavigation { get; set; }
    public bool AvoidDangerousRooms { get; set; }
    public bool PauseNavigationInCombat { get; set; }
}

public class Thresholds  
{
    // ...existing properties...
    public int NavigationMinHpPercent { get; set; } = 30;
    public int MaxRoomDangerLevel { get; set; } = 5;
}
```

### ViewModel Integration
Extend existing ViewModels rather than creating entirely new ones:

```csharp
public partial class RoomViewModel : ViewModelBase
{
    // ...existing room display...
    
    [ObservableProperty] private string? _navigationDestination;
    [ObservableProperty] private bool _isNavigating;
    
    [RelayCommand]
    private async Task NavigateToAsync(string destination)
    {
        // Navigation logic
    }
}
```

## Testing & Validation

### Graph Data Validation
- Verify all edges have valid source/target room IDs
- Ensure bidirectional consistency where expected
- Validate coordinate data for pathfinding accuracy
- Check spawn data integrity

### Pathfinding Validation
- Test paths between known room pairs
- Verify optimal path calculation
- Test edge cases (unreachable rooms, invalid destinations)
- Performance testing with large graphs

### Integration Testing
- Test with existing automation features enabled
- Verify proper pause/resume during combat
- Test health-based navigation safety
- Validate room matching accuracy

## Performance Considerations

### Memory Usage
- Load graph data once at startup
- Use efficient data structures (HashSet, Dictionary)
- Implement lazy loading for detailed room data
- Cache frequently accessed paths

### Pathfinding Optimization
- Implement path caching for common routes
- Use bidirectional A* for long distances
- Pre-calculate paths between major landmarks
- Limit search depth to prevent infinite loops

### UI Responsiveness
- Execute pathfinding on background threads
- Use async patterns for long calculations
- Provide progress feedback for complex routes
- Allow cancellation of long-running operations

## Future Enhancement Opportunities

1. **Machine Learning Integration**: Learn optimal paths based on player behavior
2. **Dynamic Obstacle Detection**: Adapt to temporary blockages or dangers
3. **Multi-Character Coordination**: Group navigation for multiple players
4. **Route Sharing**: Export/import popular routes between users
5. **Map Visualization**: 2D map display with navigation overlay
6. **Voice Commands**: Integration with speech recognition for hands-free navigation

## Summary

The room navigation system should be implemented as a natural extension of the existing automation framework, following established patterns for dependency injection, event handling, and service organization. Priority should be given to safety integration, ensuring navigation never conflicts with combat or health management systems already in place.

Start with basic pathfinding between known rooms, then progressively add advanced features like hunt optimization and quest routing. Maintain the project's established file size limits through focused, single-responsibility services that can be easily tested and maintained.