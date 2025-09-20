# DoorTelnet WPF Migration Plan

## Overview

This document outlines the staged migration from Windows Forms to WPF for DoorTelnet, maintaining the existing DoorTelnet.Core architecture while creating a modern, integrated single-screen UI experience.

## Migration Strategy

The migration follows a **parallel development approach** where the existing DoorTelnet.Cli project remains functional while we build the new WPF interface. This ensures no disruption to current functionality and allows for gradual transition.

### Key Principles

1. **Zero Core Changes**: DoorTelnet.Core remains untouched
2. **Event-Driven Architecture**: Leverage existing event system for UI updates
3. **MVVM Pattern**: Implement proper separation of concerns
4. **Incremental Development**: Build and test each component separately
5. **Performance First**: Optimize for real-time terminal data updates

## Project Setup Commands

### 1. Create Solution File (if needed)
```bash
cd C:\temp\DoorTelnet
dotnet new sln --name DoorTelnet
dotnet sln add DoorTelnet.Core\DoorTelnet.Core.csproj
dotnet sln add DoorTelnet.Cli\DoorTelnet.Cli.csproj
```

### 2. Create WPF Project
```bash
dotnet new wpf --name DoorTelnet.Wpf --framework net8.0
dotnet sln add DoorTelnet.Wpf\DoorTelnet.Wpf.csproj
dotnet add DoorTelnet.Wpf reference DoorTelnet.Core\DoorTelnet.Core.csproj
```

### 3. Add Required NuGet Packages
```bash
cd DoorTelnet.Wpf
dotnet add package Microsoft.Extensions.Hosting
dotnet add package Microsoft.Extensions.Logging
dotnet add package Microsoft.Extensions.Configuration
dotnet add package Microsoft.Extensions.Configuration.Json
dotnet add package Microsoft.Extensions.DependencyInjection
dotnet add package CommunityToolkit.Mvvm
```

## Project Structure

```
DoorTelnet.Wpf/
? App.xaml                           # Application entry point
? App.xaml.cs                        # Application code-behind
? MainWindow.xaml                    # Main integrated window
? MainWindow.xaml.cs                 # Main window code-behind
? ViewModels/                        # MVVM ViewModels
?   ? MainViewModel.cs               # Root view model (implemented)
?   ? ViewModelBase.cs               # Base class (implemented)
? Controls/                          # Custom WPF controls
?   ? TerminalControl.cs             # Custom terminal rendering control (implemented)
? Styles/                            # WPF styling and themes
?   ? DefaultTheme.xaml              # Dark theme (implemented)
? appsettings.json                   # Configuration (copied & loaded)
```

(Additional ViewModels / Views will be added in later stages.)

## Migration Stages

### Stage 1: Foundation Setup ✅ **COMPLETED**
**Goal**: Create basic WPF project with dependency injection and main window

**Deliverables**:
- [X] Create WPF project and add to solution
- [X] Set up dependency injection container (Generic Host in App.xaml.cs)
- [X] Create main window layout structure (responsive two-column + log variants iterated)
- [X] Implement basic MVVM infrastructure (ViewModelBase, MainViewModel, commands)
- [X] Migrate configuration system (appsettings.json + Host configuration)

**Key Files**:
- `App.xaml.cs` - Application startup and DI container
- `MainWindow.xaml` - Base integrated layout with themed styling
- `MainViewModel.cs` - Root view model with connection logic
- `appsettings.json` - Configuration file

**Success Criteria**:
- [X] WPF application starts without errors
- [X] Dependency injection resolves core services (ScreenBuffer, TelnetClient, etc.)
- [X] Main window displays with placeholder content
- [X] Configuration loads correctly (host/port, terminal defaults)

### Stage 2: Terminal Display ✅ **COMPLETED (Phase 2 Advanced)**
**Goal**: Implement high-performance terminal rendering

**Deliverables**:
- [X] Create TerminalControl custom control
- [X] Implement ANSI color / attribute rendering (core parsing already in Telnet client; control renders attributes)
- [X] Connect to ScreenBuffer from Core via DI
- [X] Handle real-time terminal updates (event-driven LinesChanged / Resized)
- [X] Implement cursor rendering and input handling (multiple styles from config)
- [X] Add scrollback (PageUp / PageDown), selection & copy, resize + NAWS notifications

