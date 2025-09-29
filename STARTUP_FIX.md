# Startup Performance Fix - Circular Dependency Resolution

## Issue
Application startup was hanging for 70+ seconds at the `RoomViewModel` dependency injection step.

## Root Cause
**Circular Dependency in Dependency Injection Container:**

1. **TelnetClient** ? required **RoomViewModel** (via `sp.GetRequiredService<RoomViewModel>()`)
2. **RoomViewModel** ? required **NavigationFeatureService**
3. **NavigationFeatureService** ? required **NavigationService** 
4. **NavigationService** ? required **MovementQueueService**
5. **MovementQueueService** ? required **TelnetClient** (circular!)

This created an infinite loop in the dependency resolution, causing the container to hang while trying to resolve the dependency chain.

## Solution
**Removed the circular dependency** by:

1. **Removed RoomViewModel dependency from TelnetClient registration**
   - Removed: `var roomVm = sp.GetRequiredService<RoomViewModel>();`
   - Removed: `roomTracker.RoomChanged += _ => Current?.Dispatcher.BeginInvoke(roomVm.Refresh);`

2. **Moved the subscription to MainWindow registration**
   - Added the room change subscription in `MainWindow` registration where both `RoomTracker` and `RoomViewModel` are safely available
   - This maintains the same functionality without the circular dependency

## Fixed Code Structure

### Before (Problematic):
```csharp
// TelnetClient registration
services.AddSingleton<TelnetClient>(sp =>
{
    // ... other dependencies ...
    var roomVm = sp.GetRequiredService<RoomViewModel>(); // ? CIRCULAR DEPENDENCY
    roomTracker.RoomChanged += _ => Current?.Dispatcher.BeginInvoke(roomVm.Refresh);
    // ... rest of setup ...
});
```

### After (Fixed):
```csharp
// TelnetClient registration (no circular dependency)
services.AddSingleton<TelnetClient>(sp =>
{
    // ... other dependencies ...
    // REMOVED: roomVm dependency
    // ... rest of setup ...
});

// MainWindow registration (safe place for subscription)
services.AddSingleton<MainWindow>(sp =>
{
    // ... other setup ...
    
    // Set up the RoomTracker -> RoomViewModel connection now that both services exist
    var roomTracker = sp.GetRequiredService<RoomTracker>();
    var roomVm = sp.GetRequiredService<RoomViewModel>();
    roomTracker.RoomChanged += _ => Current?.Dispatcher.BeginInvoke(roomVm.Refresh);
    
    // ... rest of setup ...
});
```

## Impact
- **Startup time**: Reduced from 70+ seconds to normal (< 5 seconds)
- **Functionality**: No functionality lost - room updates still work correctly
- **Architecture**: Cleaner separation of concerns - TelnetClient no longer has UI dependencies

## Dependency Flow (Fixed)
```
TelnetClient (no UI dependencies)
    ?
RoomTracker, CombatTracker, etc.
    ?
NavigationService chain (no circular reference)
    ?
RoomViewModel (with NavigationFeatureService)
    ? 
MainWindow (connects RoomTracker to RoomViewModel)
```

## Key Lessons
1. **Avoid UI dependencies in core services** - TelnetClient shouldn't depend on ViewModels
2. **Use event wiring at the composition root** - Wire up cross-cutting concerns in the final registration step
3. **Watch for circular dependencies** - Long startup times often indicate dependency resolution issues
4. **Keep dependency graphs acyclic** - Services should form a directed acyclic graph (DAG)

## Testing
- ? Build successful
- ? No circular dependencies
- ? Room updates still work (via event subscription)
- ? Navigation functionality preserved
- ? Startup performance restored