using System.Text.RegularExpressions;
using System.Text;

namespace DoorTelnet.Core.World;

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

public class LineBuffer
{
    private readonly Queue<BufferedLine> _lines = new();
    private readonly object _sync = new();
    private const int MaxLines = 1000;

    public void AddLine(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return;

        if (_lines.Count < 10)
        {
            var debugContent = new StringBuilder();
            foreach (char c in content.Take(50))
            {
                if (c >= 32 && c <= 126)
                {
                    debugContent.Append(c);
                }
                else if (c == '\n')
                {
                    debugContent.Append("\\n");
                }
                else if (c == '\r')
                {
                    debugContent.Append("\\r");
                }
                else if (c == '\t')
                {
                    debugContent.Append("\\t");
                }
                else
                {
                    debugContent.Append($"[{(int)c:X2}]");
                }
            }
            System.Diagnostics.Debug.WriteLine($"?? Adding to buffer: '{debugContent}'");
        }

        lock (_sync)
        {
            _lines.Enqueue(new BufferedLine { Content = content });
            while (_lines.Count > MaxLines)
            {
                _lines.Dequeue();
            }
        }
    }

    public IEnumerable<BufferedLine> GetUnprocessedLines(Func<BufferedLine, bool> processedCheck)
    {
        lock (_sync)
        {
            return _lines
                .Where(line => !processedCheck(line))
                .ToList();
        }
    }

    public IEnumerable<BufferedLine> GetAllLines()
    {
        lock (_sync)
        {
            return _lines.ToList();
        }
    }

    public void MarkProcessed(IEnumerable<BufferedLine> lines, Action<BufferedLine> markAction)
    {
        lock (_sync)
        {
            foreach (var line in lines)
            {
                markAction(line);
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _lines.Count;
            }
        }
    }

    public string GetBufferDebugInfo()
    {
        lock (_sync)
        {
            var debug = new StringBuilder();
            debug.AppendLine($"=== LINE BUFFER DEBUG ({_lines.Count} lines) ===");
            var recentLines = _lines.TakeLast(10).ToList();
            for (int i = 0; i < recentLines.Count; i++)
            {
                var line = recentLines[i];
                var flags = new List<string>();
                if (line.ProcessedForRoomDetection) flags.Add("Room");
                if (line.ProcessedForDynamicEvents) flags.Add("Dynamic");
                if (line.ProcessedForStats) flags.Add("Stats");
                if (line.ProcessedForIncarnations) flags.Add("Incarnations");

                var flagStr = flags.Count > 0
                    ? $" [{string.Join(',', flags)}]"
                    : " [Unprocessed]";

                var cleanContent = new StringBuilder();
                foreach (char c in line.Content.Take(80))
                {
                    if (c >= 32 && c <= 126)
                    {
                        cleanContent.Append(c);
                    }
                    else if (c == '\n')
                    {
                        cleanContent.Append("\\n");
                    }
                    else if (c == '\r')
                    {
                        cleanContent.Append("\\r");
                    }
                    else if (c == '\t')
                    {
                        cleanContent.Append("\\t");
                    }
                    else
                    {
                        cleanContent.Append($"[{(int)c:X2}]");
                    }
                }
                debug.AppendLine($"  {i:D2}: '{cleanContent}'{flagStr}");
            }
            debug.AppendLine("=================================");
            return debug.ToString();
        }
    }
}

