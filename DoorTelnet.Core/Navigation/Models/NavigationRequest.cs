namespace DoorTelnet.Core.Navigation.Models;

/// <summary>
/// Represents a request for navigation pathfinding with optional constraints
/// </summary>
public class NavigationRequest
{
    public string FromRoomId { get; set; } = string.Empty;
    public string ToRoomId { get; set; } = string.Empty;
    public NavigationConstraints Constraints { get; set; } = new();
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Represents constraints and preferences for navigation pathfinding
/// </summary>
public class NavigationConstraints
{
    /// <summary>
    /// Avoid non-peaceful rooms if true
    /// </summary>
    public bool AvoidDangerousRooms { get; set; } = true;

    /// <summary>
    /// Avoid rooms with traps if true
    /// </summary>
    public bool AvoidTraps { get; set; } = true;

    /// <summary>
    /// Avoid hidden exits if true
    /// </summary>
    public bool AvoidHiddenExits { get; set; } = false;

    /// <summary>
    /// Avoid exits that require doors if true
    /// </summary>
    public bool AvoidDoors { get; set; } = false;

    /// <summary>
    /// Maximum allowed path length in steps
    /// </summary>
    public int MaxPathLength { get; set; } = 100;

    /// <summary>
    /// Minimum player level for safety considerations
    /// </summary>
    public int PlayerLevel { get; set; } = 1;

    /// <summary>
    /// Prefer paths through specific room types (taverns, stores, etc.)
    /// </summary>
    public List<string> PreferredRoomTypes { get; set; } = new();

    /// <summary>
    /// Room IDs to completely avoid
    /// </summary>
    public HashSet<string> ForbiddenRooms { get; set; } = new();

    /// <summary>
    /// Maximum danger level to accept (based on spawn count and trap presence)
    /// </summary>
    public int MaxDangerLevel { get; set; } = 5;

    /// <summary>
    /// Evaluates if a room should be avoided based on these constraints
    /// </summary>
    public bool ShouldAvoidRoom(GraphNode room)
    {
        if (ForbiddenRooms.Contains(room.Id))
            return true;

        if (AvoidDangerousRooms && !room.IsPeaceful && room.SpawnTotal > 0)
        {
            // Check if danger level exceeds maximum
            int dangerLevel = room.SpawnTotal + (room.HasTrap == 1 ? 2 : 0);
            if (dangerLevel > MaxDangerLevel)
                return true;
        }

        if (AvoidTraps && room.HasTrap == 1)
            return true;

        return false;
    }

    /// <summary>
    /// Evaluates if an edge should be avoided based on these constraints
    /// </summary>
    public bool ShouldAvoidEdge(GraphEdge edge)
    {
        if (AvoidHiddenExits && edge.IsHidden)
            return true;

        if (AvoidDoors && edge.RequiresDoor)
            return true;

        return false;
    }

    /// <summary>
    /// Gets additional cost penalty for using this room in pathfinding
    /// </summary>
    public int GetRoomCostPenalty(GraphNode room)
    {
        int penalty = 0;

        if (!room.IsPeaceful && room.SpawnTotal > 0)
            penalty += Math.Min(room.SpawnTotal * 2, 10); // Cap penalty

        if (room.HasTrap == 1)
            penalty += 5;

        // Reduce penalty for preferred room types
        if (PreferredRoomTypes.Contains("tavern") && room.IsTavern == 1)
            penalty = Math.Max(0, penalty - 3);

        if (PreferredRoomTypes.Contains("store") && room.IsStore == 1)
            penalty = Math.Max(0, penalty - 2);

        return penalty;
    }
}