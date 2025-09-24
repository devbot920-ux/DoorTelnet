using System;
using System.Collections.Generic;

namespace DoorTelnet.Core.World;

/// <summary>
/// Basic data models for room tracking
/// </summary>
public record MonsterInfo(string Name, string Disposition, bool TargetingYou, int? Count);

public class RoomState
{
    public string Name { get; set; } = string.Empty;
    public List<string> Exits { get; set; } = new();
    public List<MonsterInfo> Monsters { get; set; } = new();
    public List<string> Items { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

public class BufferedLine
{
    public string Content { get; set; } = string.Empty;
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    public bool ProcessedForRoomDetection { get; set; } = false;
    public bool ProcessedForDynamicEvents { get; set; } = false;
    public bool ProcessedForStats { get; set; } = false;
    public bool ProcessedForIncarnations { get; set; } = false;
}

public class RoomGridData
{
    public GridRoomInfo? West { get; set; }
    public GridRoomInfo? Center { get; set; }
    public GridRoomInfo? East { get; set; }
    public GridRoomInfo? Northwest { get; set; }
    public GridRoomInfo? North { get; set; }
    public GridRoomInfo? Northeast { get; set; }
    public GridRoomInfo? Southwest { get; set; }
    public GridRoomInfo? South { get; set; }
    public GridRoomInfo? Southeast { get; set; }
    public GridRoomInfo? Up { get; set; }
    public GridRoomInfo? Down { get; set; }
}

public class GridRoomInfo
{
    public string Name { get; set; } = string.Empty;
    public List<string> Exits { get; set; } = new();
    public int MonsterCount { get; set; }
    public bool HasMonsters { get; set; }
    public bool HasAggressiveMonsters { get; set; }
    public int ItemCount { get; set; }
    public bool HasItems { get; set; }
    public bool IsKnown { get; set; }
    public bool IsCurrentRoom { get; set; }
}