**Key Files**:
- `Controls/TerminalControl.cs`
- `DoorTelnet.Core/Terminal/ScreenBuffer.cs` (enhanced dirty tracking, events, scrollback)
- `App.xaml.cs` (service registration)

**Technical Requirements**:
- [~] Support for all ANSI escape sequences (Core Telnet/ANSI processing handles sequences; control currently renders color/bold/inverse. Extended sequences still rely on existing parser—future refinement possible.)
- [X] Smooth event-driven rendering (throttled coalescing ~60 FPS upper bound)
- [X] Proper monospace font rendering (Consolas / metric-based sizing)
- [X] Cursor blinking and positioning (style: underscore, block, pipe, hash, dot, plus)
- [X] Input event handling and forwarding (arrows, Enter, Tab, Backspace, Esc, Ctrl+C copy when selecting)

**Success Criteria**:
- [X] Terminal displays game content correctly (buffer events reflected)
- [X] ANSI colors / attributes (fg/bg, bold, inverse) apply
- [X] No performance issues observed after throttling & coalescing
- [X] Cursor behaves correctly with blink & styles
- [X] User input forwards to ScriptEngine / TelnetClient (disabled during scrollback)

### Stage 3: Statistics Panel ?? **NEXT UP**
**Goal**: Display player statistics and status effects

**Duration**: 2-3 days

**Dependencies**: Stage 1

**Deliverables**:
- [X] Create StatsView and StatsViewModel
- [X] Bind to StatsTracker events
- [X] Display player HP/MP/MV with visual indicators
- [X] Show status effects and character information
- [X] Implement auto-refresh functionality

**Key Files**:
- `Views/StatsView.xaml` - Statistics display layout
- `ViewModels/StatsViewModel.cs` - Statistics data binding
- `Converters/HpPercentageToColorConverter.cs` - Health bar colors

**Success Criteria**:
- Real-time statistics updates
- Color-coded health/mana bars
- Status effects display correctly
- Character information shows properly

### Stage 4: Room Information Panel
**Goal**: Display current room details and navigation

**Duration**: 2 days

**Dependencies**: Stage 1

**Deliverables**:
- [ ] Create RoomView and RoomViewModel
- [ ] Connect to RoomTracker from Core
- [ ] Display room grid with visual indicators
- [ ] Show exits, monsters, and items
- [ ] Implement room change animations

**Key Files**:
- `Views/RoomView.xaml` - Room display layout
- `ViewModels/RoomViewModel.cs` - Room data binding

**Success Criteria**:
- Room information updates correctly
- Grid display shows available directions
- Monsters and items listed properly
- Exit visualization works

### Stage 5: Combat Statistics Panel
**Goal**: Display combat tracking and monster statistics

**Duration**: 2 days

**Dependencies**: Stage 1

**Deliverables**:
- [ ] Create CombatView and CombatViewModel
- [ ] Connect to CombatTracker events
- [ ] Display active combats and statistics
- [ ] Implement combat history and monster tables
- [ ] Add export functionality

**Key Files**:
- `Views/CombatView.xaml` - Combat display layout
- `ViewModels/CombatViewModel.cs` - Combat data binding

**Success Criteria**:
- Combat statistics update in real-time
- Monster tables display correctly
- Export functionality works
- Performance with large combat histories

### Stage 6: Session Log Panel
**Goal**: Implement high-performance logging display

**Duration**: 2 days

**Dependencies**: Stage 1

**Deliverables**:
- [ ] Create LogView and LogViewModel
- [ ] Implement virtualized log display
- [ ] Connect to UiLogProvider events
- [ ] Add filtering and search capabilities
- [ ] Implement log export features
- [ ] add auto-export to file based on setting.

**Key Files**:
- `Views/LogView.xaml` - Log display layout
- `ViewModels/LogViewModel.cs` - Log data binding
- `Controls/VirtualizedLogList.cs` - Performance-optimized log control

**Success Criteria**:
- Handles 10,000+ log entries smoothly
- Real-time log updates without lag
- Search and filtering work correctly
- Export functionality complete

### Stage 7: Integration and Polish
**Goal**: Integrate all components and add final features

