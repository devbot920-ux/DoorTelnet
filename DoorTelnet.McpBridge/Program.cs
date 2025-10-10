using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.McpBridge;

/// <summary>
/// Standalone MCP Bridge that connects to DoorTelnet.Wpf and exposes
/// an MCP server interface for LLM testing agents.
/// </summary>
class Program
{
    static async Task Main(string[] args)
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        var logger = loggerFactory.CreateLogger<Program>();
        
        var config = new BridgeConfig
        {
            WpfApiPort = 5000,      // Port where WPF app exposes its API
            McpServerPort = 3000,   // Port where LLM connects
            LlmApiUrl = args.Length > 0 ? args[0] : "http://localhost:1234", // LM Studio default
            LlmModel = args.Length > 1 ? args[1] : "gpt-oss-20b"
        };

        logger.LogInformation("Starting MCP Bridge...");
        logger.LogInformation("WPF API: http://localhost:{Port}", config.WpfApiPort);
        logger.LogInformation("MCP Server: http://localhost:{Port}", config.McpServerPort);
        logger.LogInformation("LLM: {Url} (model: {Model})", config.LlmApiUrl, config.LlmModel);

        var bridge = new McpBridgeServer(config, loggerFactory.CreateLogger<McpBridgeServer>());
        await bridge.StartAsync();
    }
}

public class BridgeConfig
{
    public int WpfApiPort { get; set; }
    public int McpServerPort { get; set; }
    public string LlmApiUrl { get; set; } = string.Empty;
    public string LlmModel { get; set; } = string.Empty;
}
