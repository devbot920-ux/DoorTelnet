using System.Text.Json.Serialization;

namespace DoorTelnet.Core.Navigation.Models;

/// <summary>
/// Represents a directional connection between two rooms in the navigation graph
/// </summary>
public class GraphEdge
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public string Target { get; set; } = string.Empty;

    [JsonPropertyName("dir")]
    public string Direction { get; set; } = string.Empty;

    [JsonPropertyName("door")]
    public int Door { get; set; }

    [JsonPropertyName("hidden")]
    public int Hidden { get; set; }

    /// <summary>
    /// Helper property to check if this exit requires a door
    /// </summary>
    public bool RequiresDoor => Door == 1;

    /// <summary>
    /// Helper property to check if this exit is hidden
    /// </summary>
    public bool IsHidden => Hidden == 1;

    /// <summary>
    /// Calculates the movement cost for this edge based on its properties
    /// </summary>
    public int GetMovementCost()
    {
        int cost = 1; // Base cost

        if (RequiresDoor)
            cost += 1; // Doors add complexity

        if (IsHidden)
            cost += 2; // Hidden exits are harder to find/use

        return cost;
    }

    /// <summary>
    /// Gets the normalized direction command
    /// </summary>
    public string GetNormalizedDirection()
    {
        return Direction.ToLowerInvariant() switch
        {
            "n" => "north",
            "s" => "south", 
            "e" => "east",
            "w" => "west",
            "ne" => "northeast",
            "nw" => "northwest", 
            "se" => "southeast",
            "sw" => "southwest",
            "u" => "up",
            "d" => "down",
            _ => Direction.ToLowerInvariant()
        };
    }
}