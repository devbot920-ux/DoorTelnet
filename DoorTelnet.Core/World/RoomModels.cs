using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DoorTelnet.Core.World;

/// <summary>
/// Streamlined RoomTracker that uses segmented components for better maintainability
/// </summary>
public class RoomTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RoomState> _rooms = new();
    private readonly Dictionary<string, Dictionary<string, string>> _edges = new();
    private string _currentRoomKey = string.Empty;
    private DateTime _lastRoomChange = DateTime.MinValue;
    private readonly Dictionary<string, Dictionary<string, RoomState>> _adjacentRoomData = new();
    private readonly LineBuffer _lineBuffer = new();
    private const string LookBoundary = "You peer 1 room away";
    private DateTime _lastLookParsed = DateTime.MinValue;

    // Optional CombatTracker integration for unified death handling
    public object? CombatTracker { get; set; }

    public RoomState? CurrentRoom { get; private set; }
    public event Action<RoomState>? RoomChanged;

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
        }
    }

    public bool TryUpdateRoom(string user, string character, string screenText)
    {
        var handledLook = TryParseLookCommand(user, character, screenText);
        bool anyUpdate = handledLook;

        if (handledLook)
        {
            System.Diagnostics.Debug.WriteLine("?? Skipping current room update due to look command processing");
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
            
            var roomState = ParseRoomFromBuffer();
            if (roomState == null)
            {
                if (QuickAugmentMonsters(user, character)) anyUpdate = true;
            }
            else
            {
                if (!roomState.Name.Contains("(looked)"))
                {
                    var isNewRoom = CurrentRoom == null
                                    || CurrentRoom.Name != roomState.Name
                                    || !CurrentRoom.Exits.SequenceEqual(roomState.Exits);
                    var monstersChanged = !MonstersEqual(CurrentRoom?.Monsters, roomState.Monsters);
                    var itemsChanged = (CurrentRoom == null && roomState.Items.Count > 0)
                                       || (CurrentRoom != null && !CurrentRoom.Items.SequenceEqual(roomState.Items));

                    if (isNewRoom || monstersChanged || itemsChanged)
                    {
                        UpdateRoom(user, character, roomState);
                        _lastRoomChange = DateTime.UtcNow;
                        anyUpdate = true;
                    }
                }
            }

            if (!anyUpdate && CurrentRoom != null && (DateTime.UtcNow - CurrentRoom.LastUpdated).TotalMilliseconds > 200)
            {
                CurrentRoom.LastUpdated = DateTime.UtcNow;
                try { RoomChanged?.Invoke(CurrentRoom); } catch { }
                anyUpdate = true;
            }
        }

        return anyUpdate;
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
            Exits = CurrentRoom.Exits.ToList(),
            Monsters = updated,
            Items = CurrentRoom.Items.ToList(),
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
            Exits = CurrentRoom.Exits.ToList(),
            Items = CurrentRoom.Items.ToList(),
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

        if (statsIdx.Count > 0)
        {
            var last = statsIdx.Last();
            roomContent = string.Join('\n', lines.Skip(last + 1));
            toMark = unprocessed.Skip(last + 1).ToList();
        }
        else
        {
            roomContent = string.Join('\n', lines);
            toMark = unprocessed;
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
                toMark = toMark.Where(b => b.Content.IndexOf(LookBoundary, StringComparison.OrdinalIgnoreCase) < 0).ToList();
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
                        
                        System.Diagnostics.Debug.WriteLine($"?? ROOM REFRESH: Preserved {existingMonster.Disposition} disposition for '{newMonster.Name}'");
                    }
                    else
                    {
                        // New monster not seen before, use default neutral disposition
                        mergedMonsters.Add(newMonster);
                        System.Diagnostics.Debug.WriteLine($"?? ROOM REFRESH: New monster '{newMonster.Name}' added as neutral");
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
                        System.Diagnostics.Debug.WriteLine($"?? ROOM REFRESH: Preserved missing aggressive monster '{currentMonster.Name}'");
                    }
                }
                
                // Update the parsed room state with merged monster information
                state = new RoomState
                {
                    Name = state.Name,
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
            : lines.Skip(boundaryIndex + 1).ToList();

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
                                
                                System.Diagnostics.Debug.WriteLine($"?? LOOK MERGE: Preserved {existingMonster.Disposition} disposition for '{newMonster.Name}'");
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
                                
                                System.Diagnostics.Debug.WriteLine($"?? LOOK MERGE: Added missing aggressive monster '{currentMonster.Name}' to adjacent room");
                            }
                        }
                        
                        // Update the parsed room with merged monster information
                        parsed = new RoomState
                        {
                            Name = parsed.Name,
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
            CurrentRoom = state;
            if (!_edges.ContainsKey(key))
            {
                _edges[key] = new();
            }
        }

        try
        {
            if (prev == null
                || prev.Name != state.Name
                || !prev.Exits.SequenceEqual(state.Exits)
                || !MonstersEqual(prev.Monsters, state.Monsters)
                || !prev.Items.SequenceEqual(state.Items))
            {
                RoomChanged?.Invoke(state);
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
                Exits = CurrentRoom.Exits.ToList(),
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
                        Exits = adj.Exits.ToList(),
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
}