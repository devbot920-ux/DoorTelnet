using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.McpBridge;

/// <summary>
/// MCP Bridge server that proxies between LLM and WPF application
/// Maintains game state observation and command history
/// </summary>
public class McpBridgeServer
{
    private readonly BridgeConfig _config;
    private readonly ILogger<McpBridgeServer> _logger;
    private readonly HttpClient _wpfClient;
    private readonly HttpListener _mcpListener;
    
    // Game state cache (updated periodically from WPF)
    private GameStateSnapshot _currentState = new();
    private readonly ConcurrentQueue<string> _recentLines = new();
    private readonly ConcurrentQueue<CommandRecord> _commandHistory = new();
    private readonly int _maxLineHistory = 100;
    private readonly int _maxCommandHistory = 50;
    
    private Timer? _statePollingTimer;

    public McpBridgeServer(BridgeConfig config, ILogger<McpBridgeServer> logger)
    {
        _config = config;
        _logger = logger;
        _wpfClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{config.WpfApiPort}") };
        _mcpListener = new HttpListener();
        _mcpListener.Prefixes.Add($"http://localhost:{config.McpServerPort}/");
    }

    public async Task StartAsync()
    {
        // Start MCP server
        _mcpListener.Start();
        _logger.LogInformation("MCP Server listening on port {Port}", _config.McpServerPort);
        
        // Start polling WPF app for state updates
        _statePollingTimer = new Timer(async _ => await PollWpfStateAsync(), null, 
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        
        // Handle MCP requests
        while (true)
        {
            try
            {
                var context = await _mcpListener.GetContextAsync();
                _ = HandleMcpRequestAsync(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling MCP request");
            }
        }
    }

    private async Task PollWpfStateAsync()
    {
        try
        {
            // Poll multiple endpoints to build complete state
            var stateTask = _wpfClient.GetStringAsync("/api/state");
            var linesTask = _wpfClient.GetStringAsync("/api/recent-lines?count=10");
            
            await Task.WhenAll(stateTask, linesTask);
            
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            
            var state = JsonSerializer.Deserialize<GameStateSnapshot>(await stateTask, options);
            var lines = JsonSerializer.Deserialize<List<string>>(await linesTask, options);
            
            if (state != null)
            {
                _currentState = state;
            }
            
            if (lines != null)
            {
                foreach (var line in lines)
                {
                    _recentLines.Enqueue(line);
                    while (_recentLines.Count > _maxLineHistory)
                    {
                        _recentLines.TryDequeue(out _);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WPF polling failed (app may not be running)");
        }
    }

    private async Task HandleMcpRequestAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            
            if (request.HttpMethod != "POST")
            {
                await SendJsonResponse(response, new { error = "Only POST supported" }, 405);
                return;
            }

            using var reader = new StreamReader(request.InputStream);
            var body = await reader.ReadToEndAsync();
            
            // Log the incoming request for debugging
            _logger.LogDebug("Received MCP request: {Body}", body);
            
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true // Allow case-insensitive property matching
            };
            
            var mcpRequest = JsonSerializer.Deserialize<McpRequest>(body, options);

            if (mcpRequest?.Method == null)
            {
                _logger.LogWarning("Invalid MCP request - missing method. Body: {Body}", body);
                await SendJsonResponse(response, new { error = "Invalid request - method required" }, 400);
                return;
            }

            var result = await ExecuteToolAsync(mcpRequest.Method, mcpRequest.Params);
            await SendJsonResponse(response, new { result }, 200);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing MCP request");
            await SendJsonResponse(context.Response, new { error = ex.Message }, 500);
        }
    }

    private async Task<object> ExecuteToolAsync(string method, Dictionary<string, JsonElement>? parameters)
    {
        _logger.LogInformation("Executing tool: {Method}", method);

        return method switch
        {
            // === OBSERVATION TOOLS ===
            "observe_game_state" => ObserveGameState(),
            "get_recent_output" => GetRecentOutput(parameters),
            "get_command_history" => GetCommandHistory(),
            "check_feature_status" => await CheckFeatureStatusAsync(parameters),
            
            // === ACTION TOOLS ===
            "send_command" => await SendCommandAsync(parameters),
            "send_command_sequence" => await SendCommandSequenceAsync(parameters),
            "wait_for_output" => await WaitForOutputAsync(parameters),
            
            // === AUTOMATION TOOLS ===
            "set_automation" => await SetAutomationAsync(parameters),
            "navigate_to" => await NavigateToAsync(parameters),
            
            // === TESTING TOOLS ===
            "verify_stat_change" => await VerifyStatChangeAsync(parameters),
            "verify_room_change" => await VerifyRoomChangeAsync(parameters),
            "verify_combat_initiated" => await VerifyCombatInitiatedAsync(parameters),
            
            _ => new { error = $"Unknown method: {method}" }
        };
    }

    // === OBSERVATION TOOLS ===
    
    private object ObserveGameState()
    {
        return new
        {
            character = new
            {
                name = _currentState.CharacterName,
                level = _currentState.Level,
                hp = _currentState.Hp,
                maxHp = _currentState.MaxHp,
                hpPercent = _currentState.MaxHp > 0 
                    ? (int)(_currentState.Hp * 100.0 / _currentState.MaxHp) 
                    : 0,
                mana = _currentState.Mana,
                maxMana = _currentState.MaxMana,
                experience = _currentState.Experience,
                xpLeft = _currentState.XpLeft
            },
            location = new
            {
                roomName = _currentState.RoomName,
                roomId = _currentState.RoomId,
                exits = _currentState.Exits,
                monsters = _currentState.Monsters,
                items = _currentState.Items
            },
            combat = new
            {
                inCombat = _currentState.InCombat,
                targetedMonster = _currentState.TargetedMonster
            },
            automation = new
            {
                autoGong = _currentState.AutoGongEnabled,
                autoAttack = _currentState.AutoAttackEnabled,
                autoShield = _currentState.AutoShieldEnabled
            },
            timestamp = _currentState.Timestamp
        };
    }

    private object GetRecentOutput(Dictionary<string, JsonElement>? parameters)
    {
        var count = 20;
        if (parameters?.TryGetValue("count", out var countParam) == true)
        {
            count = countParam.GetInt32();
        }

        var lines = _recentLines.TakeLast(count).ToList();
        return new { lines, count = lines.Count };
    }

    private object GetCommandHistory()
    {
        var history = _commandHistory.ToList();
        return new
        {
            commands = history.Select(c => new
            {
                command = c.Command,
                timestamp = c.Timestamp,
                success = c.Success
            }),
            count = history.Count
        };
    }

    private async Task<object> CheckFeatureStatusAsync(Dictionary<string, JsonElement>? parameters)
    {
        var feature = "";
        if (parameters != null && parameters.TryGetValue("feature", out var featureElement))
        {
            feature = featureElement.GetString() ?? "";
        }
        
        try
        {
            var response = await _wpfClient.GetStringAsync($"/api/features/{feature}");
            var status = JsonSerializer.Deserialize<Dictionary<string, object>>(response);
            return new { feature, status, available = true };
        }
        catch
        {
            return new { feature, available = false, error = "Feature not found" };
        }
    }

    // === ACTION TOOLS ===
    
    private async Task<object> SendCommandAsync(Dictionary<string, JsonElement>? parameters)
    {
        var command = "";
        if (parameters != null && parameters.TryGetValue("command", out var commandElement))
        {
            command = commandElement.GetString() ?? "";
        }
        
        if (string.IsNullOrEmpty(command))
        {
            return new { error = "Command parameter required" };
        }

        try
        {
            var payload = JsonSerializer.Serialize(new { command });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _wpfClient.PostAsync("/api/send-command", content);
            
            var record = new CommandRecord
            {
                Command = command,
                Timestamp = DateTime.UtcNow,
                Success = response.IsSuccessStatusCode
            };
            
            _commandHistory.Enqueue(record);
            while (_commandHistory.Count > _maxCommandHistory)
            {
                _commandHistory.TryDequeue(out _);
            }

            return new
            {
                success = response.IsSuccessStatusCode,
                command,
                timestamp = record.Timestamp
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send command: {Command}", command);
            return new { error = ex.Message, command };
        }
    }

    private async Task<object> SendCommandSequenceAsync(Dictionary<string, JsonElement>? parameters)
    {
        var delayMs = 500;
        if (parameters != null && parameters.TryGetValue("delay_ms", out var delayElement))
        {
            delayMs = delayElement.GetInt32();
        }

        if (parameters == null || !parameters.TryGetValue("commands", out var commandsElement))
        {
            return new { error = "Commands parameter required" };
        }

        var commands = JsonSerializer.Deserialize<List<string>>(commandsElement.GetRawText());
        if (commands == null || commands.Count == 0)
        {
            return new { error = "Commands array cannot be empty" };
        }

        var results = new List<object>();
        foreach (var cmd in commands)
        {
            var result = await SendCommandAsync(new Dictionary<string, JsonElement>
            {
                ["command"] = JsonSerializer.SerializeToElement(cmd)
            });
            results.Add(result);
            await Task.Delay(delayMs);
        }

        return new { commands = results, count = results.Count };
    }

    private async Task<object> WaitForOutputAsync(Dictionary<string, JsonElement>? parameters)
    {
        var pattern = "";
        if (parameters != null && parameters.TryGetValue("pattern", out var patternElement))
        {
            pattern = patternElement.GetString() ?? "";
        }
        
        var timeoutMs = 5000;
        if (parameters != null && parameters.TryGetValue("timeout_ms", out var timeoutElement))
        {
            timeoutMs = timeoutElement.GetInt32();
        }

        if (string.IsNullOrEmpty(pattern))
        {
            return new { error = "Pattern parameter required" };
        }

        var startTime = DateTime.UtcNow;
        while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
        {
            var recentLines = _recentLines.TakeLast(10).ToList();
            if (recentLines.Any(line => line.Contains(pattern, StringComparison.OrdinalIgnoreCase)))
            {
                return new
                {
                    found = true,
                    pattern,
                    elapsed_ms = (DateTime.UtcNow - startTime).TotalMilliseconds,
                    matching_line = recentLines.First(line => 
                        line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                };
            }
            await Task.Delay(100);
        }

        return new { found = false, pattern, timeout = true };
    }

    // === AUTOMATION TOOLS ===
    
    private async Task<object> SetAutomationAsync(Dictionary<string, JsonElement>? parameters)
    {
        var feature = "";
        if (parameters != null && parameters.TryGetValue("feature", out var featureElement))
        {
            feature = featureElement.GetString() ?? "";
        }
        
        var enabled = true;
        if (parameters != null && parameters.TryGetValue("enabled", out var enabledElement))
        {
            enabled = enabledElement.GetBoolean();
        }

        if (string.IsNullOrEmpty(feature))
        {
            return new { error = "Feature parameter required" };
        }

        try
        {
            var payload = JsonSerializer.Serialize(new { feature, enabled });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _wpfClient.PostAsync("/api/automation/set", content);

            return new { success = response.IsSuccessStatusCode, feature, enabled };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, feature };
        }
    }

    private async Task<object> NavigateToAsync(Dictionary<string, JsonElement>? parameters)
    {
        var destination = "";
        if (parameters != null && parameters.TryGetValue("destination", out var destElement))
        {
            destination = destElement.GetString() ?? "";
        }

        if (string.IsNullOrEmpty(destination))
        {
            return new { error = "Destination parameter required" };
        }

        try
        {
            var payload = JsonSerializer.Serialize(new { destination });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _wpfClient.PostAsync("/api/navigation/start", content);

            return new { success = response.IsSuccessStatusCode, destination };
        }
        catch (Exception ex)
        {
            return new { error = ex.Message, destination };
        }
    }

    // === TESTING/VERIFICATION TOOLS ===
    
    private async Task<object> VerifyStatChangeAsync(Dictionary<string, JsonElement>? parameters)
    {
        var stat = "";
        if (parameters != null && parameters.TryGetValue("stat", out var statElement))
        {
            stat = statElement.GetString() ?? "";
        }
        
        var expectedChange = "";
        if (parameters != null && parameters.TryGetValue("expected_change", out var changeElement))
        {
            expectedChange = changeElement.GetString() ?? "";
        }

        if (string.IsNullOrEmpty(stat))
        {
            return new { error = "Stat parameter required" };
        }

        // Capture before value
        var beforeState = _currentState;
        var beforeValue = GetStatValue(beforeState, stat);

        // Wait for stat update
        await Task.Delay(1000);

        // Capture after value
        var afterState = _currentState;
        var afterValue = GetStatValue(afterState, stat);

        var actualChange = afterValue > beforeValue ? "increase" 
            : afterValue < beforeValue ? "decrease" 
            : "no_change";

        var passed = expectedChange == actualChange;

        return new
        {
            passed,
            stat,
            expected_change = expectedChange,
            actual_change = actualChange,
            before_value = beforeValue,
            after_value = afterValue
        };
    }

    private async Task<object> VerifyRoomChangeAsync(Dictionary<string, JsonElement>? parameters)
    {
        var beforeRoom = _currentState.RoomName;
        
        // Wait for room to potentially change
        await Task.Delay(1500);
        
        var afterRoom = _currentState.RoomName;
        var changed = beforeRoom != afterRoom;

        return new
        {
            changed,
            before_room = beforeRoom,
            after_room = afterRoom
        };
    }

    private async Task<object> VerifyCombatInitiatedAsync(Dictionary<string, JsonElement>? parameters)
    {
        var timeoutMs = 3000;
        if (parameters?.TryGetValue("timeout_ms", out var timeoutParam) == true)
        {
            timeoutMs = timeoutParam.GetInt32();
        }

        var startTime = DateTime.UtcNow;
        while ((DateTime.UtcNow - startTime).TotalMilliseconds < timeoutMs)
        {
            if (_currentState.InCombat)
            {
                return new
                {
                    initiated = true,
                    targeted_monster = _currentState.TargetedMonster,
                    elapsed_ms = (DateTime.UtcNow - startTime).TotalMilliseconds
                };
            }
            await Task.Delay(100);
        }

        return new { initiated = false, timeout = true };
    }

    private int GetStatValue(GameStateSnapshot state, string stat)
    {
        return stat.ToLowerInvariant() switch
        {
            "hp" => state.Hp,
            "maxhp" => state.MaxHp,
            "mana" => state.Mana,
            "maxmana" => state.MaxMana,
            "experience" => (int)state.Experience,
            _ => 0
        };
    }

    private async Task SendJsonResponse(HttpListenerResponse response, object data, int statusCode)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        
        var buffer = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer);
        response.Close();
    }

    private class McpRequest
    {
        public string? Method { get; set; }
        public Dictionary<string, JsonElement>? Params { get; set; }
    }

    private class CommandRecord
    {
        public string Command { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public bool Success { get; set; }
    }
}

public class GameStateSnapshot
{
    public string CharacterName { get; set; } = string.Empty;
    public int Level { get; set; }
    public int Hp { get; set; }
    public int MaxHp { get; set; }
    public int Mana { get; set; }
    public int MaxMana { get; set; }
    public long Experience { get; set; }
    public long XpLeft { get; set; }
    public string RoomName { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public List<string> Exits { get; set; } = new();
    public List<string> Monsters { get; set; } = new();
    public List<string> Items { get; set; } = new();
    public bool InCombat { get; set; }
    public string? TargetedMonster { get; set; }
    public bool AutoGongEnabled { get; set; }
    public bool AutoAttackEnabled { get; set; }
    public bool AutoShieldEnabled { get; set; }
    public DateTime Timestamp { get; set; }
}