public class RoomTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, RoomState> _rooms = new();
    private readonly Dictionary<string, Dictionary<string, string>> _edges = new();
    private string _currentRoomKey = string.Empty;
    private DateTime _lastRoomChange = DateTime.MinValue;
    private readonly Dictionary<string, Dictionary<string, RoomState>> _adjacentRoomData = new();
    private readonly LineBuffer _lineBuffer = new();
    private const string LookBoundary = "You peer 1 room away"; // boundary marker for directional look
    private DateTime _lastLookParsed = DateTime.MinValue; // throttle duplicate look output handling

    public RoomState? CurrentRoom { get; private set; }

    public event Action<RoomState>? RoomChanged; // New event for UI consumers

    // Restored helper methods used by CLI / other subsystems
    public IEnumerable<BufferedLine> GetUnprocessedLinesForStats() => _lineBuffer.GetUnprocessedLines(l => l.ProcessedForStats);

    public void MarkLineProcessedForStats(BufferedLine line) => _lineBuffer.MarkProcessed(new[] { line }, l => l.ProcessedForStats = true);

    public bool HasUnprocessedDynamicEvents()
    {
        foreach (var bl in _lineBuffer.GetUnprocessedLines(l => l.ProcessedForDynamicEvents))
        {
            var line = StripLeadingPartialStats(bl.Content).stripped;
            if (Regex.IsMatch(line, @"^(?:A|An)\s+(.+?)\s+is\s+summoned\s+for\s+combat!?\s*$", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(line, @"^(?:A |An |The )?(.+?)\s+(enters|arrives)\s+", RegexOptions.IgnoreCase)) return true;
            if (IsDeathLine(line) && FindMatchingMonsterNames(line).Any()) return true;
        }
        return false;
    }

    public void AddLine(string line)
    {
        var originalLine = line;
        var cleanedLine = CleanLineContent(line);

        if (_lineBuffer.Count < 10 && originalLine != cleanedLine)
        {
            System.Diagnostics.Debug.WriteLine("?? CLEANING APPLIED:");
            System.Diagnostics.Debug.WriteLine($"   RAW: '{originalLine}'");
            System.Diagnostics.Debug.WriteLine($"   CLEAN: '{cleanedLine}'");
        }

        if (!string.IsNullOrEmpty(cleanedLine))
        {
            _lineBuffer.AddLine(cleanedLine);
        }
        else if (!string.IsNullOrEmpty(originalLine) && _lineBuffer.Count < 10)
        {
            System.Diagnostics.Debug.WriteLine($"??? FILTERED OUT: '{originalLine}'");
        }
    }

    private static (string stripped, bool hadFragments) StripLeadingPartialStats(string line)
    {
        if (string.IsNullOrEmpty(line)) return (line, false);

        var pattern = @"^(?:[\x00-\x20]*p=\d+/Mp=\d+/Mv=\d+]\s*)+";
        bool had = false;
        int guard = 0;

        while (Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase) && guard < 10)
        {
            had = true;
            line = Regex.Replace(line, pattern, string.Empty, RegexOptions.IgnoreCase);
            guard++;
        }

        return (line.TrimStart(), had);
    }

    private static string CleanLineContent(string line)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;

        var (afterStrip, had) = StripLeadingPartialStats(line);
        line = afterStrip;

        if (had && string.IsNullOrWhiteSpace(line)) return string.Empty;

        if (Regex.IsMatch(line, @"^(p=|Mp=|Mv=|Ac=)\d+", RegexOptions.IgnoreCase)) return string.Empty;

        var fragCount = Regex.Matches(line, @"(p=\d+|Mp=\d+|Mv=\d+)", RegexOptions.IgnoreCase).Count;
        if (fragCount >= 3 && !line.Contains("[Hp=")) return string.Empty;

        line = Regex.Replace(line, @"\x1B\[[0-9;]*[A-Za-z]", "");
        line = Regex.Replace(line, @"\[[0-9;]*m", "");
        line = Regex.Replace(line, @"\[[0-9]*[A-Za-z]", "");
        line = Regex.Replace(line, @"\x1B[()][A-Za-z0-9]", "");
        line = Regex.Replace(line, @"\[[0-9;]*[A-Za-z@]", "");

        var sb = new StringBuilder();
        foreach (var c in line)
        {
            if (c >= 32 && c <= 126)
            {
                sb.Append(c);
            }
            else if (c == '\t')
            {
                sb.Append(' ');
            }
        }

        var result = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return result.Length < 2 ? string.Empty : result;
    }

    public bool TryUpdateRoom(string user, string character, string screenText)
    {
        // First attempt to capture directional LOOK without letting remote room override current.
        if (TryParseLookCommand(user, character, screenText)) return true; // adjacency captured

        // Reduce throttling to improve detection speed
        if ((DateTime.UtcNow - _lastRoomChange).TotalMilliseconds < 25)
        {
            var unprocessedCount = _lineBuffer.GetUnprocessedLines(l => l.ProcessedForRoomDetection).Count();
            if (unprocessedCount == 0) return false;
        }

        bool anyUpdate = false;

        lock (_sync)
        {
            if (TryHandleDynamicEvents(user, character))
            {
                anyUpdate = true;
            }

            var roomState = ParseRoomFromBuffer();
            if (roomState == null)
            {
                // Attempt quick monster augmentation if no full room parsed.
                if (QuickAugmentMonsters(user, character)) anyUpdate = true;
            }
            else
            {
                if (!roomState.Name.Contains("(looked)"))
                {
                    bool isNewRoom = CurrentRoom == null
                        || CurrentRoom.Name != roomState.Name
                        || !CurrentRoom.Exits.SequenceEqual(roomState.Exits);

                    bool monstersChanged = !MonstersEqual(CurrentRoom?.Monsters, roomState.Monsters);

                    bool itemsChanged = (CurrentRoom == null && roomState.Items.Count > 0)
                        || (CurrentRoom != null && !CurrentRoom.Items.SequenceEqual(roomState.Items));

                    if (isNewRoom || monstersChanged || itemsChanged)
                    {
                        UpdateRoom(user, character, roomState);
                        _lastRoomChange = DateTime.UtcNow;
                        anyUpdate = true;
                    }
                }
            }

            // Reduce threshold for lightweight timestamp refresh to be more responsive
            if (!anyUpdate && CurrentRoom != null && (DateTime.UtcNow - CurrentRoom.LastUpdated).TotalMilliseconds > 200)
            {
                CurrentRoom.LastUpdated = DateTime.UtcNow;
                try
                {
                    RoomChanged?.Invoke(CurrentRoom);
                }
                catch
                {
                    // ignored
                }
                anyUpdate = true;
            }
        }

        return anyUpdate;
    }

    /// <summary>
    /// Update a monster's disposition in the current room
    /// </summary>
    /// <param name="monsterName">The name of the monster to update</param>
    /// <param name="newDisposition">The new disposition (e.g., "aggressive", "neutral")</param>
    /// <returns>True if the monster was found and updated</returns>
    public bool UpdateMonsterDisposition(string monsterName, string newDisposition)
    {
        lock (_sync)
        {
            if (CurrentRoom?.Monsters == null || CurrentRoom.Monsters.Count == 0)
                return false;

            // Find the monster in the current room
            var existingMonster = CurrentRoom.Monsters.FirstOrDefault(m => 
                string.Equals(m.Name, monsterName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(m.Name?.Replace(" (summoned)", ""), monsterName, StringComparison.OrdinalIgnoreCase));

            if (existingMonster != null && !existingMonster.Disposition.Equals(newDisposition, StringComparison.OrdinalIgnoreCase))
            {
                // Monster exists but has different disposition - update it
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

                // Fire room changed event
                try
                {
                    RoomChanged?.Invoke(CurrentRoom);
                }
                catch
                {
                    // ignored
                }

                return true;
            }

            return false;
        }
    }

    // Quick augmentation: If we have standalone monster lines ("X is here." or variants) not yet processed, add them.
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

            var names = new List<string>();
            if (listPart.Contains(','))
            {
                // Split by comma first
                var commaParts = Regex.Split(listPart, @"\s*,\s*")
                    .Where(p => p.Length > 0)
                    .ToList();

                if (commaParts.Count > 0)
                {
                    // Process all parts except the last one normally
                    for (int i = 0; i < commaParts.Count - 1; i++)
                    {
                        names.Add(commaParts[i].Trim());
                    }

                    // Handle the last part, which might contain " and "
                    var lastPart = commaParts[^1].Trim();
                    if (Regex.IsMatch(lastPart, @"\s+and\s+", RegexOptions.IgnoreCase))
                    {
                        // Split "Y and Z" into separate parts
                        var lastParts = Regex.Split(lastPart, @"\s+and\s+", RegexOptions.IgnoreCase)
                            .Where(p => p.Length > 0)
                            .Select(p => p.Trim());
                        names.AddRange(lastParts);
                    }
                    else
                    {
                        // No " and " in the last part, add it as-is
                        names.Add(lastPart);
                    }
                }
            }
            else if (Regex.IsMatch(listPart, @"\s+and\s+", RegexOptions.IgnoreCase))
            {
                // No commas, just split by " and " (e.g., "X and Y")
                names.AddRange(Regex.Split(listPart, @"\s+and\s+", RegexOptions.IgnoreCase)
                    .Where(p => p.Length > 0)
                    .Select(p => p.Trim()));
            }
            else
            {
                // Single monster
                names.Add(listPart);
            }

            foreach (var n in names)
            {
                var name = n.Trim();
                if (name.Length == 0) continue;

                // Always add monsters, even if we already have one with the same name
                // This allows for multiple monsters with the same name in a room
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

    // New helper: Determine if a line is a death line (based only on last word keyword)
    private static bool IsDeathLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;

        var trimmed = line.TrimEnd();

        // Accept ending punctuation . or ! (possibly both forms repeated) but optional
        // Extract last token (letters only) ignoring trailing punctuation
        int i = trimmed.Length - 1;
        while (i >= 0 && (trimmed[i] == '!' || trimmed[i] == '.' || trimmed[i] == ' ')) i--;
        int end = i;
        if (end < 0) return false;
        while (i >= 0 && char.IsLetter(trimmed[i])) i--;
        var lastWord = trimmed.Substring(i + 1, end - i).ToLowerInvariant();
        return lastWord is "banished" or "cracks" or "darkness" or "dead" or "death" or "defeated" or "dies" or "disappears" or "earth" or "exhausted" or "existance" or "existence" or "flames" or "goddess" or "gone" or "ground" or "himself" or "killed" or "lifeless" or "mana" or "manaless" or "nothingness" or "over" or "pieces" or "portal" or "scattered" or "silent" or "slain" or "still" or "vortex"; // per user specification
    }

    // New helper: find current room monster names that appear inside the line
    private IEnumerable<string> FindMatchingMonsterNames(string line)
    {
        if (CurrentRoom == null) yield break;

        foreach (var m in CurrentRoom.Monsters)
        {
            var name = m.Name;
            // If monster names have annotations like "(summoned)" remove annotation for matching
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

        // Local helper to strip (summoned) for comparisons
        static string StripSummonedSuffix(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name;
            return name.Replace(" (summoned)", "", StringComparison.OrdinalIgnoreCase);
        }

        foreach (var bl in unprocessed)
        {
            var original = bl.Content;
            var line = StripLeadingPartialStats(original).stripped;
            System.Diagnostics.Debug.WriteLine($"?? DYN LINE: '{original}' -> '{line}'");

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

            // New death detection: only if last word indicates death keyword, then remove monsters whose names appear in the line
            if (IsDeathLine(line))
            {
                var matches = FindMatchingMonsterNames(line)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (matches.Count > 0)
                {
                    toRemove.AddRange(matches);
                    System.Diagnostics.Debug.WriteLine($"?? DEATH LINE matched keywords; removing monsters: {string.Join(", ", matches)}");
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

        // If this buffer still contains a LOOK boundary, skip treating it as current room content.
        if (roomContent.IndexOf(LookBoundary, StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var boundaryMatches = toMark
                .Where(b => b.Content.IndexOf(LookBoundary, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

            if (boundaryMatches.Any())
            {
                _lineBuffer.MarkProcessed(boundaryMatches, l => l.ProcessedForRoomDetection = true);
            }
            return null;
        }

        // Added: defer parsing result if we have not yet seen an Exits line so we can accumulate preceding lines.
        bool exitsKeywordPresent = roomContent.IndexOf("Exits:", StringComparison.OrdinalIgnoreCase) >= 0;

        if (!roomContent.Contains("Exits:") && !HasBasicRoomContent(roomContent)) return null;

        var state = RoomParser.Parse(roomContent);

        if (state != null)
        {
            // NEW LOGIC: If the parse produced a room with NO exits and we have not yet actually
            // received an "Exits:" line, treat this as a partial room snapshot and keep lines unprocessed
            // so that when the Exits line arrives we can re-parse with the full context.
            if (state.Exits.Count == 0 && !exitsKeywordPresent)
            {
                System.Diagnostics.Debug.WriteLine("?? PARTIAL ROOM (no exits yet) - deferring mark & update");
                return null; // do NOT mark lines yet
            }

            _lineBuffer.MarkProcessed(toMark, l => l.ProcessedForRoomDetection = true);
        }
        else
        {
            var nonRoom = toMark.Where(b => IsObviouslyNonRoomLine(b.Content)).ToList();
            if (nonRoom.Any())
            {
                _lineBuffer.MarkProcessed(nonRoom, l => l.ProcessedForRoomDetection = true);
            }
        }

        return state;
    }

    private static bool HasBasicRoomContent(string c)
    {
        return c.Contains("Exits:")
               || c.Contains(" is here")
               || c.Contains(" are here")
               || c.Contains(" lay here")
               || c.Contains(" lays here")
               || c.Contains("summoned for combat")
               || c.Contains(" enters ")
               || c.Contains(" leaves ")
               || c.Contains(" follows you")
               || Regex.IsMatch(c, @"^(?:.+?)\s+(?:is|are|stands|lurks|waits)\s+here\.?$", RegexOptions.IgnoreCase);
    }

    private static bool IsObviouslyNonRoomLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;

        line = line.Trim();

        if (RoomParser.IsStatsLine(line)) return true;
        if (Regex.IsMatch(line, @"^(p=|Mp=|Mv=|Ac=)\d+", RegexOptions.IgnoreCase)) return true;
        if (line.Contains("p=") && line.Contains("Mp=") && line.Contains("Mv=") && !line.Contains("[Hp=")) return true;
        if (line.StartsWith(">")
            || line.StartsWith("Enter")
            || line.StartsWith("Press")
            || line.StartsWith("Type")
            || line.StartsWith("Command:")
            || line.StartsWith("Password:")
            || line.StartsWith("Username:")
            || line.StartsWith("Login:"))
        {
            return true;
        }

        if (line.Length < 3) return true;

        if (line.Contains("connected")
            || line.Contains("logged in")
            || line.Contains("disconnected")
            || line.Contains("Welcome")
            || line.Contains("Goodbye")
            || line.Contains("*** "))
        {
            return true;
        }

        if (Regex.IsMatch(line, @"^[\]})][^a-zA-Z]*$")) return true;

        return false;
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
        // A LOOK directional command produces remote room data following boundary line.
        // We must capture adjacency but not let remote room become current.
        var lines = screenText
            .Split('\n')
            .Select(l => l.TrimEnd('\r').Trim())
            .Where(l => l.Length > 0)
            .ToList();

        // Allow optional stats prefix before the command; handle variants: look n, l n, look north, l north
        var lookPattern = new Regex(@"^(?:\[Hp=.*?\]\s*)?(?:look|l)\s+(north|south|east|west|northeast|northwest|southeast|southwest|up|down|n|s|e|w|ne|nw|se|sw|u|d)\s*$", RegexOptions.IgnoreCase);

        string? dir = null;
        foreach (var line in lines)
        {
            var m = lookPattern.Match(line);
            if (m.Success)
            {
                dir = NormalizeDirection(m.Groups[1].Value);
                break;
            }
        }

        if (dir == null) return false;

        // Find boundary line
        int boundaryIndex = lines.FindIndex(l => l.IndexOf(LookBoundary, StringComparison.OrdinalIgnoreCase) >= 0);
        if (boundaryIndex < 0) return false; // boundary not present yet

        // Avoid re-processing the same look output (user might press Up/Enter quickly)
        if ((DateTime.UtcNow - _lastLookParsed).TotalMilliseconds < 150 && boundaryIndex == lines.Count - 1)
        {
            return true; // treat as handled
        }

        var remoteSegment = string.Join('\n', lines.Skip(boundaryIndex + 1));
        if (string.IsNullOrWhiteSpace(remoteSegment)) return true; // nothing after boundary yet

        var parsed = RoomParser.Parse(remoteSegment);
        if (parsed != null && CurrentRoom != null)
        {
            lock (_sync)
            {
                var curKey = MakeKey(user, character, CurrentRoom.Name);
                if (!_adjacentRoomData.ContainsKey(curKey))
                {
                    _adjacentRoomData[curKey] = new();
                }
                _adjacentRoomData[curKey][dir] = parsed;

                var adjKey = MakeKey(user, character, parsed.Name);
                if (!_rooms.ContainsKey(adjKey))
                {
                    _rooms[adjKey] = parsed;
                }
                LinkRooms(user, character, CurrentRoom.Name, dir, parsed.Name);
            }
            _lastLookParsed = DateTime.UtcNow;
        }

        return true; // handled (prevent remote room from being parsed as current)
    }

    private static string NormalizeDirection(string dir)
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

        // Fire outside lock to avoid deadlocks
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
            // ignored
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
            if (!_edges.ContainsKey(kFrom))
            {
                _edges[kFrom] = new();
            }
            if (!_edges.ContainsKey(kTo))
            {
                _edges[kTo] = new();
            }
            _edges[kFrom][direction] = kTo;
            if (rev != string.Empty)
            {
                _edges[kTo][rev] = kFrom;
            }
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
                    case "north":
                        grid.North = info;
                        break;
                    case "south":
                        grid.South = info;
                        break;
                    case "east":
                        grid.East = info;
                        break;
                    case "west":
                        grid.West = info;
                        break;
                    case "northeast":
                        grid.Northeast = info;
                        break;
                    case "northwest":
                        grid.Northwest = info;
                        break;
                    case "southeast":
                        grid.Southwest = info; // (retained original behavior)
                        break;
                    case "southwest":
                        grid.Southwest = info;
                        break;
                    case "up":
                        grid.Up = info;
                        break;
                    case "down":
                        grid.Down = info;
                        break;
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
}

public static class RoomParser
{
    private static readonly Regex ExitsLine = new(@"^(?:Obvious )?Exits?:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex MonsterLine = new(@"^(?<list>.+?)\s+(?:is|are|stands|lurks|waits)\s+here\.?$", RegexOptions.IgnoreCase);
    private static readonly Regex MonstersSplitConjunction = new(@"\s+and\s+", RegexOptions.IgnoreCase);
    private static readonly Regex MonstersComma = new(@"\s*,\s*");
    private static readonly Regex LayHere = new(@"^(.+?)\s+lay\s+here\.?(\s+|\s*[\.,])$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex TitleLike = new(@"^[A-Za-z0-9' ,\-()]{3,60}$");
    private static readonly Regex StatsLine = new(@"\[Hp=\d+/Mp=\d+/Mv=\d+(?:/At=\d+)?(?:/Ac=\d+)?\]", RegexOptions.Compiled);
    private static readonly Regex MovementCommands = new(@"^\[Hp=.*?\]\s*(n|s|e|w|ne|nw|se|sw|u|d)(?:\s|$)|^\[Hp=.*?\]\s*(north|south|east|west|northeast|northwest|southeast|southwest|up|down)(?:\s|$)", RegexOptions.IgnoreCase);
    private static readonly Regex InvalidRoomNames = new(@"(exits|sorry|no such exit|you peer)", RegexOptions.IgnoreCase);

    private static string? NormalizeDir(string t)
    {
        t = t.ToLowerInvariant();
        return t switch
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
            "north" or "south" or "east" or "west" or "northeast" or "northwest" or "southeast" or "southwest" or "up" or "down" => t,
            _ => null
        };
    }

    public static RoomState? Parse(string screenText)
    {
        var cleaned = Clean(screenText);
        var enhanced = ParseBetweenStatsLines(cleaned);
        if (enhanced != null) return enhanced;

        var block = ExtractRoomBlock(cleaned);
        var name = ExtractRoomName(block);
        var exits = ExtractExits(block);
        var monsters = DetectMonsters(block);
        var items = ExtractItems(block);

        if (string.IsNullOrEmpty(name) && exits.Count == 0) return null;

        return new RoomState
        {
            Name = name ?? string.Empty,
            Exits = exits,
            Monsters = monsters,
            Items = items,
            LastUpdated = DateTime.UtcNow
        };
    }

    private static RoomState? ParseBetweenStatsLines(string text)
    {
        var lines = text
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        string boundary = "You peer 1 room away";
        int peerIdx = lines.FindIndex(l => l.Contains(boundary));
        if (peerIdx >= 0)
        {
            var segment = string.Join('\n', lines.Skip(peerIdx + 1));
            if (HasRoomIndicators(segment))
            {
                var name = ExtractRoomName(segment);
                var exits = ExtractExits(segment);
                if (!string.IsNullOrEmpty(name) || exits.Count > 0)
                {
                    return new RoomState
                    {
                        Name = name ?? string.Empty,
                        Exits = exits,
                        Monsters = DetectMonsters(segment),
                        Items = ExtractItems(segment),
                        LastUpdated = DateTime.UtcNow
                    };
                }
            }
        }

        var statsIdx = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (StatsLine.IsMatch(lines[i]))
            {
                statsIdx.Add(i);
            }
        }

        if (statsIdx.Count < 2) return null;

        for (int i = statsIdx.Count - 1; i > 0; i--)
        {
            var a = statsIdx[i - 1];
            var b = statsIdx[i];
            var between = string.Join('\n', lines.Skip(a + 1).Take(b - a - 1));

            if (HasRoomIndicators(between))
            {
                var name = ExtractRoomName(between);
                var exits = ExtractExits(between);
                if (!string.IsNullOrEmpty(name) || exits.Count > 0)
                {
                    return new RoomState
                    {
                        Name = name ?? string.Empty,
                        Exits = exits,
                        Monsters = DetectMonsters(between),
                        Items = ExtractItems(between),
                        LastUpdated = DateTime.UtcNow
                    };
                }
            }
        }

        return null;
    }

    private static bool HasRoomIndicators(string t)
    {
        return ExitsLine.IsMatch(t)
               || t.Contains("summoned for combat")
               || t.Contains(" enters ")
               || t.Contains(" leaves ")
               || t.Contains(" follows you");
    }

    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;

        s = Regex.Replace(s, @"\x1B\[[0-9;?]*[A-Za-z]", "");
        s = Regex.Replace(s, @"\x1B[\[()][0-9;]*[A-Za-z@]", "");

        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c >= 32 && c <= 126)
            {
                sb.Append(c);
            }
            else if (c == '\n' || c == '\r')
            {
                sb.Append(c);
            }
            else if (c == '\t')
            {
                sb.Append(' ');
            }
        }

        s = Regex.Replace(sb.ToString(), "[ \t]+", " ");
        return s;
    }

    private static string ExtractRoomBlock(string text)
    {
        var lines = text
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        string boundary = "You peer 1 room away";
        int idx = lines.FindIndex(l => l.Contains(boundary));

        List<string> rel;
        if (idx >= 0)
        {
            rel = lines.Skip(idx + 1).ToList();
        }
        else
        {
            int lastCmd = -1;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (StatsLine.IsMatch(lines[i]))
                {
                    lastCmd = i;
                    break;
                }
            }
            rel = lastCmd >= 0 ? lines.Skip(lastCmd + 1).ToList() : lines;
        }

        var exits = rel
            .Select((line, i) => new { line, i })
            .Where(x => ExitsLine.IsMatch(x.line))
            .ToList();

        if (exits.Count > 0)
        {
            var last = exits.Last().i;
            int start = Math.Max(0, last - 10);
            int end = Math.Min(rel.Count - 1, last + 5);
            var blk = rel
                .Skip(start)
                .Take(end - start + 1)
                .Where(l => !l.Contains("[Hp=") && !MovementCommands.IsMatch(l) && l != "Sorry, no such exit exists!")
                .ToList();
            return string.Join('\n', blk);
        }

        var filtered = rel
            .Where(l => !l.Contains("[Hp=") && !MovementCommands.IsMatch(l) && l != "Sorry, no such exit exists!");
        return string.Join('\n', filtered);
    }

    private static string? ExtractRoomName(string block)
    {
        var lines = block
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0) return null;

        int exitIdx = lines.FindIndex(l => ExitsLine.IsMatch(l));
        if (exitIdx > 0)
        {
            for (int j = exitIdx - 1, scanned = 0; j >= 0 && scanned < 5; j--, scanned++)
            {
                var cand = lines[j];
                if (IsMonsterLine(cand) || IsItemLine(cand)) continue;
                if (IsStatsLine(cand)) continue;
                if (cand.StartsWith("[Hp=", StringComparison.OrdinalIgnoreCase)) continue;
                if (cand.StartsWith("Exits:", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsLikelyTitle(cand)) return cand.Trim();
            }
        }

        foreach (var l in lines)
        {
            if (IsMonsterLine(l) || IsItemLine(l)) continue;
            if (IsStatsLine(l)) continue;
            if (l.StartsWith("[Hp=", StringComparison.OrdinalIgnoreCase)) continue;
            if (l.StartsWith("Exits:", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsLikelyTitle(l)) return l.Trim();
        }

        return null;
    }

    private static bool IsMonsterLine(string line)
    {
        if (MonsterLine.IsMatch(line)) return true;
        if (Regex.IsMatch(line, @"\b(?:is|are) here\.?$", RegexOptions.IgnoreCase)) return true;
        return false;
    }

    private static bool IsItemLine(string line)
    {
        if (line.IndexOf(" lay here", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (line.IndexOf(" lays here", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        if (LayHere.IsMatch(line)) return true;
        return false;
    }

    private static bool IsLikelyTitle(string cand)
    {
        if (string.IsNullOrWhiteSpace(cand)) return false;
        if (InvalidRoomNames.IsMatch(cand)) return false;
        if (cand.StartsWith(">") || cand.StartsWith("Enter ", StringComparison.OrdinalIgnoreCase)) return false;
        if (cand.IndexOf(" are here", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (cand.IndexOf(" is here", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (cand.IndexOf(" lay here", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (cand.IndexOf(" lays here", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (cand.IndexOf("Hp=", StringComparison.OrdinalIgnoreCase) >= 0
            || cand.IndexOf("Mp=", StringComparison.OrdinalIgnoreCase) >= 0
            || cand.IndexOf("Mv=", StringComparison.OrdinalIgnoreCase) >= 0
            || cand.IndexOf("At=", StringComparison.OrdinalIgnoreCase) >= 0
            || cand.IndexOf("Ac=", StringComparison.OrdinalIgnoreCase) >= 0
            || Regex.IsMatch(cand, @"\bp=\d+/Mp=\d+/Mv=\d+", RegexOptions.IgnoreCase)) return false;

        var letters = cand.Count(char.IsLetter);
        if (letters < 2) return false;

        return true;
    }

    private static List<string> ExtractExits(string block)
    {
        var m = ExitsLine.Match(block);
        if (!m.Success) return new();

        var raw = m.Groups[1].Value.Trim();
        if (Regex.IsMatch(raw, "^none$", RegexOptions.IgnoreCase)) return new();

        var final = new List<string>();

        if (!raw.Contains(',') && !raw.Contains(" and "))
        {
            var dir = NormalizeDir(raw.Trim().Replace(".", ""));
            if (dir != null) final.Add(dir);
            return final;
        }

        if (raw.Contains(" and ") && !raw.Contains(','))
        {
            foreach (var part in raw.Split(" and ", StringSplitOptions.RemoveEmptyEntries))
            {
                var dir = NormalizeDir(part.Trim().Replace(".", ""));
                if (dir != null) final.Add(dir);
            }
            return final.Distinct().ToList();
        }

        var normalized = raw.Replace(", and ", ", ");
        var parts = normalized.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        for (int i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            if (i == parts.Count - 1 && part.Contains(" and "))
            {
                foreach (var last in part.Split(" and ", StringSplitOptions.RemoveEmptyEntries))
                {
                    var dir = NormalizeDir(last.Trim().Replace(".", ""));
                    if (dir != null) final.Add(dir);
                }
            }
            else
            {
                var dir = NormalizeDir(part);
                if (dir != null) final.Add(dir);
            }
        }

        return final.Distinct().ToList();
    }

    private static List<MonsterInfo> DetectMonsters(string block)
    {
        var result = new List<MonsterInfo>();

        foreach (var raw in block.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.IndexOf(" lay here", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            if (line.IndexOf(" lays here", StringComparison.OrdinalIgnoreCase) >= 0) continue;


            var m = MonsterLine.Match(line);
            if (!m.Success)
            {
                var single = Regex.Match(line, @"^(?:A |An |The )?(.+?)\s+is\s+here\.?$", RegexOptions.IgnoreCase);
                if (single.Success)
                {
                    var nameS = single.Groups[1].Value.Trim();
                    AddMonster(result, nameS, line, false);
                }
                continue;
            }

            var listPart = m.Groups["list"].Value.Trim();
            if (listPart.Length == 0) continue;

            var names = new List<string>();
            if (listPart.Contains(','))
            {
                // Split by comma first
                var commaParts = Regex.Split(listPart, @"\s*,\s*")
                    .Where(p => p.Length > 0)
                    .ToList();

                if (commaParts.Count > 0)
                {
                    // Process all parts except the last one normally
                    for (int i = 0; i < commaParts.Count - 1; i++)
                    {
                        names.Add(commaParts[i].Trim());
                    }

                    // Handle the last part, which might contain " and "
                    var lastPart = commaParts[^1].Trim();
                    if (Regex.IsMatch(lastPart, @"\s+and\s+", RegexOptions.IgnoreCase))
                    {
                        // Split "Y and Z" into separate parts
                        var lastParts = Regex.Split(lastPart, @"\s+and\s+", RegexOptions.IgnoreCase)
                            .Where(p => p.Length > 0)
                            .Select(p => p.Trim());
                        names.AddRange(lastParts);
                    }
                    else
                    {
                        // No " and " in the last part, add it as-is
                        names.Add(lastPart);
                    }
                }
            }
            else if (Regex.IsMatch(listPart, @"\s+and\s+", RegexOptions.IgnoreCase))
            {
                // No commas, just split by " and " (e.g., "X and Y")
                names.AddRange(Regex.Split(listPart, @"\s+and\s+", RegexOptions.IgnoreCase)
                    .Where(p => p.Length > 0)
                    .Select(p => p.Trim()));
            }
            else
            {
                // Single monster
                names.Add(listPart);
            }

            foreach (var n in names)
            {
                var name = n.Trim();
                if (name.Length == 0) continue;
                AddMonster(result, name, line, false);
            }
        }

        return result;
    }

    private static void AddMonster(List<MonsterInfo> list, string rawName, string sourceLine, bool targeting)
    {
        var norm = NormalizeEntity(rawName);
        var count = ParseCountPrefix(rawName);
        var disp = "neutral";

        // Always add the monster - multiple monsters with same name are allowed
        list.Add(new MonsterInfo(norm, disp ?? "neutral", targeting, count));
    }

    private static string NormalizeEntity(string s)
    {
        if (string.IsNullOrEmpty(s)) return "unknown";
        var trimmed = s.Trim();
        return trimmed.Length == 0 ? "unknown" : trimmed;
    }

    private static int? ParseCountPrefix(string s)
    {
        var first = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first == null) return null;

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "one", 1 },
            { "two", 2 },
            { "three", 3 },
            { "four", 4 },
            { "five", 5 }
        };

        if (map.TryGetValue(first, out var v)) return v;
        if (int.TryParse(first, out var iv)) return iv;
        return null;
    }

    private static List<string> ExtractItems(string block)
    {
        var items = new List<string>();
        foreach (Match m in LayHere.Matches(block))
        {
            var list = m.Groups[1].Value.Trim();
            items.AddRange(SplitItems(list));
        }

        var lines = block
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        int exitIdx = lines.FindIndex(l => ExitsLine.IsMatch(l));
        if (exitIdx > 0)
        {
            var prev = lines[exitIdx - 1];
            if (prev.Contains(" lay here") || prev.Contains("lays here"))
            {
                var part = Regex.Replace(prev, @"\s+(lay|lays)\s+here\.?(\s+|$)", "", RegexOptions.IgnoreCase)
                    .Trim();
                if (part.Length > 0)
                {
                    items.AddRange(SplitItems(part));
                }
            }
        }

        return items
            .Select(NormalizeItem)
            .Distinct()
            .ToList();
    }

    private static IEnumerable<string> SplitItems(string list)
    {
        var parts = Regex.Split(list, @",\s*|\s+and\s+", RegexOptions.IgnoreCase);
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.Length > 0)
            {
                yield return t;
            }
        }
    }

    private static string NormalizeItem(string s) => Regex.Replace(s, "^(?i)(a|an|the) ", "").Trim();

    public static bool IsStatsLine(string line) => StatsLine.IsMatch(line);

    public static string DebugParse(string text)
    {
        var cleaned = Clean(text);
        var sb = new StringBuilder();
        sb.AppendLine("=== ROOM PARSER DEBUG ===");

        var lines = cleaned
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        string boundary = "You peer 1 room away";
        int p = lines.FindIndex(l => l.Contains(boundary));
        if (p >= 0)
        {
            sb.AppendLine($"Boundary at {p}: {lines[p]}");
        }

        var stats = lines.Where(l => StatsLine.IsMatch(l)).ToList();
        sb.AppendLine($"Stats lines: {stats.Count}");
        foreach (var l in stats)
        {
            sb.AppendLine($"  > {l}");
        }

        var enhanced = ParseBetweenStatsLines(cleaned);
        if (enhanced != null)
        {
            sb.AppendLine("--- PARSE SUCCESS ---");
            sb.AppendLine($"Name: {enhanced.Name}");
            sb.AppendLine($"Exits: {string.Join(", ", enhanced.Exits)}");
            sb.AppendLine($"Monsters: {enhanced.Monsters.Count}");
            sb.AppendLine($"Items: {enhanced.Items.Count}");
        }
        else
        {
            sb.AppendLine("--- PARSE FAILED ---");
            var block = ExtractRoomBlock(cleaned);
            var name = ExtractRoomName(block);
            var exits = ExtractExits(block);
            sb.AppendLine($"Block: {block.Replace('\n', '|')}");
            sb.AppendLine($"Name: {name}");
            sb.AppendLine($"Exits: {string.Join(", ", exits)}");
        }

        sb.AppendLine("========================");
        return sb.ToString();
    }
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