**Duration**: 3-4 days

**Dependencies**: Stages 2-6

**Deliverables**:
- [ ] Integrate all panels into main window
- [ ] Implement keyboard shortcuts
- [ ] Add credential management dialogs
- [ ] Create settings management system
- [ ] Add automation controls
- [ ] Character profile management

**Key Files**:
- `MainWindow.xaml` - Complete integrated layout
- `Views/SettingsView.xaml` - Settings dialog
- `Services/DialogService.cs` - Dialog management
- `Styles/DefaultTheme.xaml` - Application theming

**Success Criteria**:
- All functionality from WinForms version works
- Smooth user experience
- Keyboard shortcuts functional
- Settings persist correctly

### Stage 8: Testing and Optimization
**Goal**: Ensure stability and performance

**Duration**: 2-3 days

**Dependencies**: Stage 7

**Deliverables**:
- [ ] Performance testing and optimization
- [ ] Memory leak detection and fixes
- [ ] UI responsiveness testing
- [ ] Feature parity verification
- [ ] User acceptance testing

**Success Criteria**:
- No memory leaks during extended use
- Smooth performance with high data rates
- All WinForms features working
- Stable under stress testing

## Technical Implementation Details

### MVVM Architecture

#### ViewModels Base Class
```csharp
public abstract class ViewModelBase : ObservableObject, IDisposable
{
    protected readonly ILogger _logger;
    private bool _isDisposed;

    protected ViewModelBase(ILogger logger)
    {
        _logger = logger;
    }

    public virtual void Dispose()
    {
        if (!_isDisposed)
        {
            OnDisposing();
            _isDisposed = true;
        }
    }

    protected virtual void OnDisposing() { }
}
```

#### Main ViewModel Structure
```csharp
public class MainViewModel : ViewModelBase
{
    public TerminalViewModel Terminal { get; }
    public StatsViewModel Stats { get; }
    public RoomViewModel Room { get; }
    public CombatViewModel Combat { get; }
    public LogViewModel Log { get; }
    
    // Commands for menu actions
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand SettingsCommand { get; }
    
    // Connection state
    public bool IsConnected { get; private set; }
    public string ConnectionStatus { get; private set; }
}
```

### Custom Terminal Control

#### Key Requirements
- **High Performance**: Must handle 30+ updates per second
- **ANSI Support**: Full escape sequence parsing
- **Font Rendering**: Monospace font with proper character spacing
- **Input Handling**: Capture all keyboard input and forward to game
- **Cursor Rendering**: Blinking cursor with multiple styles

#### Implementation Approach
```csharp
public class TerminalControl : Control
{
    private ScreenBuffer _screenBuffer;
    private WriteableBitmap _displayBitmap;
    private DispatcherTimer _refreshTimer;
    private DispatcherTimer _cursorTimer;
    
    // Optimized rendering using WriteableBitmap for performance
    protected override void OnRender(DrawingContext drawingContext)
    {
        // Render only changed cells for performance
        RenderDirtyRegions(drawingContext);
    }
}
```

### Data Binding Strategy

#### Real-time Updates
- Use `ObservableCollection<T>` for dynamic lists
- Implement `INotifyPropertyChanged` for all ViewModels
- Use `Dispatcher.BeginInvoke()` for cross-thread updates
- Throttle high-frequency updates to prevent UI lag

#### Memory Management
- Implement proper disposal patterns
- Use weak event patterns where appropriate
- Limit collection sizes (e.g., max 1000 log entries in UI)
- Implement virtualization for large data sets

### Performance Considerations

#### Terminal Rendering
- **Dirty Region Tracking**: Only redraw changed areas
- **Buffer Double-Buffering**: Use WriteableBitmap for smooth updates
- **Font Caching**: Pre-render character bitmaps
- **Update Throttling**: Limit to 30 FPS maximum

#### Memory Usage
- **Object Pooling**: Reuse objects for frequent allocations
- **Collection Limits**: Cap UI collections to prevent memory growth
- **Event Cleanup**: Properly unsubscribe from events
- **Weak References**: Use for long-lived event subscriptions

## Integration Points with Existing Code

### Core Services Integration
The WPF application will reuse all existing core services:

