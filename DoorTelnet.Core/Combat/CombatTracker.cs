using System.Text.RegularExpressions;
using DoorTelnet.Core.World;

namespace DoorTelnet.Core.Combat;

/// <summary>
/// Represents a single combat encounter with a monster
/// </summary>
public class CombatEntry
{
    public string MonsterName { get; set; } = string.Empty;
    public int DamageDealt { get; set; }
    public int DamageTaken { get; set; }
    public int ExperienceGained { get; set; }
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public DateTime? EndTime { get; set; }
    public bool IsCompleted { get; set; }
    public string Status { get; set; } = "Active"; // Active, Victory, Fled, Death
    
    /// <summary>
    /// Duration of the combat in seconds
    /// </summary>
    public double DurationSeconds 
    { 
        get 
        { 
            var end = EndTime ?? DateTime.UtcNow;
            return (end - StartTime).TotalSeconds; 
        } 
    }
    
    /// <summary>
    /// Damage per second dealt to the monster
    /// </summary>
    public double DpsDealt
    {
        get
        {
            var duration = DurationSeconds;
            return duration > 0 ? DamageDealt / duration : 0;
        }
    }
    
    /// <summary>
    /// Damage per second taken from the monster
    /// </summary>
    public double DpsTaken
    {
        get
        {
            var duration = DurationSeconds;
            return duration > 0 ? DamageTaken / duration : 0;
        }
    }
    
    /// <summary>
    /// Experience per second gained
    /// </summary>
    public double ExpPerSecond
    {
        get
        {
            var duration = DurationSeconds;
            return duration > 0 ? ExperienceGained / duration : 0;
        }
    }
}

/// <summary>
/// Tracks an ongoing combat encounter
/// </summary>
public class ActiveCombat
{
    public string MonsterName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public int DamageDealt { get; set; }
    public int DamageTaken { get; set; }
    public DateTime LastDamageTime { get; set; } = DateTime.UtcNow;
    public bool AwaitingExperience { get; set; }
    public DateTime? DeathTime { get; set; }
    
    /// <summary>
    /// Duration of the combat in seconds
    /// </summary>
    public double DurationSeconds 
    { 
        get 
        { 
            var end = DeathTime ?? DateTime.UtcNow;
            return (end - StartTime).TotalSeconds; 
        } 
    }
    
    /// <summary>
    /// Marks the combat as complete and returns a CombatEntry
    /// </summary>
    public CombatEntry Complete(string status, int experienceGained = 0)
    {
        var endTime = DeathTime ?? DateTime.UtcNow;
        
        return new CombatEntry
        {
            MonsterName = MonsterName,
            DamageDealt = DamageDealt,
            DamageTaken = DamageTaken,
            ExperienceGained = experienceGained,
            StartTime = StartTime,
            EndTime = endTime,
            IsCompleted = true,
            Status = status
        };
    }
    
    /// <summary>
    /// Check if this combat should be considered stale and auto-completed
    /// </summary>
    public bool IsStale(TimeSpan timeout)
    {
        return DateTime.UtcNow - LastDamageTime > timeout;
    }
}

/// <summary>
/// Core combat tracking service that monitors damage, deaths, and experience
/// </summary>
public class CombatTracker
{
    private readonly object _sync = new();
    private readonly List<CombatEntry> _completedCombats = new();
    private readonly Dictionary<string, ActiveCombat> _activeCombats = new();
    private readonly List<ActiveCombat> _combatsAwaitingExperience = new();
    private readonly TimeSpan _combatTimeout = TimeSpan.FromMinutes(2); // Auto-complete stale combats
    private readonly TimeSpan _experienceTimeout = TimeSpan.FromSeconds(30); // Time to wait for experience after death
    private readonly RoomTracker? _roomTracker; // Optional dependency for room monster matching
    
    // Track experience for calculating gains
    private int _lastExperienceLeft = -1;
    private int _lastCurrentExperience = -1;
    
    // Track if we've seen first stats line to trigger initial commands
    private bool _hasSeenFirstStats = false;
    
