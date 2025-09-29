namespace DoorTelnet.Core.Navigation.Models;

/// <summary>
/// Represents a calculated navigation path between two rooms
/// </summary>
public class NavigationPath
{
    public string FromRoomId { get; set; } = string.Empty;
    public string ToRoomId { get; set; } = string.Empty;
    public List<NavigationStep> Steps { get; set; } = new();
    public bool IsValid { get; set; }
    public string? ErrorReason { get; set; }
    public double TotalDistance { get; set; }
    public double EstimatedTime { get; set; }
    public double TotalCost { get; set; }
    public DateTime CalculatedAt { get; set; } = DateTime.UtcNow;

    public int StepCount => Steps.Count;
    public bool HasDoors => Steps.Any(s => s.RequiresDoor);
    public bool HasHiddenExits => Steps.Any(s => s.IsHidden);

    /// <summary>
    /// Creates a successful navigation path
    /// </summary>
    public static NavigationPath Success(string fromRoomId, string toRoomId, List<NavigationStep> steps, double totalDistance, double totalCost)
    {
        return new NavigationPath
        {
            FromRoomId = fromRoomId,
            ToRoomId = toRoomId,
            Steps = steps,
            IsValid = true,
            TotalDistance = totalDistance,
            TotalCost = totalCost,
            EstimatedTime = steps.Sum(s => s.EstimatedDelaySeconds),
            CalculatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates a failed navigation path with error reason
    /// </summary>
    public static NavigationPath Failed(string fromRoomId, string toRoomId, string errorReason)
    {
        return new NavigationPath
        {
            FromRoomId = fromRoomId,
            ToRoomId = toRoomId,
            IsValid = false,
            ErrorReason = errorReason,
            Steps = new List<NavigationStep>(),
            CalculatedAt = DateTime.UtcNow
        };
    }
}

/// <summary>
/// Represents a single step in a navigation path
/// </summary>
public class NavigationStep
{
    public string FromRoomId { get; set; } = string.Empty;
    public string ToRoomId { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;
    public double EstimatedDelaySeconds { get; set; } = 1.5;
    public bool RequiresDoor { get; set; }
    public bool IsHidden { get; set; }
    public int StepIndex { get; set; }
    public int MovementCost { get; set; } = 1;

    /// <summary>
    /// Creates a NavigationStep from a graph edge
    /// </summary>
    public static NavigationStep FromEdge(GraphEdge edge, GraphNode fromRoom, GraphNode toRoom)
    {
        return new NavigationStep
        {
            FromRoomId = fromRoom.Id,
            ToRoomId = toRoom.Id,
            Direction = edge.Direction,
            RequiresDoor = edge.RequiresDoor,
            IsHidden = edge.IsHidden,
            MovementCost = edge.GetMovementCost(),
            EstimatedDelaySeconds = CalculateDelayForEdge(edge, fromRoom, toRoom)
        };
    }

    /// <summary>
    /// Calculates estimated delay for traversing an edge
    /// </summary>
    private static double CalculateDelayForEdge(GraphEdge edge, GraphNode fromRoom, GraphNode toRoom)
    {
        double baseDelay = .5; // Base movement delay

        // Add delay for doors
        if (edge.RequiresDoor)
            baseDelay += 0.5;

        // Add delay for hidden exits (might need searching)
        if (edge.IsHidden)
            baseDelay += 1.0;

        // Add delay for dangerous rooms (combat might occur)
        if (!toRoom.IsPeaceful && toRoom.SpawnTotal > 0)
            baseDelay += Math.Min(toRoom.SpawnTotal * 0.2, 2.0);

        return baseDelay;
    }
}