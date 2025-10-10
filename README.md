# DoorTelnet - MUD Client with AI Testing

A modern .NET 8 WPF MUD client with autonomous AI testing capabilities.

## Projects

### DoorTelnet.Core
Core game logic including:
- Telnet protocol handling
- Room/Combat/Stats tracking
- Navigation and pathfinding
- Automation features

### DoorTelnet.Wpf
WPF user interface with:
- Terminal display
- Stats/Room/Combat panels
- Automation controls
- Game API for external tools (port 5000)

### DoorTelnet.McpBridge
MCP (Model Context Protocol) Bridge that:
- Proxies between LLM and WPF app
- Maintains game state cache
- Exposes testing tools to AI (port 3000)

### testing/
Python-based AI testing system:
- `llm_tester.py` - Main test runner
- `test_connectivity.py` - Connection checker
- Documentation and guides

## Quick Start

### Play the Game
```bash
cd DoorTelnet.Wpf
dotnet run
```

### AI Testing (3 Terminal Setup)

**Terminal 1: WPF App**
```bash
cd DoorTelnet.Wpf
dotnet run
# Connect to MUD and login
```

**Terminal 2: MCP Bridge**
```bash
cd DoorTelnet.McpBridge
dotnet run
```

**Terminal 3: Run Tests**
```bash
cd testing
pip install -r requirements.txt
python llm_tester.py autogong
```

See `testing/QUICK_START.md` for detailed setup instructions.

## Requirements

- .NET 8 SDK
- Python 3.8+ (for AI testing)
- LM Studio (for local LLM) - Optional but recommended

## Features

### Core Features
? Telnet MUD client with ANSI support  
? Room tracking and mapping  
? Combat tracking with XP calculation  
? Auto-attack, Auto-gong, Auto-heal  
? Navigation with pathfinding  
? Character profiles and saved credentials  

### AI Testing Features
? Autonomous feature testing via LLM  
? Bug detection and analysis  
? Automatic Copilot prompt generation  
? Continuous regression testing  
? Game state observation tools  

## Architecture

```
???????????????????????
?  DoorTelnet.Wpf     ? ? User plays here
?  (Game Client)      ?
?  Port: 5000 (API)   ?
???????????????????????
           ? HTTP
???????????????????????
?  DoorTelnet.McpBridge? ? Proxy layer
?  (MCP Server)        ?
?  Port: 3000          ?
???????????????????????
           ? MCP Protocol
???????????????????????
?  llm_tester.py      ? ? AI test agent
?  (Python)           ?
???????????????????????
           ? OpenAI API
???????????????????????
?  LM Studio          ? ? Local LLM
?  (gpt-oss-20b)      ?
?  Port: 1234         ?
???????????????????????
```

## Configuration

Edit `DoorTelnet.Wpf/appsettings.json`:

```json
{
  "connection": {
    "host": "thepenaltybox.org",
    "port": 23
  },
  "api": {
    "enabled": true,
    "port": 5000
  }
}
```

## Documentation

- `testing/README.md` - Full MCP Bridge documentation
- `testing/QUICK_START.md` - 5-minute setup guide
- `testing/IMPLEMENTATION.md` - Technical implementation details

## License

MIT License - See LICENSE file for details

## Contributing

Contributions welcome! Areas of interest:
- New automation features
- Additional test scenarios
- UI improvements
- Documentation

## Support

For issues or questions:
1. Check the documentation in `testing/`
2. Run `python test_connectivity.py` to diagnose connection issues
3. Review test results in `test_results_*.json`
4. Check bug analyses in `bug_analysis_*.md`

---

**Note**: This is a MUD game client. Make sure you have permission to use automation features on your target MUD server.