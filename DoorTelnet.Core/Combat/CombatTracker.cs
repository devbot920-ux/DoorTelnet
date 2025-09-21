using System.Text.RegularExpressions;
using DoorTelnet.Core.World;
using System.Linq;

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
    /// Indicates if this monster is currently targeted for melee combat
    /// </summary>
    public bool IsTargeted { get; set; }
    
    /// <summary>
    /// When this monster was targeted for melee combat
    /// </summary>
    public DateTime? TargetedTime { get; set; }
    
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
    
    // Melee targeting pattern - matches "You circle the challenger and prepare to attack!"
    private static readonly Regex MeleeTargetingPattern = new(
        @"You circle the (.+?) and prepare to attack!",
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
    public event Action<string>? MonsterTargeted; // Fired when a monster is targeted for melee combat
    public event Action<string>? MonsterBecameAggressive; // Fired when a monster becomes aggressive due to damaging player
    
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
        if (string.IsNullOrWhiteSpace(line)) return false;
        var cleanedLine = CleanLineContent(line);
        if (string.IsNullOrWhiteSpace(cleanedLine)) return false;
        bool foundCombatEvent = false;

        if (_previousCleanLine != null && cleanedLine.Contains("You suffered", StringComparison.OrdinalIgnoreCase))
        {
            var dmgMatch = NumberRegex.Match(cleanedLine);
            if (dmgMatch.Success && int.TryParse(dmgMatch.Value, out var dmg))
            {
                string? attacker = FindMatchingRoomMonster(_previousCleanLine);
                if (!string.IsNullOrWhiteSpace(attacker) && !attacker.Contains("suffered", StringComparison.OrdinalIgnoreCase))
                {
                    RecordMonsterDamage(attacker, dmg);
                    foundCombatEvent = true;
                    _previousCleanLine = cleanedLine;
                    return foundCombatEvent; 

                }
            }
        }
        _previousCleanLine = cleanedLine;

        // Check for melee targeting first (happens when player initiates attack)
        if (TryParseMeleeTargeting(cleanedLine, out var targetedMonster))
        {
            // TryParseMeleeTargeting now handles everything - no need for additional calls
            foundCombatEvent = true;
        }

        if (TryParsePlayerDamage(cleanedLine, out var playerDamage)) { RecordPlayerDamage(playerDamage.target, playerDamage.damage); foundCombatEvent = true; }
        if (TryParseMonsterDamage(cleanedLine, out var monsterDamage)) { RecordMonsterDamage(monsterDamage.monster, monsterDamage.damage); foundCombatEvent = true; }
        if (TryParseDeathEvent(cleanedLine, out var deathInfo)) { ProcessMonsterDeath(deathInfo.monsters); foundCombatEvent = true; }
        if (TryParseExperience(cleanedLine, out var experience)) { AssignExperienceToRecentCombats(experience); foundCombatEvent = true; }
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
        
        // Check monsters in the room - prioritize exact matches first
        var candidates = new List<(string monsterName, int priority)>();
        
        foreach (var monster in currentRoom.Monsters)
        {
            var monsterName = monster.Name?.Replace(" (summoned)", "") ?? "";
            if (string.IsNullOrWhiteSpace(monsterName))
                continue;
            
            // Priority 1: Exact match (highest priority)
            if (line.Contains(monsterName, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add((monsterName, 1));
                continue;
            }
            
            // Priority 2: Match without articles
            var withoutArticles = RemoveArticles(monsterName);
            if (line.Contains(withoutArticles, StringComparison.OrdinalIgnoreCase))
            {
                candidates.Add((monsterName, 2));
                continue;
            }
            
            // Priority 3: Match individual words (but only for multi-word monster names to avoid false positives)
            if (monsterName.Contains(' '))
            {
                var words = monsterName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool allWordsMatch = true;
                
                // Check if ALL significant words from the monster name appear in the line
                foreach (var word in words)
                {
                    if (word.Length >= 3 && !line.Contains(word, StringComparison.OrdinalIgnoreCase))
                    {
                        allWordsMatch = false;
                        break;
                    }
                }
                
                if (allWordsMatch && words.Length > 1) // Only if it's a multi-word name
                {
                    candidates.Add((monsterName, 3));
                }
            }
        }
        
        // Return the highest priority match
        return candidates.OrderBy(c => c.priority).FirstOrDefault().monsterName;
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

        // Check if it's a death word and starts with "the" or "a/an ";
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
                
                // Sanity check - if gain seems to large or negative, use left field difference
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
                    
                    // Clear targeting if this was the targeted monster
                    if (combat.IsTargeted)
                    {
                        combat.IsTargeted = false;
                        combat.TargetedTime = null;
                    }
                    
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
        ActiveCombat? combat = null;
        bool isNewCombat = false;
        
        lock (_sync)
        {
            // Always resolve to exact room monster name for consistent tracking
            var resolvedTarget = ResolveToRoomMonsterName(target);
            
            if (!_activeCombats.TryGetValue(resolvedTarget, out combat))
            {
                combat = new ActiveCombat
                {
                    MonsterName = resolvedTarget,
                    StartTime = DateTime.UtcNow,
                    LastDamageTime = DateTime.UtcNow
                };
                _activeCombats[resolvedTarget] = combat;
                isNewCombat = true;
            }
            
            combat.DamageDealt += damage;
            combat.LastDamageTime = DateTime.UtcNow;
        }
        
        // Fire events outside the lock
        if (isNewCombat && combat != null)
        {
            CombatStarted?.Invoke(combat);
        }
        else if (combat != null)
        {
            CombatUpdated?.Invoke(combat);
        }
    }
    
    /// <summary>
    /// Record damage taken from a monster and mark the monster as aggressive
    /// </summary>
    private void RecordMonsterDamage(string monster, int damage)
    {
        ActiveCombat? combat = null;
        bool isNewCombat = false;
        string resolvedMonster;
        
        lock (_sync)
        {
            // Always resolve to exact room monster name for consistent tracking
            resolvedMonster = ResolveToRoomMonsterName(monster);
            
            if (!_activeCombats.TryGetValue(resolvedMonster, out combat))
            {
                combat = new ActiveCombat
                {
                    MonsterName = resolvedMonster,
                    StartTime = DateTime.UtcNow,
                    LastDamageTime = DateTime.UtcNow
                };
                _activeCombats[resolvedMonster] = combat;
                isNewCombat = true;
            }
            
            combat.DamageTaken += damage;
            combat.LastDamageTime = DateTime.UtcNow;
        }
        
        // Mark the monster as aggressive in the current room since it attacked us
        MarkMonsterAsAggressive(resolvedMonster);
        
        // Fire events outside the lock
        if (isNewCombat && combat != null)
        {
            CombatStarted?.Invoke(combat);
        }
        else if (combat != null)
        {
            CombatUpdated?.Invoke(combat);
        }
    }
    
    /// <summary>
    /// Mark a monster as aggressive in the current room
    /// </summary>
    private void MarkMonsterAsAggressive(string monsterName)
    {
        if (_roomTracker == null)
            return;

        // Use the room tracker's public method to update monster disposition
        var updated = _roomTracker.UpdateMonsterDisposition(monsterName, "aggressive");
        
        if (updated)
        {
            // Log the change for debugging
            System.Diagnostics.Debug.WriteLine($"?? Marked monster '{monsterName}' as aggressive due to damage dealt to player");
            
            // Fire event to notify about the disposition change
            try
            {
                MonsterBecameAggressive?.Invoke(monsterName);
            }
            catch
            {
                // Ignore event handler errors
            }
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
    
    /// <summary>
    /// Find and return the exact room monster name that matches any given monster reference
    /// This ensures all combat tracking uses consistent room-based monster names
    /// </summary>
    private string ResolveToRoomMonsterName(string monsterReference)
    {
        var currentRoom = _roomTracker?.CurrentRoom;
        if (currentRoom?.Monsters == null || currentRoom.Monsters.Count == 0)
            return NormalizeMonsterName(monsterReference);
        
        // Check monsters in the room using comprehensive matching
        foreach (var monster in currentRoom.Monsters)
        {
            var monsterName = monster.Name?.Replace(" (summoned)", "") ?? "";
            if (string.IsNullOrWhiteSpace(monsterName))
                continue;
            
            // Check if this room monster matches our monster reference
            if (DoesMonsterMatch(monsterName, monsterReference))
                return monsterName;
        }
        
        // No room monster found, normalize the reference
        return NormalizeMonsterName(monsterReference);
    }
    
    /// <summary>
    /// Check if a room monster matches a given monster reference using comprehensive logic
    /// Enhanced to better handle multi-word monster names with spaces
    /// </summary>
    private bool DoesMonsterMatch(string roomMonsterName, string monsterReference)
    {
        if (string.IsNullOrWhiteSpace(roomMonsterName) || string.IsNullOrWhiteSpace(monsterReference))
            return false;
        
        // Priority 1: Exact match (case insensitive)
        if (roomMonsterName.Equals(monsterReference, StringComparison.OrdinalIgnoreCase))
            return true;
        
        // Priority 2: Match without articles
        var roomNameNoArticles = RemoveArticles(roomMonsterName);
        var refNoArticles = RemoveArticles(monsterReference);
        
        if (roomNameNoArticles.Equals(refNoArticles, StringComparison.OrdinalIgnoreCase))
            return true;
        
        // Priority 3: Check if the reference is contained in the room monster name
        if (roomMonsterName.Contains(monsterReference, StringComparison.OrdinalIgnoreCase) ||
            roomNameNoArticles.Contains(refNoArticles, StringComparison.OrdinalIgnoreCase))
            return true;
        
        // Priority 4: Check if the room monster name is contained in the reference
        if (monsterReference.Contains(roomMonsterName, StringComparison.OrdinalIgnoreCase) ||
            refNoArticles.Contains(roomNameNoArticles, StringComparison.OrdinalIgnoreCase))
            return true;
        
        // Priority 5: For multi-word names, check if ALL significant words from room monster appear in reference
        if (roomMonsterName.Contains(' ') || monsterReference.Contains(' '))
        {
            var roomWords = roomNameNoArticles.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3) // Only significant words
                .ToList();
            
            var refWords = refNoArticles.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 3) // Only significant words
                .ToList();
            
            // Check if all room monster words appear in the reference (for targeting commands like "a fire")
            if (roomWords.Count > 0 && roomWords.All(roomWord => 
                refWords.Any(refWord => refWord.Contains(roomWord, StringComparison.OrdinalIgnoreCase) ||
                                       roomWord.Contains(refWord, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
            
            // Check if all reference words appear in the room monster name (for damage lines)
            if (refWords.Count > 0 && refWords.All(refWord => 
                roomWords.Any(roomWord => roomWord.Contains(refWord, StringComparison.OrdinalIgnoreCase) ||
                                         refWord.Contains(roomWord, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }
        
        return false;
    }
    
    private string? _previousCleanLine; // previous cleaned line

    /// <summary>
    /// Parse melee targeting line - "You circle the challenger and prepare to attack!"
    /// This directly handles the targeting and room updates in one place.
    /// </summary>
    private bool TryParseMeleeTargeting(string line, out string targetedMonster)
    {
        targetedMonster = string.Empty;
        
        var match = MeleeTargetingPattern.Match(line);
        if (!match.Success)
            return false;
        
        // Extract and normalize the monster name from the targeting message
        var parsedMonsterName = match.Groups[1].Value.Trim();
        targetedMonster = NormalizeMonsterName(parsedMonsterName);
        
        // Ensure monster exists in room and is marked as aggressive (since we attacked it)
        var currentRoom = _roomTracker?.CurrentRoom;
        if (currentRoom != null)
        {
            var monsterNameForMatching = targetedMonster; // Store in local variable for lambda
            var existingMonster = currentRoom.Monsters.FirstOrDefault(m => 
                DoesMonsterMatch(m.Name, monsterNameForMatching));
            
            if (existingMonster != null)
            {
                // Monster exists - update it to be aggressive if it isn't already
                if (!existingMonster.Disposition.Equals("aggressive", StringComparison.OrdinalIgnoreCase))
                {
                    var updatedMonsters = currentRoom.Monsters.ToList();
                    updatedMonsters.Remove(existingMonster);
                    updatedMonsters.Add(new MonsterInfo(
                        existingMonster.Name, 
                        "aggressive", 
                        true, // TargetingYou = true since we attacked it
                        existingMonster.Count));
                    
                    currentRoom.Monsters.Clear();
                    currentRoom.Monsters.AddRange(updatedMonsters);
                    currentRoom.LastUpdated = DateTime.UtcNow;
                }
            }
            else
            {
                // Monster doesn't exist - add it as aggressive and targeting us
                currentRoom.Monsters.Add(new MonsterInfo(targetedMonster, "aggressive", true, null));
                currentRoom.LastUpdated = DateTime.UtcNow;
            }
        }
        
        // Set this monster as targeted in combat tracking
        lock (_sync)
        {
            var resolvedName = ResolveToRoomMonsterName(targetedMonster);
            
            // Clear targeting from all other monsters
            foreach (var combat in _activeCombats.Values)
            {
                combat.IsTargeted = false;
                combat.TargetedTime = null;
            }
            
            // Set or create the targeted combat
            if (!_activeCombats.TryGetValue(resolvedName, out var targetedCombat))
            {
                targetedCombat = new ActiveCombat
                {
                    MonsterName = resolvedName,
                    StartTime = DateTime.UtcNow,
                    LastDamageTime = DateTime.UtcNow,
                    IsTargeted = true,
                    TargetedTime = DateTime.UtcNow
                };
                _activeCombats[resolvedName] = targetedCombat;
                
                // Fire event outside the lock
                Task.Run(() => CombatStarted?.Invoke(targetedCombat));
            }
            else
            {
                targetedCombat.IsTargeted = true;
                targetedCombat.TargetedTime = DateTime.UtcNow;
                
                // Fire event outside the lock
                Task.Run(() => CombatUpdated?.Invoke(targetedCombat));
            }
        }
        
        // Fire targeting event
        MonsterTargeted?.Invoke(targetedMonster);
        
        return !string.IsNullOrWhiteSpace(targetedMonster);
    }

    /// <summary>
    /// Get the currently targeted monster (if any) - thread safe without holding lock during external operations
    /// </summary>
    public ActiveCombat? GetTargetedMonster()
    {
        lock (_sync)
        {
            var targetedCombat = _activeCombats.Values.FirstOrDefault(c => c.IsTargeted);
            if (targetedCombat == null) return null;
            
            // Return a copy to avoid exposing the original object outside the lock
            return new ActiveCombat
            {
                MonsterName = targetedCombat.MonsterName,
                StartTime = targetedCombat.StartTime,
                DamageDealt = targetedCombat.DamageDealt,
                DamageTaken = targetedCombat.DamageTaken,
                LastDamageTime = targetedCombat.LastDamageTime,
                AwaitingExperience = targetedCombat.AwaitingExperience,
                DeathTime = targetedCombat.DeathTime,
                IsTargeted = targetedCombat.IsTargeted,
                TargetedTime = targetedCombat.TargetedTime
            };
        }
    }
    
    /// <summary>
    /// Test method to demonstrate the monster aggression marking functionality
    /// Can be called from CLI or debugging to test the feature
    /// </summary>
    public void TestMonsterAggressionMarking()
    {
        // Simulate some monster damage lines that would trigger the marking
        var testLines = new[]
        {
            "The goblin strikes you for 15 damage",
            "An orc warrior hits you for 23 damage", 
            "A fire elemental attacks you for 8 damage"
        };

        foreach (var line in testLines)
        {
            var success = ProcessLine(line);
            if (success)
            {
                System.Diagnostics.Debug.WriteLine($"Processed damage line: {line}");
            }
        }
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