    // Simple regex patterns for combat detection
    private static readonly Regex NumberRegex = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex ExperiencePattern = new(
        @"\[Cur:\s*(?<current>\d+)\s+Nxt:\s*(?<next>\d+)\s+Left:\s*(?<left>\d+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // Enhanced stats pattern to detect stats lines that need to be stripped
    private static readonly Regex StatsPattern = new(
        @"\[Hp=\d+/Mp=\d+/Mv=\d+(?:/At=\d+)?(?:/Ac=\d+)?\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // Death detection patterns - using the same logic as RoomModels
    private static readonly string[] DeathWords = 
    {
        "banished", "cracks", "darkness", "dead", "death", "defeated", "dies", "disappears", 
        "earth", "exhausted", "existance", "existence", "flames", "goddess", "gone", "ground", 
        "himself", "killed", "lifeless", "mana", "manaless", "nothingness", "over", "pieces", 
        "portal", "scattered", "silent", "slain", "still", "vortex"
    };
    
    public event Action<CombatEntry>? CombatCompleted;
    public event Action<ActiveCombat>? CombatStarted;
    public event Action<ActiveCombat>? CombatUpdated;
    public event Action<string>? MonsterDeath; // For logging purposes
    public event Action? RequestExperienceCheck; // Request XP command after death
    public event Action? RequestInitialCommands; // Request initial commands when joining game
    
    /// <summary>
    /// Initialize combat tracker with optional room tracker for enhanced monster identification
    /// </summary>
    public CombatTracker(RoomTracker? roomTracker = null)
    {
        _roomTracker = roomTracker;
    }
    
    /// <summary>
    /// Get all completed combat entries
    /// </summary>
    public IReadOnlyList<CombatEntry> CompletedCombats
    {
        get
        {
            lock (_sync)
            {
                return _completedCombats.ToList();
            }
        }
    }
    
    /// <summary>
    /// Get all currently active combats
    /// </summary>
    public IReadOnlyList<ActiveCombat> ActiveCombats
    {
        get
        {
            lock (_sync)
            {
                return _activeCombats.Values.ToList();
            }
        }
    }
    
    /// <summary>
    /// Clean line content and remove all stats blocks while preserving XP lines
    /// </summary>
    private string CleanLineContent(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;
        
        // Remove ANSI escape sequences
        var cleaned = Regex.Replace(line, @"\x1B\[[0-9;]*[a-zA-Z]", "");
        
        // Check if this is the first stats line we've seen (trigger initial commands)
        if (!_hasSeenFirstStats && StatsPattern.IsMatch(cleaned))
        {
            _hasSeenFirstStats = true;
            RequestInitialCommands?.Invoke();
        }
        
        // Remove all stats blocks but preserve XP lines
        if (!ExperiencePattern.IsMatch(cleaned))
        {
            cleaned = StatsPattern.Replace(cleaned, "").Trim();
        }
        
        // Strip leading partial stats (like the room tracker does)
        cleaned = StripLeadingPartialStats(cleaned);
        
        // Remove extra whitespace
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        
        return cleaned;
    }
    
    /// <summary>
    /// Strip leading partial statistics that interfere with parsing (like [C from xp command)
    /// </summary>
    private string StripLeadingPartialStats(string line)
    {
        if (string.IsNullOrEmpty(line))
            return line;
        
        // Remove leading partial brackets like "[C" that interfere with parsing
        if (line.StartsWith("[") && line.Length > 1 && char.IsLetter(line[1]))
        {
            // Don't strip if it looks like an XP line
            if (line.Contains("Cur:") || line.Contains("Nxt:") || line.Contains("Left:"))
                return line;
            
            // Find the end of the partial bracket or use the whole line if malformed
            int endBracket = line.IndexOf(']', 1);
            if (endBracket > 0)
            {
                // Check if this looks like a partial stat line
                var bracketContent = line.Substring(0, endBracket + 1);
                if (bracketContent.Length < 10) // Partial brackets are usually short
                {
                    line = line.Substring(endBracket + 1).TrimStart();
                }
            }
            else
            {
                // No closing bracket found, likely partial - remove the opening part
                var spaceIndex = line.IndexOf(' ');
                if (spaceIndex > 0 && spaceIndex < 5) // Only if it's a short partial
                {
                    line = line.Substring(spaceIndex + 1).TrimStart();
                }
            }
        }
        
        return line;
    }
    
    /// <summary>
    /// Process a line of game text to detect combat events
    /// </summary>
    /// <param name="line">The line of text to process</param>
    /// <returns>True if the line contained combat-related information</returns>
    public bool ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        
        // Clean the line first
        var cleanedLine = CleanLineContent(line);
        if (string.IsNullOrWhiteSpace(cleanedLine))
            return false;
        
        bool foundCombatEvent = false;
        
        // Try to detect player damage (You ... damage)
        if (TryParsePlayerDamage(cleanedLine, out var playerDamage))
        {
            RecordPlayerDamage(playerDamage.target, playerDamage.damage);
            foundCombatEvent = true;
        }
        
        // Try to detect monster damage (A/An/The ... you ... damage)
        if (TryParseMonsterDamage(cleanedLine, out var monsterDamage))
        {
            RecordMonsterDamage(monsterDamage.monster, monsterDamage.damage);
            foundCombatEvent = true;
        }
        
        // Try to detect death events
        if (TryParseDeathEvent(cleanedLine, out var deathInfo))
        {
            ProcessMonsterDeath(deathInfo.monsters);
            foundCombatEvent = true;
        }
        
        // Try to detect experience messages
        if (TryParseExperience(cleanedLine, out var experience))
        {
            AssignExperienceToRecentCombats(experience);
            foundCombatEvent = true;
        }
        
        return foundCombatEvent;
    }
    
    /// <summary>
    /// Find the first room monster that matches the damage line
    /// </summary>
    private string? FindMatchingRoomMonster(string line)
    {
        var currentRoom = _roomTracker?.CurrentRoom;
        if (currentRoom?.Monsters == null || currentRoom.Monsters.Count == 0)
            return null;
        
        // Check monsters in the room
        foreach (var monster in currentRoom.Monsters)
        {
            var monsterName = monster.Name?.Replace(" (summoned)", "") ?? "";
            if (string.IsNullOrWhiteSpace(monsterName))
                continue;
            
            // Check exact match first
            if (line.Contains(monsterName, StringComparison.OrdinalIgnoreCase))
                return monsterName;
            
            // Check without articles
            var withoutArticles = RemoveArticles(monsterName);
            if (line.Contains(withoutArticles, StringComparison.OrdinalIgnoreCase))
                return monsterName;
            
            // Check individual words in the monster name (for partial matches)
            var words = monsterName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in words)
            {
                if (word.Length >= 3 && line.Contains(word, StringComparison.OrdinalIgnoreCase))
                    return monsterName;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Simple player damage detection: "You ... damage" with room monster matching
    /// </summary>
    private bool TryParsePlayerDamage(string line, out (string target, int damage) result)
    {
        result = default;

        // Remove any trailing ! or . from line, might be more than one.
        line = line.Replace("!", "").Replace(".", "");

        // Simple check: starts with "You" and ends with "damage"
        if (!line.StartsWith("You ", StringComparison.OrdinalIgnoreCase) || 
            !line.EndsWith("damage", StringComparison.OrdinalIgnoreCase))
            return false;
        
        // Extract all numbers from the line
        var matches = NumberRegex.Matches(line);
        var numbers = matches.Cast<Match>().Select(m => int.Parse(m.Value)).ToList();
        
        if (numbers.Count == 0)
            return false;
        
        // Take the largest number as damage (usually the most significant)
        var damage = numbers.Max();
        
        // First try to find a matching monster from the current room
        var roomMonster = FindMatchingRoomMonster(line);
        if (roomMonster != null)
        {
            result = (roomMonster, damage);
            return true;
        }
        
        // Fallback: Extract target name using the old method
        var targetPart = line.Substring(4); // Remove "You "
        var damageIndex = targetPart.LastIndexOf("damage", StringComparison.OrdinalIgnoreCase);
        if (damageIndex <= 0)
            return false;
        
        var targetSection = targetPart.Substring(0, damageIndex).Trim();
        
        // Remove common action words to get the target
        var actionWords = new[] { "attack", "strike", "hit", "slash", "pierce", "crush", "bash", 
                                "pummel", "stab", "slice", "smash", "whack", "pound", "thump", 
                                "thwack", "and", "do", "deal", "inflict", "cause", "for" };
        
        var words = targetSection.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var targetWords = new List<string>();
        
        foreach (var word in words)
        {
            var cleanWord = word.TrimEnd('.', ',', '!', '?');
            if (!actionWords.Contains(cleanWord.ToLowerInvariant()) && !NumberRegex.IsMatch(cleanWord))
            {
                targetWords.Add(cleanWord);
            }
        }
        
        if (targetWords.Count == 0)
            return false;
        
        var target = string.Join(" ", targetWords);
        
        result = (target, damage);
        return true;
    }
    
    /// <summary>
    /// Simple monster damage detection: "A/An/The ... you ... damage" with room monster matching
    /// </summary>
    private bool TryParseMonsterDamage(string line, out (string monster, int damage) result)
    {
        result = default;

        // Remove any trailing ! or . from line, might be more than one.
        line = line.Replace("!", "").Replace(".", "");

        // Simple check: starts with A/An/The and contains "you" and ends with "damage"
        var startsCorrectly = line.StartsWith("A ", StringComparison.OrdinalIgnoreCase) ||
                             line.StartsWith("An ", StringComparison.OrdinalIgnoreCase) ||
                             line.StartsWith("The ", StringComparison.OrdinalIgnoreCase);
        
        if (!startsCorrectly || 
            !line.Contains(" you ", StringComparison.OrdinalIgnoreCase) ||
            !line.EndsWith("damage", StringComparison.OrdinalIgnoreCase))
            return false;
        
        // Extract all numbers from the line
        var matches = NumberRegex.Matches(line);
        var numbers = matches.Cast<Match>().Select(m => int.Parse(m.Value)).ToList();
        
        if (numbers.Count == 0)
            return false;
        
        // Take the largest number as damage
        var damage = numbers.Max();
        
        // First try to find a matching monster from the current room
        var roomMonster = FindMatchingRoomMonster(line);
        if (roomMonster != null)
        {
            result = (roomMonster, damage);
            return true;
        }
        
        // Fallback: Extract monster name using the old method
        var youIndex = line.IndexOf(" you ", StringComparison.OrdinalIgnoreCase);
        if (youIndex <= 0)
            return false;
        
        var monsterSection = line.Substring(0, youIndex).Trim();
        
        // Remove action words to get monster name
        var actionWords = new[] { "attacks", "attack", "strikes", "strike", "hits", "hit", 
                                "slashes", "slash", "pierces", "pierce", "crushes", "crush", 
                                "bashes", "bash", "pummels", "pummel", "stabs", "stab", 
                                "slices", "slice", "smashes", "smash", "whacks", "whack", 
                                "pounds", "pound", "thumps", "thump", "thwacks", "thwack", "and" };
        
        var words = monsterSection.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var monsterWords = new List<string>();
        
        foreach (var word in words)
        {
            var cleanWord = word.TrimEnd('.', ',', '!', '?');
            if (!actionWords.Contains(cleanWord.ToLowerInvariant()) && !NumberRegex.IsMatch(cleanWord))
            {
                monsterWords.Add(cleanWord);
            }
        }
        
        if (monsterWords.Count == 0)
            return false;
        
        var monster = string.Join(" ", monsterWords);
        
        result = (monster, damage);
        return true;
    }
    
    /// <summary>
    /// Death detection using simplified logic - check if line ends with death word
    /// </summary>
    private bool TryParseDeathEvent(string line, out (List<string> monsters, string deathLine) result)
    {
        result = default;
        
        if (string.IsNullOrWhiteSpace(line))
            return false;
        
        // Remove trailing punctuation and get last word
        var trimmed = line.TrimEnd('.', '!', '?', ' ');
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        
        if (words.Length == 0)
            return false;
        
        var firstWord = words[0].ToLowerInvariant();
        var lastWord = words[^1].ToLowerInvariant();

        // Check if it's a death word and starts with "the" or "a/an "
        if (!DeathWords.Contains(lastWord))
            return false;
        if (!(firstWord == "the" || firstWord == "a" || firstWord == "an"))
            return false;

        // First try to find monsters from current room that match the death line
        var foundMonsters = new List<string>();
        
        if (_roomTracker?.CurrentRoom?.Monsters != null)
        {
            foreach (var roomMonster in _roomTracker.CurrentRoom.Monsters)
            {
                var monsterName = roomMonster.Name?.Replace(" (summoned)", "") ?? "";
                if (string.IsNullOrWhiteSpace(monsterName))
                    continue;
                
                // Check if monster name appears in death line
                if (line.Contains(monsterName, StringComparison.OrdinalIgnoreCase) ||
                    line.Contains(RemoveArticles(monsterName), StringComparison.OrdinalIgnoreCase))
                {
                    foundMonsters.Add(monsterName);
                }
            }
        }
        
        // Fallback: check active combats
        if (foundMonsters.Count == 0)
        {
            lock (_sync)
            {
                foreach (var combat in _activeCombats.Values)
                {
                    var monsterName = combat.MonsterName;
                    var baseName = RemoveArticles(monsterName);
                    
                    if (line.Contains(baseName, StringComparison.OrdinalIgnoreCase))
                    {
                        foundMonsters.Add(monsterName);
                    }
                }
            }
        }
        
        if (foundMonsters.Count == 0)
            return false;
        
        result = (foundMonsters, line);
        return true;
    }
    
    /// <summary>
    /// Parse experience and track the difference to calculate actual gains
    /// </summary>
    private bool TryParseExperience(string line, out int experienceGained)
    {
        experienceGained = 0;
        
        var match = ExperiencePattern.Match(line);
        if (!match.Success)
            return false;
        
        if (int.TryParse(match.Groups["current"].Value, out var current) &&
            int.TryParse(match.Groups["left"].Value, out var left))
        {
            // If we have previous experience data, calculate the actual gain
            if (_lastCurrentExperience >= 0 && _lastExperienceLeft >= 0)
            {
                // Experience gained = difference in current experience
                experienceGained = current - _lastCurrentExperience;
                
                // Sanity check - if gain seems too large or negative, use left field difference
                if (experienceGained <= 0 || experienceGained > 10000)
                {
                    // Alternative calculation using "left" field
                    experienceGained = _lastExperienceLeft - left;
                }
            }
            
            // Update our tracking values
            _lastCurrentExperience = current;
            _lastExperienceLeft = left;
            
            // If we couldn't calculate a gain, return false but still update tracking
            return experienceGained > 0;
        }
        
        return false;
    }
    
    /// <summary>
    /// Remove articles and common prefixes from monster names for matching
    /// </summary>
    private string RemoveArticles(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;
        
        var prefixes = new[] { "a ", "an ", "the ", "some " };
        foreach (var prefix in prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return name.Substring(prefix.Length);
            }
        }
        
        return name;
    }
    
    /// <summary>
    /// Process monster deaths and move combats to awaiting experience
    /// </summary>
    private void ProcessMonsterDeath(List<string> monsterNames)
    {
        lock (_sync)
        {
            foreach (var monsterName in monsterNames)
            {
                if (_activeCombats.TryGetValue(monsterName, out var combat))
                {
                    combat.DeathTime = DateTime.UtcNow;
                    combat.AwaitingExperience = true;
                    
                    // Move to awaiting experience list
                    _combatsAwaitingExperience.Add(combat);
                    _activeCombats.Remove(monsterName);
                    
                    // Fire event for logging
                    MonsterDeath?.Invoke($"Combat with '{monsterName}' ended - awaiting experience (dealt: {combat.DamageDealt}, taken: {combat.DamageTaken}, duration: {combat.DurationSeconds:F1}s)");
                    
                    // Request XP check after monster death
                    RequestExperienceCheck?.Invoke();
                }
            }
        }
    }
    
    /// <summary>
    /// Record damage dealt by the player to a target
    /// </summary>
    private void RecordPlayerDamage(string target, int damage)
    {
        lock (_sync)
        {
            // Use the target as-is if it came from room matching, otherwise normalize
            var normalizedTarget = target;
            var isRoomEntity = false;
            
            if (_roomTracker?.CurrentRoom?.Monsters != null)
            {
                // Check if target matches any monster in the room
                isRoomEntity = _roomTracker.CurrentRoom.Monsters.Any(m => 
                    m.Name?.Replace(" (summoned)", "").Equals(target, StringComparison.OrdinalIgnoreCase) == true);
            }
            
            if (!isRoomEntity)
            {
                normalizedTarget = NormalizeMonsterName(target);
            }
            
            if (!_activeCombats.TryGetValue(normalizedTarget, out var combat))
            {
                combat = new ActiveCombat
                {
                    MonsterName = normalizedTarget,
                    StartTime = DateTime.UtcNow,
                    LastDamageTime = DateTime.UtcNow
                };
                _activeCombats[normalizedTarget] = combat;
                CombatStarted?.Invoke(combat);
            }
            
            combat.DamageDealt += damage;
            combat.LastDamageTime = DateTime.UtcNow;
            CombatUpdated?.Invoke(combat);
        }
    }
    
    /// <summary>
    /// Record damage taken from a monster
    /// </summary>
    private void RecordMonsterDamage(string monster, int damage)
    {
        lock (_sync)
        {
            // Use the monster name as-is if it came from room matching, otherwise normalize
            var normalizedMonster = monster;
            var isRoomEntity = false;
            
            if (_roomTracker?.CurrentRoom?.Monsters != null)
            {
                // Check if monster matches any monster in the room
                isRoomEntity = _roomTracker.CurrentRoom.Monsters.Any(m => 
                    m.Name?.Replace(" (summoned)", "").Equals(monster, StringComparison.OrdinalIgnoreCase) == true);
            }
            
            if (!isRoomEntity)
            {
                normalizedMonster = NormalizeMonsterName(monster);
            }
            
            if (!_activeCombats.TryGetValue(normalizedMonster, out var combat))
            {
                combat = new ActiveCombat
                {
                    MonsterName = normalizedMonster,
                    StartTime = DateTime.UtcNow,
                    LastDamageTime = DateTime.UtcNow
                };
                _activeCombats[normalizedMonster] = combat;
                CombatStarted?.Invoke(combat);
            }
            
            combat.DamageTaken += damage;
            combat.LastDamageTime = DateTime.UtcNow;
            CombatUpdated?.Invoke(combat);
        }
    }
    
    /// <summary>
    /// Assign experience to recent combats that are awaiting experience
    /// </summary>
    private void AssignExperienceToRecentCombats(int experience)
    {
        lock (_sync)
        {
            // Find combats that are awaiting experience and are recent enough
            var now = DateTime.UtcNow;
            var eligibleCombats = _combatsAwaitingExperience
                .Where(c => c.AwaitingExperience && 
                           c.DeathTime.HasValue && 
                           (now - c.DeathTime.Value) <= _experienceTimeout)
                .OrderBy(c => c.DeathTime)
                .ToList();
            
            if (eligibleCombats.Count == 0)
                return;
            
            // Assign experience to the most recent combat
            var targetCombat = eligibleCombats.Last();
            var completedEntry = targetCombat.Complete("Victory", experience);
            
            _completedCombats.Add(completedEntry);
            _combatsAwaitingExperience.Remove(targetCombat);
            
            CombatCompleted?.Invoke(completedEntry);
        }
    }
    
    /// <summary>
    /// Normalize monster names for consistent tracking
    /// </summary>
    private string NormalizeMonsterName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "unknown";
        
        var normalized = name.Trim();
        
        // Remove common articles and prefixes
        var prefixes = new[] { "a ", "an ", "the ", "some " };
        foreach (var prefix in prefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(prefix.Length);
                break;
            }
        }
        
        // Remove trailing punctuation
        normalized = normalized.TrimEnd('.', '!', '?', ',');
        
        // Convert to title case for consistency
        if (normalized.Length > 0)
        {
            normalized = char.ToUpperInvariant(normalized[0]) + 
                        (normalized.Length > 1 ? normalized.Substring(1).ToLowerInvariant() : "");
        }
        
        return normalized;
    }
    
