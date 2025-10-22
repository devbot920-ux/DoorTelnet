using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DoorTelnet.Core.Terminal;

namespace DoorTelnet.Core.World;

/// <summary>
/// Streamlined RoomTracker that uses segmented components for better maintainability
/// Enhanced with color-based parsing for improved accuracy
/// </summary>
public class RoomTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RoomState> _rooms = new();
    private readonly Dictionary<string, Dictionary<string, string>> _edges = new();
    private string _currentRoomKey = string.Empty;
    private DateTime _lastRoomChange = DateTime.MinValue;
    private DateTime _lastMovementCommand = DateTime.MinValue; // Track when we last moved
    private string? _lastMovementDirection = null; // Track which direction we moved
    private string? _previousRoomId = null; // Track the room we came from
    private readonly Dictionary<string, Dictionary<string, RoomState>> _adjacentRoomData = new();
    private readonly LineBuffer _lineBuffer = new();
    private const string LookBoundary = "You peer 1 room away";
    private DateTime _lastLookParsed = DateTime.MinValue;

    // Movement command patterns
    private static readonly Regex MovementCommandPattern = new(@"^\[Hp=.*?\]\s*[nsewud][e|w]?(?:\s|$)|^\[Hp=.*?\]\s*(north|south|east|west|northeast|northwest|southeast|southwest|up|down)(?:\s|$)", RegexOptions.IgnoreCase);
    private static readonly Regex EnterKeyPattern = new(@"^\[Hp=.*?\]\s*$", RegexOptions.IgnoreCase); // Stats line followed by nothing (Enter key)
    private static readonly Regex StatsPattern = new(
        @"\[Hp=\d+/Mp=\d+/Mv=\d+(?:/At=\d+)?(?:/Ac=\d+)?\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Optional CombatTracker integration for unified death handling
    public object? CombatTracker { get; set; }
    
    // Optional ScreenBuffer reference for color-based parsing
    private ScreenBuffer? _screenBuffer;

    public RoomState? CurrentRoom { get; private set; }
    
    /// <summary>
    /// Gets the ID of the room we were previously in (before the current room)
    /// </summary>
    public string? PreviousRoomId => _previousRoomId;
    
    public event Action<RoomState>? RoomChanged;
    
    /// <summary>
    /// Gets the last movement direction that was detected (if any)
    /// </summary>
    public string? LastMovementDirection => _lastMovementDirection;

    /// <summary>
    /// Set the ScreenBuffer reference for color-based parsing (optional but recommended)
    /// </summary>
    public void SetScreenBuffer(ScreenBuffer screenBuffer)
    {
        _screenBuffer = screenBuffer;
    }

    // Restored helper methods used by CLI / other subsystems
    public IEnumerable<BufferedLine> GetUnprocessedLinesForStats() => 
        _lineBuffer.GetUnprocessedLines(l => l.ProcessedForStats);

    public void MarkLineProcessedForStats(BufferedLine line) => 
        _lineBuffer.MarkProcessed(new[] { line }, l => l.ProcessedForStats = true);

    public bool HasUnprocessedDynamicEvents()
    {
        foreach (var bl in _lineBuffer.GetUnprocessedLines(l => l.ProcessedForDynamicEvents))
        {
            var line = RoomTextProcessor.StripLeadingPartialStats(bl.Content).stripped;
            line = StatsPattern.Replace(line, "").Trim();
            if (Regex.IsMatch(line, @"^(?:A|An)\s+(.+?)\s+is\s+summoned\s+for\s+combat!?\s*$", RegexOptions.IgnoreCase)) 
                return true;
            if (Regex.IsMatch(line, @"^(?:A |An |The )?(.+?)\s+(enters|arrives)\s+", RegexOptions.IgnoreCase)) 
                return true;
            if (IsDeathLine(line) && FindMatchingMonsterNames(line).Any()) 
                return true;
        }
        return false;
    }

    public void AddLine(string line)
    {
        var cleanedLine = RoomTextProcessor.CleanLineContent(line);
        if (!string.IsNullOrEmpty(cleanedLine))
        {
            _lineBuffer.AddLine(cleanedLine);
            
            // Check for movement commands or Enter key
            if (MovementCommandPattern.IsMatch(cleanedLine))
            {
                _lastMovementCommand = DateTime.UtcNow;
                
                // Try to extract the direction
                var match = MovementCommandPattern.Match(cleanedLine);
                if (match.Success)
                {
                    var dirGroup = match.Groups[1];
                    if (dirGroup.Success && !string.IsNullOrWhiteSpace(dirGroup.Value))
                    {
                        _lastMovementDirection = NormalizeDirection(dirGroup.Value);
                    }
                    else
                    {
                        // Try to extract from the command itself (e.g., "n", "2s", "ne")
                        var cmdText = cleanedLine.Replace("[Hp=", "").Split(']').LastOrDefault()?.Trim();
                        if (!string.IsNullOrWhiteSpace(cmdText))
                        {
                            // Remove any leading numbers (e.g., "2s" -> "s")
                            var dirPart = new string([.. cmdText.Where(char.IsLetter)]);
                            _lastMovementDirection = NormalizeDirection(dirPart);
                        }
                    }
                }
                
                System.Diagnostics.Debug.WriteLine($"🚶 MOVEMENT COMMAND DETECTED: '{cleanedLine}' -> Direction: {_lastMovementDirection ?? "unknown"}");
            }
            else if (EnterKeyPattern.IsMatch(cleanedLine))
            {
                _lastMovementCommand = DateTime.UtcNow;
                _lastMovementDirection = null; // Enter key, not a directional move
                System.Diagnostics.Debug.WriteLine($"⏎ ENTER KEY DETECTED: '{cleanedLine}'");
            }
        }
    }

    public bool TryUpdateRoom(string user, string character, string screenText)
    {
        var handledLook = TryParseLookCommand(user, character, screenText);
        bool anyUpdate = handledLook;

        if (handledLook)
        {
            System.Diagnostics.Debug.WriteLine("🔍 Skipping current room update due to look command processing");
            return anyUpdate;
        }

        if ((DateTime.UtcNow - _lastRoomChange).TotalMilliseconds < 25)
        {
            var unprocessedCount = _lineBuffer.GetUnprocessedLines(l => l.ProcessedForRoomDetection).Count();
            if (unprocessedCount == 0) return anyUpdate;
        }

        lock (_sync)
        {
            if (TryHandleDynamicEvents(user, character)) anyUpdate = true;
            
            // Check if we should look for a room update
            bool recentMovement = (DateTime.UtcNow - _lastMovementCommand).TotalMilliseconds < 2000;
            
            // TRY COLOR-BASED PARSING FIRST if ScreenBuffer is available
            RoomState? roomState = null;
            if (_screenBuffer != null && recentMovement)
            {
                try
                {
                    roomState = ParseRoomWithColor();
                    if (roomState != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"🎨 COLOR-BASED PARSING SUCCESS: Room='{roomState.Name}', Monsters={roomState.Monsters.Count}, Exits={roomState.Exits.Count}");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"❌ COLOR-BASED PARSING ERROR: {ex.Message}");
                    // Fall through to text-based parsing
                }
            }
            
            // Fallback to text-based parsing if color parsing failed or unavailable
            if (roomState == null && recentMovement)
            {
                roomState = ParseRoomFromBuffer();
            }
            
            if (roomState == null)
            {
                // No room state parsed
                if (recentMovement)
                {
                    // We moved but didn't detect a new room name
                    // Check if exits changed or we're in darkness
                    if (CurrentRoom != null && !string.IsNullOrEmpty(_lastMovementDirection))
                    {
                        // Try to find where we moved to in the graph
                        var curKey = MakeKey(user, character, CurrentRoom.Name);
                        if (_edges.TryGetValue(curKey, out var edgeDict) && 
                            edgeDict.TryGetValue(_lastMovementDirection, out var targetKey))
                        {
                            // We know where we should have gone
                            if (_rooms.TryGetValue(targetKey, out var targetRoom))
                            {
                                System.Diagnostics.Debug.WriteLine($"📍 ASSUMED MOVEMENT: {_lastMovementDirection} from '{CurrentRoom.Name}' to '{targetRoom.Name}' (no room name detected, using graph)");
                                
                                // Update to the target room, preserving monster info
                                UpdateRoom(user, character, targetRoom);
                                _lastRoomChange = DateTime.UtcNow;
                                return true;
                            }
                        }
                        
                        System.Diagnostics.Debug.WriteLine($"⚠️ MOVEMENT WITHOUT NAME: Direction {_lastMovementDirection} from '{CurrentRoom.Name}' but no graph data");
                    }
                }
                
                if (QuickAugmentMonsters(user, character)) anyUpdate = true;
            }
            else
            {
                // We have a room state
                // Only update room name if we recently moved or it's a significant change
                bool shouldUpdateName = recentMovement || 
                                       CurrentRoom == null || 
                                       !CurrentRoom.Exits.SequenceEqual(roomState.Exits);
                
                if (!shouldUpdateName && CurrentRoom != null)
                {
                    // Don't update room name, but update monsters/items
                    var monstersChanged = !MonstersEqual(CurrentRoom?.Monsters, roomState.Monsters);
                    var itemsChanged = (CurrentRoom == null && roomState.Items.Count > 0)
                                       || (CurrentRoom != null && !CurrentRoom.Items.SequenceEqual(roomState.Items));

                    if (monstersChanged || itemsChanged)
                    {
                        // Preserve current room name AND RoomId
                        roomState = new RoomState
                        {
                            Name = CurrentRoom.Name, // Keep existing name
                            RoomId = CurrentRoom.RoomId, // Keep existing RoomId
                            Exits = roomState.Exits,
                            Monsters = roomState.Monsters,
                            Items = roomState.Items,
                            LastUpdated = DateTime.UtcNow
                        };
                        
                        UpdateRoom(user, character, roomState);
                        _lastRoomChange = DateTime.UtcNow;
                        anyUpdate = true;
                    }
                }
                else if (!roomState.Name.Contains("(looked)"))
                {
                    // CRITICAL: If we recently moved, ALWAYS update the room even if the name is the same
                    // This handles cases where multiple rooms have the same short description
                    // The movement direction and graph edges will help us determine which room we're actually in
                    var isNewRoom = CurrentRoom == null
                                    || CurrentRoom.Name != roomState.Name
                                    || !CurrentRoom.Exits.SequenceEqual(roomState.Exits)
                                    || _lastMovementDirection != null;
                    var monstersChanged = !MonstersEqual(CurrentRoom?.Monsters, roomState.Monsters);
                    var itemsChanged = (CurrentRoom == null && roomState.Items.Count > 0)
                                       || (CurrentRoom != null && !CurrentRoom.Items.SequenceEqual(roomState.Items));

                    if (isNewRoom || monstersChanged || itemsChanged)
                    {
                        // Don't preserve RoomId when moving to what appears to be a new room
                        // Let the navigation service's room matching determine the correct ID
                        UpdateRoom(user, character, roomState);
                        _lastRoomChange = DateTime.UtcNow;
                        anyUpdate = true;
                    }
                }
            }
        }

        return anyUpdate;
    }

    /// <summary>
    /// Parse room using color information from ScreenBuffer (PRIMARY parsing method)
    /// OPTIMIZED: After movement, only look at recent lines (last 10-15 lines after stats)
    /// </summary>
    private RoomState? ParseRoomWithColor()
    {
        if (_screenBuffer == null) return null;

        // Get snapshot of current screen buffer
        var snapshot = _screenBuffer.Snapshot();
        var rows = _screenBuffer.Rows;
        var cols = _screenBuffer.Columns;

        // Convert snapshot to colored lines
        var coloredLines = RoomParser.SnapshotToColoredLines(snapshot, rows, cols);

        if (coloredLines.Count == 0) return null;

        // Find the LAST stats line to determine where room content starts
        var statsIdx = new List<int>();
        for (int i = 0; i < coloredLines.Count; i++)
        {
            if (coloredLines[i].DominantForegroundColor.HasValue)
            {
                coloredLines[i].Text = StatsPattern.Replace(coloredLines[i].Text, "").Trim();
            }
            if (RoomParser.IsStatsLine(coloredLines[i].Text))
            {
                statsIdx.Add(i);
            }
        }

        List<ColoredLine> roomLines;
        bool recentMovement = (DateTime.UtcNow - _lastMovementCommand).TotalMilliseconds < 2000;
        
        if (statsIdx.Count > 0)
        {
            var lastStatsIdx = statsIdx.Last();
            if (!MovementCommandPattern.IsMatch(coloredLines[lastStatsIdx].Text) && statsIdx.Count > 1)
            {
                lastStatsIdx = statsIdx[statsIdx.Count - 2];
            }
            
            // OPTIMIZATION: After a movement command, only look at the most recent lines (10-15 lines after stats)
            // This prevents scanning the entire buffer and improves performance
            if (recentMovement)
            {
                // Take only the next 15 lines after the last stats line
                // This is enough for: room name (1 line) + monsters (1-3 lines) + items (0-2 lines) + exits (1 line)
                const int maxLinesToScan = 15;
                roomLines = [.. coloredLines.Skip(lastStatsIdx + 1).Take(maxLinesToScan)];
                
                System.Diagnostics.Debug.WriteLine($"🔎 OPTIMIZED SCAN: Looking at {roomLines.Count} lines after stats (recent movement)");
                if (roomLines.Count > 1)
                {
                    System.Diagnostics.Debug.WriteLine($"OPTIMIZED SCAN: found more than one lines after stats");
                }
            }
            else
            {
                // No recent movement, can scan more lines for updates to current room
                roomLines = [.. coloredLines.Skip(lastStatsIdx + 1)];
            }
        }
        else
        {
            // No stats line found, limit scan to last 20 lines
            const int maxLinesToScan = 20;
            roomLines = [.. coloredLines.TakeLast(maxLinesToScan)];
            
            if (recentMovement)
            {
                System.Diagnostics.Debug.WriteLine($"🔎 OPTIMIZED SCAN: No stats found, looking at last {roomLines.Count} lines");
            }
        }

        if (roomLines.Count == 0) return null;

        // Use color-based parsing on the limited set of lines
        var state = RoomParser.ParseWithColor(roomLines);

        if (state != null)
        {
            // CRITICAL: Preserve existing monster disposition information for current room refreshes
            if (CurrentRoom != null 
                && string.Equals(state.Name, CurrentRoom.Name, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(_lastMovementDirection))
            {
                var mergedMonsters = new List<MonsterInfo>();
                foreach (var newMonster in state.Monsters)
                {
                    var existingMonster = CurrentRoom.Monsters.FirstOrDefault(m => 
                        string.Equals(m.Name, newMonster.Name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(m.Name?.Replace(" (summoned)", ""), newMonster.Name, StringComparison.OrdinalIgnoreCase));
                    
                    if (existingMonster != null)
                    {
                        mergedMonsters.Add(new MonsterInfo(
                            newMonster.Name, 
                            existingMonster.Disposition,
                            existingMonster.TargetingYou,
                            newMonster.Count));
                        
                        System.Diagnostics.Debug.WriteLine($"🔄 COLOR-PARSE: Preserved {existingMonster.Disposition} disposition for '{newMonster.Name}'");
                    }
                    else
                    {
                        mergedMonsters.Add(newMonster);
                    }
                }
                
                // Preserve aggressive monsters that might have been missed
                foreach (var currentMonster in CurrentRoom.Monsters)
                {
                    if (!mergedMonsters.Any(m => string.Equals(m.Name, currentMonster.Name, StringComparison.OrdinalIgnoreCase)) &&
                        currentMonster.Disposition.Equals("aggressive", StringComparison.OrdinalIgnoreCase))
                    {
                        mergedMonsters.Add(currentMonster);
                        System.Diagnostics.Debug.WriteLine($"🔄 COLOR-PARSE: Preserved missing aggressive monster '{currentMonster.Name}'");
                    }
                }
                
                // Preserve RoomId when updating existing room
                state = new RoomState
                {
                    Name = state.Name,
                    RoomId = CurrentRoom.RoomId, // Keep existing RoomId
                    Exits = state.Exits,
                    Items = state.Items,
                    Monsters = mergedMonsters,
                    LastUpdated = DateTime.UtcNow
                };
            }
            else
            {
                state = new RoomState
                {
                    Name = state.Name,
                    RoomId = null, 
                    Exits = state.Exits,
                    Items = state.Items,
                    Monsters = state.Monsters,
                    LastUpdated = DateTime.UtcNow
                };
            }
        }

        return state;
    }

    private bool QuickAugmentMonsters(string user, string character)
    {
        if (CurrentRoom == null) return false;

        var newMonsterLines = _lineBuffer
            .GetUnprocessedLines(l => l.ProcessedForRoomDetection)
            .Where(l => !l.ProcessedForDynamicEvents)
            .Select(l => l.Content)
            .Where(c => c.IndexOf(" lay here", StringComparison.OrdinalIgnoreCase) < 0
                        && c.IndexOf(" lays here", StringComparison.OrdinalIgnoreCase) < 0
                        && Regex.IsMatch(c, @"^(?:.+?)\s+(?:is|are)\s+here\.?$", RegexOptions.IgnoreCase))
            .ToList();

        if (newMonsterLines.Count == 0) return false;

        var updated = CurrentRoom.Monsters.ToList();
        foreach (var line in newMonsterLines)
        {
            var listPart = Regex.Replace(line, @"\s+(?:is|are)\s+here\.?$", string.Empty, RegexOptions.IgnoreCase).Trim();
            if (listPart.Length == 0) continue;

            var names = ParseMonsterNamesFromList(listPart);
            foreach (var n in names)
            {
                var name = n.Trim();
                if (name.Length == 0) continue;
                updated.Add(new MonsterInfo(name, "neutral", false, null));
            }
        }

        _lineBuffer.MarkProcessed(
            _lineBuffer.GetUnprocessedLines(l => l.ProcessedForDynamicEvents)
                .Where(l => newMonsterLines.Contains(l.Content)),
            l => l.ProcessedForDynamicEvents = true
        );

        if (updated.Count == CurrentRoom.Monsters.Count) return false;

        var newState = new RoomState
        {
            Name = CurrentRoom.Name,
            RoomId = CurrentRoom.RoomId, // Preserve RoomId
            Exits = [.. CurrentRoom.Exits],
            Monsters = updated,
            Items = [.. CurrentRoom.Items],
            LastUpdated = DateTime.UtcNow
        };

        UpdateRoom(user, character, newState);
        return true;
    }

    private List<string> ParseMonsterNamesFromList(string listPart)
    {
        var names = new List<string>();
        if (listPart.Contains(','))
        {
            var commaParts = Regex.Split(listPart, @"\s*,\s*")
                .Where(p => p.Length > 0)
                .ToList();

            if (commaParts.Count > 0)
            {
                for (int i = 0; i < commaParts.Count - 1; i++)
                {
                    names.Add(commaParts[i].Trim());
                }

                var lastPart = commaParts[^1].Trim();
                if (Regex.IsMatch(lastPart, @"\s+and\s+", RegexOptions.IgnoreCase))
                {
                    var lastParts = Regex.Split(lastPart, @"\s+and\s+", RegexOptions.IgnoreCase)
                        .Where(p => p.Length > 0)
                        .Select(p => p.Trim());
                    names.AddRange(lastParts);
                }
                else
                {
                    names.Add(lastPart);
                }
            }
        }
        else if (Regex.IsMatch(listPart, @"\s+and\s+", RegexOptions.IgnoreCase))
        {
            names.AddRange(Regex.Split(listPart, @"\s+and\s+", RegexOptions.IgnoreCase)
                .Where(p => p.Length > 0)
                .Select(p => p.Trim()));
        }
        else
        {
            names.Add(listPart);
        }

        return names;
    }

    private static bool IsDeathLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        var trimmed = line.TrimEnd();
        int i = trimmed.Length - 1;
        while (i >= 0 && (trimmed[i] == '!' || trimmed[i] == '.' || trimmed[i] == ' ')) i--;
        int end = i;
        if (end < 0) return false;
        while (i >= 0 && char.IsLetter(trimmed[i])) i--;
        var lastWord = trimmed.Substring(i + 1, end - i).ToLowerInvariant();
        
        var deathWords = new[] { "banished", "cracks", "darkness", "dead", "death", "defeated", "dies", 
                               "disappears", "earth", "exhausted", "existance", "existence", "flames", 
                               "goddess", "gone", "ground", "himself", "killed", "lifeless", "mana", 
                               "manaless", "nothingness", "over", "pieces", "portal", "scattered", 
                               "silent", "slain", "still", "vortex" };
        
        return deathWords.Contains(lastWord);
    }

    private IEnumerable<string> FindMatchingMonsterNames(string line)
    {
        if (CurrentRoom == null) yield break;

        foreach (var m in CurrentRoom.Monsters)
        {
            var name = m.Name;
            var baseName = name;
            var idx = baseName.IndexOf(" (", StringComparison.Ordinal);
            if (idx > 0)
            {
                baseName = baseName.Substring(0, idx);
            }

            if (baseName.Length == 0) continue;

            if (line.IndexOf(baseName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                yield return baseName;
            }
        }
    }

    private bool TryHandleDynamicEvents(string user, string character)
    {
        if (CurrentRoom == null) return false;

        var unprocessed = _lineBuffer.GetUnprocessedLines(l => l.ProcessedForDynamicEvents).ToList();
        if (!unprocessed.Any()) return false;

        var toAdd = new List<MonsterInfo>();
        var toRemove = new List<string>();
        var deathNotifications = new List<(List<string> monsters, string line)>(); // Store death notifications to send outside lock

        foreach (var bl in unprocessed)
        {
            var original = bl.Content;
            var line = RoomTextProcessor.StripLeadingPartialStats(original).stripped;
            line = StatsPattern.Replace(line, "").Trim();

            var mSummon = Regex.Match(line, @"^(?:A|An)\s+(.+?)\s+is\s+summoned\s+for\s+combat!?\s*$", RegexOptions.IgnoreCase);
            if (mSummon.Success)
            {
                var name = mSummon.Groups[1].Value.Trim();
                bool exists = CurrentRoom.Monsters.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    toAdd.Add(new MonsterInfo(name, "aggressive", false, null));
                }
                continue;
            }

            var mEnter = Regex.Match(line, @"^(?:A |An |The )?(.+?)\s+(enters|arrives)\s+(?:from\s+the\s+)?(.+?)\.?$", RegexOptions.IgnoreCase);
            if (mEnter.Success)
            {
                var name = mEnter.Groups[1].Value.Trim();
                var dir = mEnter.Groups[3].Value.Trim();
                if (!CurrentRoom.Monsters.Any(m => m.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
                {
                    toAdd.Add(new MonsterInfo($"{name} (entered from {dir})", "neutral", false, null));
                }
                continue;
            }

            var mFollow = Regex.Match(line, @"^(?:A |An |The )?(.+?)\s+follows\s+you\.?$", RegexOptions.IgnoreCase);
            if (mFollow.Success)
            {
                var name = mFollow.Groups[1].Value.Trim();
                if (!CurrentRoom.Monsters.Any(m => m.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
                {
                    toAdd.Add(new MonsterInfo($"{name} (followed you)", "neutral", false, null));
                }
                continue;
            }

            if (IsDeathLine(line))
            {
                var matches = FindMatchingMonsterNames(line)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (matches.Count > 0)
                {
                    toRemove.AddRange(matches);
                    
                    // Store death notification to send outside lock to prevent deadlocks
                    deathNotifications.Add((matches, line));
                }
            }
        }

        _lineBuffer.MarkProcessed(unprocessed, l => l.ProcessedForDynamicEvents = true);

        if (toAdd.Count == 0 && toRemove.Count == 0) return false;

        var updated = CurrentRoom.Monsters.ToList();

        foreach (var dead in toRemove)
        {
            var matches = updated
                .Where(m => m.Name.Contains(dead, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var m in matches)
            {
                updated.Remove(m);
            }
        }

        foreach (var add in toAdd)
        {
            if (!updated.Any(m => m.Name.Equals(add.Name, StringComparison.OrdinalIgnoreCase)))
            {
                updated.Add(add);
            }
        }

        var newState = new RoomState
        {
            Name = CurrentRoom.Name,
            RoomId = CurrentRoom.RoomId, // Preserve RoomId
            Exits = [.. CurrentRoom.Exits],
            Items = [.. CurrentRoom.Items],
            Monsters = updated,
            LastUpdated = DateTime.UtcNow
        };

        UpdateRoom(user, character, newState);

        // Notify CombatTracker about newly summoned monsters so they can be tracked for auto-attack
        foreach (var add in toAdd)
        {
            if (add.Disposition.Equals("aggressive", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (CombatTracker != null)
                    {
                        var ensureMethod = CombatTracker.GetType().GetMethod("EnsureMonsterTracked");
                        ensureMethod?.Invoke(CombatTracker, new object[] { add.Name });
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"?? COMBAT TRACKING ERROR: {ex.Message}");
                }
            }
        }

        // Send death notifications OUTSIDE the lock to prevent deadlocks
        foreach (var (monsters, line) in deathNotifications)
        {
            try
            {
                if (CombatTracker != null)
                {
                    var notifyMethod = CombatTracker.GetType().GetMethod("NotifyMonsterDeath");
                    notifyMethod?.Invoke(CombatTracker, new object[] { monsters, line });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"?? COMBAT NOTIFICATION ERROR: {ex.Message}");
            }
        }

        return true;
    }

    private RoomState? ParseRoomFromBuffer()
    {
        var unprocessed = _lineBuffer.GetUnprocessedLines(l => l.ProcessedForRoomDetection).ToList();
        if (!unprocessed.Any()) return null;

        var lines = unprocessed.Select(l => l.Content).ToList();
        var statsIdx = new List<int>();

        for (int i = 0; i < lines.Count; i++)
        {
            if (RoomParser.IsStatsLine(lines[i]))
            {
                statsIdx.Add(i);
            }
        }

        string roomContent;
        List<BufferedLine> toMark;
        bool recentMovement = (DateTime.UtcNow - _lastMovementCommand).TotalMilliseconds < 2000;

        if (statsIdx.Count > 0)
        {
            var last = statsIdx.Last();
            
            // OPTIMIZATION: After a movement command, only look at recent lines after the last stats
            // Take at most 15 lines after stats for room detection
            if (recentMovement)
            {
                const int maxLinesToScan = 15;
                var linesToScan = lines.Skip(last + 1).Take(maxLinesToScan).ToList();
                roomContent = string.Join('\n', linesToScan);
                toMark = [.. unprocessed.Skip(last + 1).Take(maxLinesToScan)];
                
                System.Diagnostics.Debug.WriteLine($"🔎 TEXT-BASED OPTIMIZED SCAN: Looking at {linesToScan.Count} lines after stats (recent movement)");
            }
            else
            {
                // No recent movement, can scan all lines after stats
                roomContent = string.Join('\n', lines.Skip(last + 1));
                toMark = [.. unprocessed.Skip(last + 1)];
            }
        }
        else
        {
            // No stats found, limit to last 20 lines if recent movement
            if (recentMovement)
            {
                const int maxLinesToScan = 20;
                var linesToScan = lines.TakeLast(maxLinesToScan).ToList();
                roomContent = string.Join('\n', linesToScan);
                toMark = [.. unprocessed.TakeLast(maxLinesToScan)];
                
                System.Diagnostics.Debug.WriteLine($"🔎 TEXT-BASED OPTIMIZED SCAN: No stats found, looking at last {linesToScan.Count} lines");
            }
            else
            {
                roomContent = string.Join('\n', lines);
                toMark = unprocessed;
            }
        }

        if (string.IsNullOrWhiteSpace(roomContent)) return null;

        // Check for look boundary but be more specific
        if (roomContent.IndexOf(LookBoundary, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var boundaryMatches = toMark
                .Where(b => b.Content.IndexOf(LookBoundary, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (boundaryMatches.Any())
            {
                _lineBuffer.MarkProcessed(boundaryMatches, l => l.ProcessedForRoomDetection = true);
            }
            
            var trimmedContent = roomContent.Trim();
            if (trimmedContent.StartsWith(LookBoundary, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            else
            {
                var cleanedLines = lines.Where(l => l.IndexOf(LookBoundary, StringComparison.OrdinalIgnoreCase) < 0).ToList();
                roomContent = string.Join('\n', cleanedLines);
                toMark = [.. toMark.Where(b => b.Content.IndexOf(LookBoundary, StringComparison.OrdinalIgnoreCase) < 0)];
            }
        }

        bool exitsKeywordPresent = roomContent.IndexOf("Exits:", StringComparison.OrdinalIgnoreCase) >= 0;

        if (!roomContent.Contains("Exits:") && !RoomTextProcessor.HasBasicRoomContent(roomContent)) return null;

        var state = RoomParser.Parse(roomContent);

        if (state != null)
        {
            if (state.Exits.Count == 0 && !exitsKeywordPresent)
            {
                return null;
            }

            // CRITICAL FIX: Preserve existing monster disposition information for current room refreshes
            // This handles cases like pressing Enter to refresh the room, which should not reset aggressive monsters
            if (CurrentRoom != null && string.Equals(state.Name, CurrentRoom.Name, StringComparison.OrdinalIgnoreCase))
            {
                // Merge monster disposition information from current room
                var mergedMonsters = new List<MonsterInfo>();
                foreach (var newMonster in state.Monsters)
                {
                    // Look for existing monster with same name
                    var existingMonster = CurrentRoom.Monsters.FirstOrDefault(m => 
                        string.Equals(m.Name, newMonster.Name, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(m.Name?.Replace(" (summoned)", ""), newMonster.Name, StringComparison.OrdinalIgnoreCase));
                    
                    if (existingMonster != null)
                    {
                        // Preserve the existing disposition and targeting info
                        mergedMonsters.Add(new MonsterInfo(
                            newMonster.Name, 
                            existingMonster.Disposition, // Keep existing disposition (aggressive/neutral)
                            existingMonster.TargetingYou, // Keep existing targeting status
                            newMonster.Count));
                        
                        System.Diagnostics.Debug.WriteLine($"🔄 ROOM REFRESH: Preserved {existingMonster.Disposition} disposition for '{newMonster.Name}'");
                    }
                    else
                    {
                        // New monster not seen before, use default neutral disposition
                        mergedMonsters.Add(newMonster);
                        System.Diagnostics.Debug.WriteLine($"➕ ROOM REFRESH: New monster '{newMonster.Name}' added as neutral");
                    }
                }
                
                // Also preserve monsters that might not be in the new parse but are still aggressive
                // (e.g., if the room parsing missed them but they're still there)
                foreach (var currentMonster in CurrentRoom.Monsters)
                {
                    if (!mergedMonsters.Any(m => string.Equals(m.Name, currentMonster.Name, StringComparison.OrdinalIgnoreCase)) &&
                        currentMonster.Disposition.Equals("aggressive", StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep aggressive monsters that might have been missed in parsing
                        mergedMonsters.Add(currentMonster);
                        System.Diagnostics.Debug.WriteLine($"🔄 ROOM REFRESH: Preserved missing aggressive monster '{currentMonster.Name}'");
                    }
                }
                
                // Update the parsed room state with merged monster information AND preserve RoomId
                state = new RoomState
                {
                    Name = state.Name,
                    RoomId = CurrentRoom.RoomId, // Preserve existing RoomId
                    Exits = state.Exits,
                    Items = state.Items,
                    Monsters = mergedMonsters,
                    LastUpdated = DateTime.UtcNow
                };
            }

            _lineBuffer.MarkProcessed(toMark, l => l.ProcessedForRoomDetection = true);
        }
        else
        {
            var nonRoom = toMark.Where(b => RoomTextProcessor.IsObviouslyNonRoomLine(b.Content)).ToList();
            if (nonRoom.Any())
            {
                _lineBuffer.MarkProcessed(nonRoom, l => l.ProcessedForRoomDetection = true);
            }
        }

        return state;
    }

    private static bool MonstersEqual(List<MonsterInfo>? a, List<MonsterInfo>? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;

        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];

            if (x.Name != y.Name
                || x.Disposition != y.Disposition
                || x.TargetingYou != y.TargetingYou
                || x.Count != y.Count)
            {
                return false;
            }
        }

        return true;
    }

    private bool TryParseLookCommand(string user, string character, string screenText)
    {
        var lines = screenText.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();

        var lookPattern = new Regex(@"^(?:\[Hp=.*?\]\s*)?(?:look|l)\s+(north|south|east|west|northeast|northwest|southeast|southwest|up|down|n|s|e|w|ne|nw|se|sw|u|d)\s*$", RegexOptions.IgnoreCase);
        var movementPattern = new Regex(@"^\[Hp=.*?\]\s*(n|s|w|e|ne|se|nw|sw|u|d|north|south|east|west|northeast|northwest|southeast|southwest|up|down)(?:\s|$)", RegexOptions.IgnoreCase);

        // Find the LAST look echo
        int lookIdx = -1;
        string? dir = null;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var m = lookPattern.Match(lines[i]);
            if (m.Success)
            {
                dir = NormalizeDirection(m.Groups[1].Value);
                lookIdx = i;
                break;
            }
        }
        if (dir == null) return false;

        // Find the boundary nearest to/below that look (prefer the newest)
        int boundaryIndex = -1;
        for (int i = lines.Count - 1; i >= lookIdx; i--)
        {
            if (lines[i].IndexOf(LookBoundary, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                boundaryIndex = i;
                break;
            }
        }
        if (boundaryIndex < 0) return false;

        // Look for ACTUAL movement commands after the look echo
        // Only detect explicit directional movement commands, NOT room descriptions
        int movementIdx = -1;
        for (int i = Math.Max(lookIdx + 1, 0); i < lines.Count; i++)
        {
            var line = lines[i];
            
            // Check for explicit movement commands only (n, s, 3n, etc.)
            if (movementPattern.IsMatch(line))
            {
                movementIdx = i;
                System.Diagnostics.Debug.WriteLine($"?? MOVEMENT DETECTED (command): line {i} = '{line}'");
                return false; // Movement detected after look command - don't process look data
            }
        }

        // Build remote segment between boundary and movement (or end if no movement)
        var remoteLines = movementIdx > 0
            ? lines.Skip(boundaryIndex + 1).Take(movementIdx - (boundaryIndex + 1)).ToList()
            : [.. lines.Skip(boundaryIndex + 1)];

        var remoteSegment = string.Join('\n', remoteLines);
        
        System.Diagnostics.Debug.WriteLine($"?? LOOK ANALYSIS: dir={dir}, lookIdx={lookIdx}, boundaryIdx={boundaryIndex}, movementIdx={movementIdx}");
        System.Diagnostics.Debug.WriteLine($"   Remote segment ({remoteLines.Count} lines): '{string.Join(" | ", remoteLines.Take(3))}'");
        
        if (!string.IsNullOrWhiteSpace(remoteSegment) && CurrentRoom != null)
        {
            var parsed = RoomParser.Parse(remoteSegment);
            if (parsed != null)
            {
                lock (_sync)
                {
                    var curKey = MakeKey(user, character, CurrentRoom.Name);
                    if (!_adjacentRoomData.ContainsKey(curKey))
                    {
                        _adjacentRoomData[curKey] = new();
                    }

                    // CRITICAL FIX: Preserve existing monster disposition information
                    // Check if we have existing information about this room
                    var adjKey = MakeKey(user, character, parsed.Name);
                    if (_rooms.ContainsKey(adjKey))
                    {
                        var existingRoom = _rooms[adjKey];
                        
                        // Merge monster disposition information from existing room data
                        var mergedMonsters = new List<MonsterInfo>();
                        foreach (var newMonster in parsed.Monsters)
                        {
                            // Look for existing monster with same name
                            var existingMonster = existingRoom.Monsters.FirstOrDefault(m => 
                                string.Equals(m.Name, newMonster.Name, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(m.Name?.Replace(" (summoned)", ""), newMonster.Name, StringComparison.OrdinalIgnoreCase));
                        
                            if (existingMonster != null)
                            {
                                // Preserve the existing disposition and targeting info
                                mergedMonsters.Add(new MonsterInfo(
                                    newMonster.Name, 
                                    existingMonster.Disposition, // Keep existing disposition (aggressive/neutral)
                                    existingMonster.TargetingYou, // Keep existing targeting status
                                    newMonster.Count));
                            

                                System.Diagnostics.Debug.WriteLine($"🔄 LOOK MERGE: Preserved {existingMonster.Disposition} disposition for '{newMonster.Name}'");
                            }
                            else
                            {
                                // New monster, use default neutral disposition
                                mergedMonsters.Add(newMonster);
                            }
                        }
                        
                        // Also check for monsters that exist in our current room but not in the look result
                        // (they might have moved to the adjacent room)
                        foreach (var currentMonster in CurrentRoom.Monsters)
                        {
                            // If this monster is not in the look result but was aggressive, track it
                            if (!mergedMonsters.Any(m => string.Equals(m.Name, currentMonster.Name, StringComparison.OrdinalIgnoreCase)) &&
                                currentMonster.Disposition.Equals("aggressive", StringComparison.OrdinalIgnoreCase))
                            {
                                // Monster may have moved to the adjacent room - add it with aggressive disposition
                                mergedMonsters.Add(new MonsterInfo(
                                    currentMonster.Name,
                                    currentMonster.Disposition,
                                    currentMonster.TargetingYou,
                                    currentMonster.Count));
                                
                                System.Diagnostics.Debug.WriteLine($"🔄 LOOK MERGE: Added missing aggressive monster '{currentMonster.Name}' to adjacent room");
                            }
                        }
                        
                        // Update the parsed room with merged data
                        parsed = new RoomState
                        {
                            Name = parsed.Name,
                            RoomId = existingRoom.RoomId, // Preserve existing RoomId from stored room data
                            Exits = parsed.Exits,
                            Items = parsed.Items,
                            Monsters = mergedMonsters,
                            LastUpdated = DateTime.UtcNow
                        };
                    }

                    _adjacentRoomData[curKey][dir] = parsed;

                    if (!_rooms.ContainsKey(adjKey))
                    {
                        _rooms[adjKey] = parsed;
                    }
                    else
                    {
                        // Update existing room with merged data
                        _rooms[adjKey] = parsed;
                    }
                    
                    LinkRooms(user, character, CurrentRoom.Name, dir, parsed.Name);
                }
                _lastLookParsed = DateTime.UtcNow;
                
                System.Diagnostics.Debug.WriteLine($"?? LOOK PARSED: {dir} -> '{parsed.Name}' (monsters: {parsed.Monsters.Count}, exits: {string.Join(",", parsed.Exits)})");
            }
        }

        // Only mark lines as processed that are clearly part of the look output
        // Don't mark movement lines to avoid interfering with room detection
        try
        {
            var bufferLines = _lineBuffer.GetUnprocessedLines(l => l.ProcessedForRoomDetection).ToList();
            var toMark = bufferLines
                .Where(b => remoteLines.Contains(b.Content) && !movementPattern.IsMatch(b.Content))
                .ToList();

            // Ensure the specific boundary line that was found is marked as processed
            var boundaryLineInScreenText = lines[boundaryIndex];
            var boundaryLineInLookBuffer = bufferLines.FirstOrDefault(b => b.Content == boundaryLineInScreenText);
            if (boundaryLineInLookBuffer != null && !toMark.Contains(boundaryLineInLookBuffer))
            {
                toMark.Add(boundaryLineInLookBuffer);
            }

            if (toMark.Count > 0)
            {
                _lineBuffer.MarkProcessed(toMark, l => l.ProcessedForRoomDetection = true);
                System.Diagnostics.Debug.WriteLine($"?? LOOK PROCESSING: marked {toMark.Count} lines as processed");
            }
        }
        catch { }

        // Return true if we found a valid look, regardless of movement detection
        // The key insight: we should always return true for valid look parsing
        // Movement detection is just used to limit what gets marked as processed
        var foundValidLook = !string.IsNullOrWhiteSpace(remoteSegment);
        
        if (foundValidLook)
        {
            System.Diagnostics.Debug.WriteLine($"?? LOOK COMMAND HANDLED: movement detected = {movementIdx > 0}");
        }
        
        return foundValidLook;
    }

    public void UpdateRoom(string user, string character, RoomState state)
    {
        var key = MakeKey(user, character, state.Name);
        RoomState? prev;

        lock (_sync)
        {
            _rooms[key] = state;
            _currentRoomKey = key;
            prev = CurrentRoom;
            
            // CRITICAL: Store the previous room's ID before changing to the new room
            // This ensures we always have context about where we came from
            // ONLY update if this is actually a NEW room (not just a RoomId being set)
            if (prev != null && !string.IsNullOrEmpty(prev.RoomId) && 
                (prev.Name != state.Name || prev.RoomId != state.RoomId))
            {
                _previousRoomId = prev.RoomId;
                System.Diagnostics.Debug.WriteLine($"📍 ROOM TRANSITION: {_previousRoomId} -> {state.RoomId ?? "unknown"} (direction: {_lastMovementDirection ?? "unknown"})");
            }
            
            CurrentRoom = state;
            if (!_edges.ContainsKey(key))
            {
                _edges[key] = new();
            }
        }

        try
        {
            // CRITICAL FIX: Only fire RoomChanged if something ACTUALLY changed
            // Don't fire just because RoomId was set - that's not a room change
            // We have to fire if there is a movement command and a room was parsed.
            // if we dont we will not detect duplicate room changes. Only check if we dont know the room though
            bool actualChange = prev == null
                || prev.Name != state.Name
                || !prev.Exits.SequenceEqual(state.Exits)
                || !MonstersEqual(prev.Monsters, state.Monsters)
                || !prev.Items.SequenceEqual(state.Items)
                || (_lastMovementDirection != null && string.IsNullOrEmpty(state.RoomId));
       
            
            if (actualChange)
            {
                System.Diagnostics.Debug.WriteLine($"🔔 FIRING RoomChanged: {prev?.Name ?? "null"} -> {state.Name}, RoomId: {prev?.RoomId ?? "null"} -> {state.RoomId ?? "null"}");
                RoomChanged?.Invoke(state);
                _lastMovementDirection = null;
             }


        }
        catch
        {
        }
    }

    public void LinkRooms(string user, string character, string from, string direction, string to)
    {
        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to) || from == to) return;

        var kFrom = MakeKey(user, character, from);
        var kTo = MakeKey(user, character, to);
        var rev = ReverseDir(direction);

        lock (_sync)
        {
            if (!_edges.ContainsKey(kFrom)) _edges[kFrom] = new();
            if (!_edges.ContainsKey(kTo)) _edges[kTo] = new();
            _edges[kFrom][direction] = kTo;
            if (rev != string.Empty) _edges[kTo][rev] = kFrom;
        }
    }

    public Dictionary<string, string> GetAdjacent(string user, string character)
    {
        lock (_sync)
        {
            if (CurrentRoom == null) return new();

            var key = MakeKey(user, character, CurrentRoom.Name);
            var res = new Dictionary<string, string>();

            if (_edges.TryGetValue(key, out var edgeDict))
            {
                foreach (var kv in edgeDict)
                {
                    res[kv.Key] = _rooms.TryGetValue(kv.Value, out var rs) ? rs.Name : "?";
                }
            }

            if (_adjacentRoomData.TryGetValue(key, out var lookDict))
            {
                foreach (var kv in lookDict)
                {
                    if (!res.ContainsKey(kv.Key))
                    {
                        res[kv.Key] = $"{kv.Value.Name} (looked)";
                    }
                }
            }

            return res;
        }
    }

    public RoomGridData GetRoomGrid(string user, string character)
    {
        lock (_sync)
        {
            var grid = new RoomGridData();
            if (CurrentRoom == null) return grid;

            var curKey = MakeKey(user, character, CurrentRoom.Name);

            grid.Center = new GridRoomInfo
            {
                Name = CurrentRoom.Name,
                Exits = [.. CurrentRoom.Exits],
                MonsterCount = CurrentRoom.Monsters.Count,
                HasMonsters = CurrentRoom.Monsters.Count > 0,
                HasAggressiveMonsters = CurrentRoom.Monsters.Any(m => m.Disposition == "aggressive"),
                ItemCount = CurrentRoom.Items.Count,
                HasItems = CurrentRoom.Items.Count > 0,
                IsKnown = true,
                IsCurrentRoom = true
            };

            foreach (var exit in CurrentRoom.Exits)
            {
                var dir = exit.ToLowerInvariant();
                RoomState? adj = null;

                if (_edges.TryGetValue(curKey, out var d)
                    && d.TryGetValue(dir, out var key)
                    && _rooms.TryGetValue(key, out var known))
                {
                    adj = known;
                }
                else if (_adjacentRoomData.TryGetValue(curKey, out var a)
                         && a.TryGetValue(dir, out var looked))
                {
                    adj = looked;
                }

                GridRoomInfo info = adj != null
                    ? new GridRoomInfo
                    {
                        Name = adj.Name,
                        Exits = [.. adj.Exits],
                        MonsterCount = adj.Monsters.Count,
                        HasMonsters = adj.Monsters.Count > 0,
                        HasAggressiveMonsters = adj.Monsters.Any(m => m.Disposition == "aggressive"),
                        ItemCount = adj.Items.Count,
                        HasItems = adj.Items.Count > 0,
                        IsKnown = true,
                        IsCurrentRoom = false
                    }
                    : new GridRoomInfo
                    {
                        Name = "Unexplored",
                        IsKnown = false
                    };

                switch (dir)
                {
                    case "north": grid.North = info; break;
                    case "south": grid.South = info; break;
                    case "east": grid.East = info; break;
                    case "west": grid.West = info; break;
                    case "northeast": grid.Northeast = info; break;
                    case "northwest": grid.Northwest = info; break;
                    case "southeast": grid.Southeast = info; break;
                    case "southwest": grid.Southwest = info; break;
                    case "up": grid.Up = info; break;
                    case "down": grid.Down = info; break;
                }
            }

            return grid;
        }
    }

    private static string MakeKey(string user, string character, string room) => $"{user}|{character}|{room}";

    private static string ReverseDir(string d) => d switch
    {
        "north" => "south",
        "south" => "north",
        "east" => "west",
        "west" => "east",
        "northeast" => "southwest",
        "southwest" => "northeast",
        "northwest" => "southeast",
        "southeast" => "northwest",
        "up" => "down",
        "down" => "up",
        _ => string.Empty
    };

    private static string? NormalizeDirection(string dir)
    {
        dir = dir.ToLowerInvariant();
        return dir switch
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
            "north" or "south" or "east" or "west" or "northeast" or "northwest" or "southeast" or "southwest" or "up" or "down" => dir,
            _ => null
        };
    }

    public bool UpdateMonsterDisposition(string monsterName, string newDisposition)
    {
        lock (_sync)
        {
            if (CurrentRoom?.Monsters == null || CurrentRoom.Monsters.Count == 0)
                return false;

            var existingMonster = CurrentRoom.Monsters.FirstOrDefault(m => 
                string.Equals(m.Name, monsterName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Name?.Replace(" (summoned)", ""), monsterName, StringComparison.OrdinalIgnoreCase));

            if (existingMonster != null && !existingMonster.Disposition.Equals(newDisposition, StringComparison.OrdinalIgnoreCase))
            {
                var updatedMonsters = CurrentRoom.Monsters.ToList();
                updatedMonsters.Remove(existingMonster);
                updatedMonsters.Add(new MonsterInfo(
                    existingMonster.Name, 
                    newDisposition, 
                    existingMonster.TargetingYou, 
                    existingMonster.Count));

                CurrentRoom.Monsters.Clear();
                CurrentRoom.Monsters.AddRange(updatedMonsters);
                CurrentRoom.LastUpdated = DateTime.UtcNow;

                try
                {
                    RoomChanged?.Invoke(CurrentRoom);
                }
                catch
                {
                }

                return true;
            }

            return false;
        }
    }
}