using System.Net;
using System.Text;
using System.Text.Json;
using System.IO;
using DoorTelnet.Core.Automation;
using DoorTelnet.Core.Combat;
using DoorTelnet.Core.Player;
using DoorTelnet.Core.Telnet;
using DoorTelnet.Core.World;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Wpf.Services;

/// <summary>
/// HTTP API service that exposes game state and commands for external tools (MCP Bridge)
/// </summary>
public class GameApiService : IDisposable
{
    private readonly TelnetClient _client;
    private readonly StatsTracker _stats;
    private readonly RoomTracker _room;
    private readonly CombatTracker _combat;
    private readonly PlayerProfile _profile;
    private readonly NavigationFeatureService _navigation;
    private readonly ILogger<GameApiService> _logger;
    
    private HttpListener? _listener;
    private readonly int _port;
    private readonly Queue<string> _lineHistory = new();
    private readonly int _maxLineHistory = 200;

    public GameApiService(
        TelnetClient client,
        StatsTracker stats,
        RoomTracker room,
        CombatTracker combat,
        PlayerProfile profile,
        NavigationFeatureService navigation,
        ILogger<GameApiService> logger,
        int port = 5000)
    {
        _client = client;
        _stats = stats;
        _room = room;
        _combat = combat;
        _profile = profile;
        _navigation = navigation;
        _logger = logger;
        _port = port;

        // Subscribe to line received to build history
        _client.LineReceived += OnLineReceived;
    }

    private void OnLineReceived(string line)
    {
        lock (_lineHistory)
        {
            _lineHistory.Enqueue(line);
            while (_lineHistory.Count > _maxLineHistory)
            {
                _lineHistory.Dequeue();
            }
        }
    }

    public async Task StartAsync()
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{_port}/");
        _listener.Start();
        
        _logger.LogInformation("Game API Service started on http://localhost:{Port}", _port);
        
        while (_listener.IsListening)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = HandleRequestAsync(context);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling API request");
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            var path = request.Url?.AbsolutePath ?? "/";

            _logger.LogDebug("API Request: {Method} {Path}", request.HttpMethod, path);

            object result = path switch
            {
                "/api/state" => GetGameState(),
                "/api/recent-lines" => GetRecentLines(request),
                "/api/send-command" when request.HttpMethod == "POST" => await SendCommandAsync(request),
                "/api/automation/set" when request.HttpMethod == "POST" => await SetAutomationAsync(request),
                "/api/navigation/start" when request.HttpMethod == "POST" => await StartNavigationAsync(request),
                _ when path.StartsWith("/api/features/") => GetFeatureStatus(path),
                _ => new { error = "Not found", path }
            };

            await SendJsonResponse(response, result, result is { } obj && 
                obj.GetType().GetProperty("error") != null ? 404 : 200);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing API request");
            await SendJsonResponse(context.Response, new { error = ex.Message }, 500);
        }
    }

    private object GetGameState()
    {
        var room = _room.CurrentRoom;
        var targeted = _combat.GetTargetedMonster();

        return new
        {
            characterName = _profile.Player.Name,
            level = _profile.Player.Level,
            hp = _stats.Hp,
            maxHp = _stats.MaxHp,
            mana = _stats.Mp,
            maxMana = 0, // StatsTracker doesn't track max mana
            experience = _profile.Player.Experience,
            xpLeft = _profile.Player.XpLeft,
            roomName = room?.Name ?? "",
            roomId = "", // RoomState doesn't have RoomId
            exits = room?.Exits ?? new List<string>(),
            monsters = room?.Monsters.Select(m => m.Name).ToList() ?? new List<string>(),
            items = room?.Items ?? new List<string>(),
            inCombat = targeted != null,
            targetedMonster = targeted?.MonsterName,
            autoGongEnabled = _profile.Features.AutoGong,
            autoAttackEnabled = _profile.Features.AutoAttack,
            autoShieldEnabled = _profile.Features.AutoShield,
            timestamp = DateTime.UtcNow
        };
    }

    private object GetRecentLines(HttpListenerRequest request)
    {
        var countStr = request.QueryString["count"];
        var count = int.TryParse(countStr, out var c) ? c : 20;

        lock (_lineHistory)
        {
            var lines = _lineHistory.TakeLast(count).ToList();
            return lines;
        }
    }

    private async Task<object> SendCommandAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync();
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(body);

        if (data?.TryGetValue("command", out var command) == true && !string.IsNullOrEmpty(command))
        {
            _client.SendCommand(command);
            _logger.LogInformation("Command sent via API: {Command}", command);
            return new { success = true, command };
        }

        return new { error = "Command parameter required" };
    }

    private async Task<object> SetAutomationAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync();
        var data = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body);

        if (data == null)
            return new { error = "Invalid request" };

        var feature = data.GetValueOrDefault("feature").GetString();
        var enabled = data.GetValueOrDefault("enabled").GetBoolean();

        var success = feature?.ToLowerInvariant() switch
        {
            "autogong" => SetFeature(() => _profile.Features.AutoGong = enabled),
            "autoattack" => SetFeature(() => _profile.Features.AutoAttack = enabled),
            "autoshield" => SetFeature(() => _profile.Features.AutoShield = enabled),
            "autoheal" => SetFeature(() => _profile.Features.AutoHeal = enabled),
            _ => false
        };

        return success 
            ? new { success = true, feature, enabled }
            : new { error = $"Unknown feature: {feature}" };
    }

    private bool SetFeature(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<object> StartNavigationAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync();
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(body);

        if (data?.TryGetValue("destination", out var destination) == true && !string.IsNullOrEmpty(destination))
        {
            var success = _navigation.StartNavigation(destination);
            return new { success, destination };
        }

        return new { error = "Destination parameter required" };
    }

    private object GetFeatureStatus(string path)
    {
        var feature = path.Replace("/api/features/", "");
        
        return feature.ToLowerInvariant() switch
        {
            "autogong" => new { enabled = _profile.Features.AutoGong, name = "AutoGong" },
            "autoattack" => new { enabled = _profile.Features.AutoAttack, name = "AutoAttack" },
            "autoshield" => new { enabled = _profile.Features.AutoShield, name = "AutoShield" },
            "navigation" => new { enabled = true, name = "Navigation" },
            _ => new { error = "Feature not found" }
        };
    }

    private async Task SendJsonResponse(HttpListenerResponse response, object data, int statusCode)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.AddHeader("Access-Control-Allow-Origin", "*"); // Allow CORS
        
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

    public void Dispose()
    {
        _listener?.Stop();
        _listener?.Close();
        _client.LineReceived -= OnLineReceived;
    }
}