    /// <summary>
    /// Get combat statistics summary
    /// </summary>
    public CombatStatistics GetStatistics()
    {
        lock (_sync)
        {
            var completed = _completedCombats.Where(c => c.IsCompleted).ToList();
            
            return new CombatStatistics
            {
                TotalCombats = completed.Count,
                TotalExperience = completed.Sum(c => c.ExperienceGained),
                TotalDamageDealt = completed.Sum(c => c.DamageDealt),
                TotalDamageTaken = completed.Sum(c => c.DamageTaken),
                AverageDamageDealt = completed.Count > 0 ? completed.Average(c => c.DamageDealt) : 0,
                AverageDamageTaken = completed.Count > 0 ? completed.Average(c => c.DamageTaken) : 0,
                AverageExperience = completed.Count > 0 ? completed.Average(c => c.ExperienceGained) : 0,
                AverageDuration = completed.Count > 0 ? completed.Average(c => c.DurationSeconds) : 0,
                Victories = completed.Count(c => c.Status == "Victory"),
                Deaths = completed.Count(c => c.Status == "Death"),
                Flees = completed.Count(c => c.Status == "Fled")
            };
        }
    }
    
    /// <summary>
    /// Clear all combat history
    /// </summary>
    public void ClearHistory()
    {
        lock (_sync)
        {
            _completedCombats.Clear();
            _activeCombats.Clear();
            _combatsAwaitingExperience.Clear();
            _lastExperienceLeft = -1;
            _lastCurrentExperience = -1;
        }
    }
    
