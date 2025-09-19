using System.Text.RegularExpressions;

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
    
    // Combat detection regex patterns
    private static readonly Regex PlayerDamagePattern = new(
        @"^You (?<action>attack|strike|hit|slash|pierce|crush|bash|pummel|stab|slice|smash|whack|pound|thump|thwack) (?<target>.+?) (?:and (?<result>do .+? damage|inflict .+? wounds?|cause .+? pain|deal .+? damage))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    private static readonly Regex MonsterDamagePattern = new(
        @"^(?:A |An |The )?(?<monster>.+?) (?<action>attacks?|strikes?|hits?|slashes?|pierces?|crushes?|bashes?|pummels?|stabs?|slices?|smashes?|whacks?|pounds?|thumps?|thwacks?) you (?:and (?<result>does? .+? damage|inflicts? .+? wounds?|causes? .+? pain|deals? .+? damage))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    private static readonly Regex AreaDamagePattern = new(
        @"^You suffer (?<damage>\d+) damage(?:\s+from (?<source>.+?))?!?\.?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    private static readonly Regex DamageNumberPattern = new(
        @"(?<amount>\d+)\s+(?:point(?:s)?\s+of\s+)?damage",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // Experience detection regex patterns
    private static readonly Regex ExperiencePattern = new(
        @"\[Cur:\s*(?<current>\d+)\s+Nxt:\s*(?<next>\d+)\s+Left:\s*(?<left>\d+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
       
    // Death detection patterns - using the same logic as RoomModels
    private static readonly Regex DeathKeywordPattern = new(
        @"\b(?:dies|died|dead|death|earth)\b\.?!?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    public event Action<CombatEntry>? CombatCompleted;
    public event Action<ActiveCombat>? CombatStarted;
    public event Action<ActiveCombat>? CombatUpdated;
    public event Action<string>? MonsterDeath; // For logging purposes
    
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
    /// Process a line of game text to detect combat events
    /// </summary>
    /// <param name="line">The line of text to process</param>
    /// <returns>True if the line contained combat-related information</returns>
    public bool ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;
        
        var trimmedLine = line.Trim();
        bool foundCombatEvent = false;
        
        // Try to detect player damage first
        if (TryParsePlayerDamage(trimmedLine, out var playerDamage))
        {
            RecordPlayerDamage(playerDamage.target, playerDamage.damage);
            foundCombatEvent = true;
        }
        
        // Try to detect monster damage
        if (TryParseMonsterDamage(trimmedLine, out var monsterDamage))
        {
            RecordMonsterDamage(monsterDamage.monster, monsterDamage.damage);
            foundCombatEvent = true;
        }
        
        // Try to detect area damage
        if (TryParseAreaDamage(trimmedLine, out var areaDamage))
        {
            RecordAreaDamage(areaDamage.source, areaDamage.damage);
            foundCombatEvent = true;
        }
        
        // Try to detect death events
        if (TryParseDeathEvent(trimmedLine, out var deathInfo))
        {
            ProcessMonsterDeath(deathInfo.monsters);
            foundCombatEvent = true;
        }
        
        // Try to detect experience messages
        if (TryParseExperience(trimmedLine, out var experience))
        {
            AssignExperienceToRecentCombats(experience);
            foundCombatEvent = true;
        }
        
        return foundCombatEvent;
    }
    
    /// <summary>
    /// Try to parse player damage from a line
    /// </summary>
    private bool TryParsePlayerDamage(string line, out (string target, int damage) result)
    {
        result = default;
        
        var match = PlayerDamagePattern.Match(line);
        if (!match.Success)
            return false;
        
        var target = match.Groups["target"].Value.Trim();
        var damageResult = match.Groups["result"].Value;
        
        var damage = ExtractDamageAmount(damageResult);
              
        result = (target, damage);
        return true;
    }
    
    /// <summary>
    /// Try to parse monster damage from a line
    /// </summary>
    private bool TryParseMonsterDamage(string line, out (string monster, int damage) result)
    {
        result = default;
        
        var match = MonsterDamagePattern.Match(line);
        if (!match.Success)
            return false;
        
        var monster = match.Groups["monster"].Value.Trim();
        var damageResult = match.Groups["result"].Value;
        
        var damage = ExtractDamageAmount(damageResult);
        
        result = (monster, damage);
        return true;
    }
    
    /// <summary>
    /// Try to parse area damage from a line
    /// </summary>
    private bool TryParseAreaDamage(string line, out (string source, int damage) result)
    {
        result = default;
        
        var match = AreaDamagePattern.Match(line);
        if (!match.Success)
            return false;
        
        var damageStr = match.Groups["damage"].Value;
        var source = match.Groups["source"].Success ? match.Groups["source"].Value.Trim() : "area effect";
        
        if (int.TryParse(damageStr, out var damage))
        {
            result = (source, damage);
            return true;
        }
        
        return false;
    }
    
    /// <summary>
    /// Try to parse death events from a line using the same logic as RoomModels
    /// </summary>
    private bool TryParseDeathEvent(string line, out (List<string> monsters, string deathLine) result)
    {
        result = default;
        
        // Use the same logic as RoomModels.IsDeathLine
        if (!IsDeathLine(line))
            return false;
        
        // Find monster names that appear in the death line
        var monsterNames = ExtractMonsterNamesFromLine(line);
        
        if (monsterNames.Count == 0)
            return false;
        
        result = (monsterNames, line);
        return true;
    }
    
    /// <summary>
    /// Death detection using the same logic as RoomModels.IsDeathLine
    /// </summary>
    private bool IsDeathLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) 
            return false;
        
        var trimmed = line.TrimEnd();
        
        // Accept ending punctuation . or ! (possibly both forms repeated) but optional
        // Extract last token (letters only) ignoring trailing punctuation
        int i = trimmed.Length - 1;
        while (i >= 0 && (trimmed[i] == '!' || trimmed[i] == '.' || trimmed[i] == ' ')) 
            i--;
        
        int end = i;
        if (end < 0) 
            return false;
        
        while (i >= 0 && char.IsLetter(trimmed[i])) 
            i--;
        
        var lastWord = trimmed.Substring(i + 1, end - i).ToLowerInvariant();
        return lastWord is "banished" or "cracks" or "darkness" or "dead" or "death" or "defeated" or "dies" or "disappears" or "earth" or "exhausted" or "existance" or "existence" or "flames" or "goddess" or "gone" or "ground" or "himself" or "killed" or "lifeless" or "mana" or "manaless" or "nothingness" or "over" or "pieces" or "portal" or "scattered" or "silent" or "slain" or "still" or "vortex";
    }
    
    /// <summary>
    /// Extract monster names from a death line by looking for active combat monsters
    /// </summary>
    private List<string> ExtractMonsterNamesFromLine(string line)
    {
        var foundMonsters = new List<string>();
        
        lock (_sync)
        {
            // Check if any active combat monster names appear in the death line
            foreach (var combat in _activeCombats.Values)
            {
                var monsterName = combat.MonsterName;
                
                // Remove common prefixes for matching
                var baseName = RemoveArticles(monsterName);
                
                // Check if the base name appears in the death line
                if (line.IndexOf(baseName, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    foundMonsters.Add(monsterName);
                }
            }
        }
        
        return foundMonsters;
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
                }
            }
        }
    }
    
    /// <summary>
    /// Try to parse experience information from a line
    /// </summary>
    private bool TryParseExperience(string line, out int experience)
    {
        experience = 0;
        
        // Try the main experience format: [Cur: X Nxt: Y Left: Z]
        var expMatch = ExperiencePattern.Match(line);
        if (expMatch.Success)
        {
            // Calculate experience gained from the "Left" field
            // This represents experience needed to next level
            if (int.TryParse(expMatch.Groups["left"].Value, out var left))
            {
                // For now, we'll use a simple heuristic to estimate experience gained
                // This could be improved by tracking previous "left" values
                experience = Math.Max(1, 100 - left); // Simple estimation
                return true;
            }
        } 
        return false;
    }
    
    /// <summary>
    /// Extract numeric damage amount from damage description
    /// </summary>
    private int ExtractDamageAmount(string damageText)
    {
        var match = DamageNumberPattern.Match(damageText);
        if (match.Success && int.TryParse(match.Groups["amount"].Value, out var amount))
        {
            return amount;
        }
        
        return 0;
    }
    
    /// <summary>
    /// Record damage dealt by the player to a target
    /// </summary>
    private void RecordPlayerDamage(string target, int damage)
    {
        lock (_sync)
        {
            // Normalize target name (remove articles, clean up)
            var normalizedTarget = NormalizeMonsterName(target);
            
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
            // Normalize monster name
            var normalizedMonster = NormalizeMonsterName(monster);
            
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
    /// Record area damage (damage from non-specific sources)
    /// </summary>
    private void RecordAreaDamage(string source, int damage)
    {
        lock (_sync)
        {
            // For area damage, we might not have a specific active combat
            // For now, we'll create a special entry for environmental damage
            var normalizedSource = NormalizeMonsterName(source);
            
            if (!_activeCombats.TryGetValue(normalizedSource, out var combat))
            {
                combat = new ActiveCombat
                {
                    MonsterName = normalizedSource,
                    StartTime = DateTime.UtcNow,
                    LastDamageTime = DateTime.UtcNow
                };
                _activeCombats[normalizedSource] = combat;
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
        }
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