```csharp
// In App.xaml.cs - ConfigureServices method
services.AddSingleton<ScreenBuffer>();
services.AddSingleton<StatsTracker>();
services.AddSingleton<PlayerProfile>();
services.AddSingleton<RoomTracker>();
services.AddSingleton<CombatTracker>();
services.AddSingleton<TelnetClient>();
services.AddSingleton<ScriptEngine>();
services.AddSingleton<RuleEngine>();
// ... other core services
```

### Event System Mapping
Map existing events to WPF data binding:

| Core Event | WPF Handler | Update Target |
|------------|-------------|---------------|
| `StatsTracker.Updated` | `StatsViewModel.OnStatsUpdated` | Stats panel |
| `UiLogProvider.Message` | `LogViewModel.OnLogMessage` | Log panel |
| `CombatTracker.CombatStarted` | `CombatViewModel.OnCombatStarted` | Combat panel |
| `RoomTracker.RoomChanged` | `RoomViewModel.OnRoomChanged` | Room panel |

### Configuration Migration
- Copy `appsettings.json` from CLI project
- Maintain same configuration structure
- Add WPF-specific settings (themes, window position, etc.)

## Testing Strategy

### Unit Testing
- Test ViewModels independently of UI
- Mock core service dependencies
- Verify proper event handling and data binding
- Test command implementations

### Integration Testing
- Test service integration and event flow
- Verify real-time update performance
- Test memory usage under load
- Validate configuration loading

### User Acceptance Testing
- Feature parity with Windows Forms version
- Performance comparison under real-world usage
- Usability testing for new integrated interface
- Keyboard shortcut functionality

## Risk Mitigation

### Technical Risks
1. **Performance Issues**: Implement early performance testing and optimization
2. **Memory Leaks**: Regular memory profiling during development
3. **Thread Safety**: Careful handling of cross-thread UI updates
4. **Font Rendering**: Test with various system fonts and DPI settings

### Project Risks
1. **Scope Creep**: Stick to feature parity with current version
2. **Timeline Delays**: Prioritize critical path (terminal rendering)
3. **Integration Issues**: Regular testing with core services
4. **User Resistance**: Maintain parallel development approach

## Success Metrics

### Performance Targets
- Startup time: < 3 seconds
- Terminal rendering: 30+ FPS
- Memory usage: < 100MB steady state
- CPU usage: < 5% during normal operation

### Feature Completeness
- ? 100% feature parity with Windows Forms version
- ? All existing keyboard shortcuts working
- ? All automation features functional
- ? Configuration and credential management
- ? Export and logging capabilities

### User Experience
- ? Intuitive single-screen interface
- ? Responsive UI under all conditions
- ? Consistent visual design
- ? Proper accessibility support

## Current Status: Stage 2 Complete ?

### Stage 1 Completed Features:
- ? WPF project structure created
- ? Dependency injection configured with all core services
- ? Main window with integrated layout
- ? MVVM infrastructure with ViewModelBase
- ? Configuration system migrated from CLI
- ? Placeholder panels for all components

### Stage 2 Completed Features:
- ? TerminalControl with DependencyProperty support
- ? ANSI escape sequence rendering with full color support
- ? High-performance rendering (30+ FPS capability)
- ? Real-time screen buffer integration
- ? Cursor rendering with blinking animation
- ? Complete keyboard input handling
- ? AnsiTextBlock helper control
- ? Professional terminal styling
- ? Sample content and welcome screen
- ? TelnetClient integration for real connections

## Known Issues Fixed:
- ? XAML binding error resolved (converted to DependencyProperty)
- ? Terminal display rendering fixed (added DisplayUpdated events)
- ? Connection functionality implemented

## Next Steps:
Ready to begin **Stage 3: Statistics Panel** implementation with real-time player stats display and color-coded health/mana bars.

---

## Getting Started

To begin the migration:

1. **Execute the project setup commands** listed above
2. **Start with Stage 1** - Foundation Setup
3. **Follow the staged approach** - don't skip stages
4. **Test each stage thoroughly** before moving to the next
5. **Maintain the existing CLI project** until WPF version is complete

This migration plan ensures a smooth transition while maintaining all existing functionality and providing a modern, integrated user experience.