    /// <summary>
    /// Reset the first stats tracking (for reconnections)
    /// </summary>
    public void ResetFirstStatsTracking()
    {
        _hasSeenFirstStats = false;
    }
    
    /// <summary>
    /// Clean up stale combats that have timed out
    /// </summary>
    public void CleanupStaleCombats()
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            
            // Clean up stale active combats
            var staleCombats = _activeCombats.Values.Where(c => c.IsStale(_combatTimeout)).ToList();
            foreach (var combat in staleCombats)
            {
                var entry = combat.Complete("Timeout");
                _completedCombats.Add(entry);
                _activeCombats.Remove(combat.MonsterName);
                CombatCompleted?.Invoke(entry);
            }
            
            // Clean up combats that have been waiting too long for experience
            var staleAwaitingExp = _combatsAwaitingExperience
                .Where(c => c.DeathTime.HasValue && (now - c.DeathTime.Value) > _experienceTimeout)
                .ToList();
            
            foreach (var combat in staleAwaitingExp)
            {
                var entry = combat.Complete("Victory", 0); // No experience gained
                _completedCombats.Add(entry);
                _combatsAwaitingExperience.Remove(combat);
                CombatCompleted?.Invoke(entry);
            }
        }
    }
    
    /// <summary>
    /// Mark a combat as ended when a monster dies (public method for external integration)
    /// </summary>
    public void MarkCombatEnded(string monsterName)
    {
        ProcessMonsterDeath(new List<string> { monsterName });
    }
}

/// <summary>
/// Combat statistics summary
/// </summary>
public class CombatStatistics
{
    public int TotalCombats { get; set; }
    public int TotalExperience { get; set; }
    public int TotalDamageDealt { get; set; }
    public int TotalDamageTaken { get; set; }
    public double AverageDamageDealt { get; set; }
    public double AverageDamageTaken { get; set; }
    public double AverageExperience { get; set; }
    public double AverageDuration { get; set; }
    public int Victories { get; set; }
    public int Deaths { get; set; }
    public int Flees { get; set; }
    
    public double WinRate => TotalCombats > 0 ? (double)Victories / TotalCombats : 0;
    public double DeathRate => TotalCombats > 0 ? (double)Deaths / TotalCombats : 0;
    public double FleeRate => TotalCombats > 0 ? (double)Flees / TotalCombats : 0;
}