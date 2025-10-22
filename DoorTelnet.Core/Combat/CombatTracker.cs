using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DoorTelnet.Core.World;

namespace DoorTelnet.Core.Combat;

/// <summary>
/// Core combat tracking service that monitors damage, deaths, and experience
/// Now refactored to use smaller, focused components for better maintainability
/// </summary>
public class CombatTracker
{
    private readonly object _sync = new();
    private readonly List<CombatEntry> _completedCombats = new();
    private readonly Dictionary<string, ActiveCombat> _activeCombats;
    private readonly List<ActiveCombat> _combatsAwaitingExperience = new();
    private readonly TimeSpan _combatTimeout = TimeSpan.FromMinutes(2);
    private readonly TimeSpan _experienceTimeout = TimeSpan.FromSeconds(30);
    private readonly RoomTracker? _roomTracker;

    // Segmented components
    private readonly CombatTextProcessor _textProcessor = new();
    private readonly CombatLineParser _lineParser;
    private readonly MonsterNameResolver _nameResolver;

    // State tracking
    private int _lastExperienceLeft = -1;
    private int _lastCurrentExperience = -1;
    private bool _hasSeenFirstStats = false;
    private string? _previousCleanLine;

    public event Action<CombatEntry>? CombatCompleted;
    public event Action<ActiveCombat>? CombatStarted;
    public event Action<ActiveCombat>? CombatUpdated;
    public event Action<string>? MonsterDeath;
    public event Action? RequestExperienceCheck;
    public event Action? RequestInitialCommands;
    public event Action<string>? MonsterTargeted;
    public event Action<string>? MonsterBecameAggressive;

    public CombatTracker(RoomTracker? roomTracker = null)
    {
        _roomTracker = roomTracker;
        _lineParser = new CombatLineParser(roomTracker);
        _nameResolver = new MonsterNameResolver(roomTracker);
        
        // Make the active combats dictionary case-insensitive to prevent duplicates
        _activeCombats = new Dictionary<string, ActiveCombat>(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<CombatEntry> CompletedCombats
    {
        get
        {
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(_sync, TimeSpan.FromMilliseconds(50), ref lockTaken);
                if (!lockTaken)
                {
                    // Return empty list if lock is contended to prevent UI freezing
                    System.Diagnostics.Debug.WriteLine("?? COMBAT COMPLETED: Lock timeout - returning empty list to prevent deadlock");
                    return new List<CombatEntry>();
                }
                return _completedCombats.ToList();
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(_sync);
                }
            }
        }
    }

    public IReadOnlyList<ActiveCombat> ActiveCombats
    {
        get
        {
            bool lockTaken = false;
            try
            {
                Monitor.TryEnter(_sync, TimeSpan.FromMilliseconds(50), ref lockTaken);
                if (!lockTaken)
                {
                    // Return empty list if lock is contended to prevent UI freezing
                    System.Diagnostics.Debug.WriteLine("?? COMBAT ACTIVE: Lock timeout - returning empty list to prevent deadlock");
                    return new List<ActiveCombat>();
                }
                return _activeCombats.Values.ToList();
            }
            finally
            {
                if (lockTaken)
                {
                    Monitor.Exit(_sync);
                }
            }
        }
    }

    public bool ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        
        var cleanedLine = _textProcessor.CleanLineContent(line, _hasSeenFirstStats, out var triggersInitialCommands);
        if (triggersInitialCommands)
        {
            _hasSeenFirstStats = true;
            RequestInitialCommands?.Invoke();
        }
        
        if (string.IsNullOrWhiteSpace(cleanedLine)) return false;
        
        bool foundCombatEvent = false;

        // NEW: Detect summoned monsters and ensure they're tracked
        if (cleanedLine.Contains("summoned for combat", StringComparison.OrdinalIgnoreCase))
        {
            var summonMatch = Regex.Match(cleanedLine, @"A\s+([a-zA-Z\s]+?)\s+is\s+summoned\s+for\s+combat", RegexOptions.IgnoreCase);
            if (summonMatch.Success)
            {
                var monsterName = summonMatch.Groups[1].Value.Trim();
                System.Diagnostics.Debug.WriteLine($"?? SUMMONED MONSTER DETECTED: '{monsterName}' - ensuring combat tracking");
                
                // Ensure the monster is tracked and marked as aggressive
                EnsureMonsterTracked(monsterName);
                MarkMonsterAsAggressive(monsterName);
                
                foundCombatEvent = true;
            }
        }

        // Handle "You suffered" damage with previous line context
        if (_previousCleanLine != null && cleanedLine.Contains("You suffered", StringComparison.OrdinalIgnoreCase))
        {
            var dmgMatch = Regex.Match(cleanedLine, @"\d+");
            if (dmgMatch.Success && int.TryParse(dmgMatch.Value, out var dmg))
            {
                string? attacker = _nameResolver.FindMatchingRoomMonster(_previousCleanLine);
                if (!string.IsNullOrWhiteSpace(attacker) && !attacker.Contains("suffered", StringComparison.OrdinalIgnoreCase))
                {
                    RecordMonsterDamage(attacker, dmg);
                    foundCombatEvent = true;
                }
            }
        }
        _previousCleanLine = cleanedLine;

        // Process different types of combat events using the segmented parser
        if (_lineParser.TryParseMeleeTargeting(cleanedLine, out var targetedMonster))
        {
            ProcessMeleeTargeting(targetedMonster);
            foundCombatEvent = true;
        }

        if (_lineParser.TryParsePlayerDamage(cleanedLine, out var playerDamage)) 
        { 
            RecordPlayerDamage(playerDamage.target, playerDamage.damage); 
            foundCombatEvent = true; 
        }
        
        if (_lineParser.TryParseMonsterDamage(cleanedLine, out var monsterDamage)) 
        { 
            RecordMonsterDamage(monsterDamage.monster, monsterDamage.damage); 
            foundCombatEvent = true; 
        }
        
        if (_lineParser.TryParseDeathEvent(cleanedLine, out var deathInfo)) 
        { 
            ProcessMonsterDeath(deathInfo.monsters); 
            foundCombatEvent = true; 
        }
        
        if (_lineParser.TryParseExperience(cleanedLine, out var experience, _lastCurrentExperience, _lastExperienceLeft)) 
        { 
            UpdateExperienceTracking(cleanedLine);
            AssignExperienceToRecentCombats(experience); 
            foundCombatEvent = true; 
        }
        else if (cleanedLine.Contains("[Cur:", StringComparison.OrdinalIgnoreCase))
        {
            // Even if we can't calculate a gain, update our tracking for next time
            UpdateExperienceTracking(cleanedLine);
            System.Diagnostics.Debug.WriteLine($"?? XP LINE PROCESSED (no gain calculated): '{cleanedLine}'");
        }
        
        return foundCombatEvent;
    }

    private void UpdateExperienceTracking(string line)
    {
        var pattern = new Regex(@"\[Cur:\s*(?<current>\d+)\s+Nxt:\s*(?<next>\d+)\s+Left:\s*(?<left>\d+)\]", 
            RegexOptions.IgnoreCase);
        var match = pattern.Match(line);
        if (match.Success)
        {
            if (int.TryParse(match.Groups["current"].Value, out var current))
                _lastCurrentExperience = current;
            if (int.TryParse(match.Groups["left"].Value, out var left))
                _lastExperienceLeft = left;
                
            System.Diagnostics.Debug.WriteLine($"?? XP TRACKING UPDATED: Current={_lastCurrentExperience} Left={_lastExperienceLeft}");
        }
    }

    private void ProcessMeleeTargeting(string targetedMonster)
    {
        UpdateRoomMonsterForTargeting(targetedMonster);

        lock (_sync)
        {
            var resolvedName = _nameResolver.ResolveToRoomMonsterName(targetedMonster);
            ClearAllTargeting();
            SetOrCreateTargetedCombat(resolvedName);
        }

        MonsterTargeted?.Invoke(targetedMonster);
    }

    private void UpdateRoomMonsterForTargeting(string targetedMonster)
    {
        var currentRoom = _roomTracker?.CurrentRoom;
        if (currentRoom?.Monsters == null) return;

        var existingMonster = currentRoom.Monsters.FirstOrDefault(m =>
            _nameResolver.DoesMonsterMatch(m.Name, targetedMonster));

        if (existingMonster != null && !existingMonster.Disposition.Equals("aggressive", StringComparison.OrdinalIgnoreCase))
        {
            var updatedMonsters = currentRoom.Monsters.ToList();
            updatedMonsters.Remove(existingMonster);
            updatedMonsters.Add(new MonsterInfo(existingMonster.Name, "aggressive", true, existingMonster.Count));

            currentRoom.Monsters.Clear();
            currentRoom.Monsters.AddRange(updatedMonsters);
            currentRoom.LastUpdated = DateTime.UtcNow;
        }
        else if (existingMonster == null)
        {
            currentRoom.Monsters.Add(new MonsterInfo(targetedMonster, "aggressive", true, null));
            currentRoom.LastUpdated = DateTime.UtcNow;
        }
    }

    private void ClearAllTargeting()
    {
        foreach (var combat in _activeCombats.Values)
        {
            combat.IsTargeted = false;
            combat.TargetedTime = null;
        }
        
        // Also clear targeting from combats awaiting experience
        foreach (var combat in _combatsAwaitingExperience)
        {
            combat.IsTargeted = false;
            combat.TargetedTime = null;
        }
    }

    private void SetOrCreateTargetedCombat(string resolvedName)
    {
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
            Task.Run(() => CombatStarted?.Invoke(targetedCombat));
        }
        else
        {
            targetedCombat.IsTargeted = true;
            targetedCombat.TargetedTime = DateTime.UtcNow;
            Task.Run(() => CombatUpdated?.Invoke(targetedCombat));
        }
    }

    private void RecordPlayerDamage(string target, int damage)
    {
        ActiveCombat? combat = null;
        bool isNewCombat = false;

        lock (_sync)
        {
            var resolvedTarget = _nameResolver.ResolveToRoomMonsterName(target);

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

        FireCombatEvents(combat, isNewCombat);
    }

    private void RecordMonsterDamage(string monster, int damage)
    {
        ActiveCombat? combat = null;
        bool isNewCombat = false;
        string resolvedMonster;

        lock (_sync)
        {
            resolvedMonster = _nameResolver.ResolveToRoomMonsterName(monster);

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

        MarkMonsterAsAggressive(resolvedMonster);
        FireCombatEvents(combat, isNewCombat);
    }

    private void FireCombatEvents(ActiveCombat combat, bool isNewCombat)
    {
        if (isNewCombat)
            CombatStarted?.Invoke(combat);
        else
            CombatUpdated?.Invoke(combat);
    }

    private void MarkMonsterAsAggressive(string monsterName)
    {
        if (_roomTracker?.UpdateMonsterDisposition(monsterName, "aggressive") == true)
        {
            System.Diagnostics.Debug.WriteLine($"?? Marked monster '{monsterName}' as aggressive");
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

    private void ProcessMonsterDeath(List<string> monsterNames)
    {
        lock (_sync)
        {
            foreach (var monsterName in monsterNames)
            {
                if (TryFindActiveMonster(monsterName, out var combat, out var key))
                {
                    ProcessSingleMonsterDeath(combat, key);
                }
            }
        }
    }

    private bool TryFindActiveMonster(string monsterName, out ActiveCombat? combat, out string? key)
    {
        combat = null;
        key = null;

        // Try exact match
        if (_activeCombats.TryGetValue(monsterName, out combat))
        {
            key = monsterName;
            return true;
        }

        // Try resolved name
        var resolvedName = _nameResolver.ResolveToRoomMonsterName(monsterName);
        if (resolvedName != monsterName && _activeCombats.TryGetValue(resolvedName, out combat))
        {
            key = resolvedName;
            return true;
        }

        // Try partial matching
        var partialMatch = _activeCombats.Keys.FirstOrDefault(k => 
            _nameResolver.DoesMonsterMatch(k, monsterName) || 
            _nameResolver.DoesMonsterMatch(monsterName, k));

        if (partialMatch != null)
        {
            combat = _activeCombats[partialMatch];
            key = partialMatch;
            return true;
        }

        return false;
    }

    private void ProcessSingleMonsterDeath(ActiveCombat combat, string monsterKey)
    {
        combat.DeathTime = DateTime.UtcNow;
        
        // Immediately complete the combat when monster dies - don't wait for XP
        var completedEntry = combat.Complete("Victory", 0); // XP will be assigned later if available
        
        // Add to completed combats immediately
        _completedCombats.Add(completedEntry);
        
        // Store targeting info for auto-attack (separate from XP awaiting)
        if (combat.IsTargeted)
        {
            // Create a copy with targeting info for auto-attack purposes
            var targetingInfo = new ActiveCombat
            {
                MonsterName = combat.MonsterName,
                StartTime = combat.StartTime,
                DamageDealt = combat.DamageDealt,
                DamageTaken = combat.DamageTaken,
                LastDamageTime = combat.LastDamageTime,
                DeathTime = combat.DeathTime,
                IsTargeted = combat.IsTargeted,
                TargetedTime = combat.TargetedTime,
                AwaitingExperience = false // Not awaiting XP anymore
            };
            _combatsAwaitingExperience.Add(targetingInfo);
        }
        
        // Remove from active combats immediately
        _activeCombats.Remove(monsterKey);
        
        // Debug: Log immediate completion
        System.Diagnostics.Debug.WriteLine($"?? COMBAT COMPLETED: '{combat.MonsterName}' died, combat completed immediately. IsTargeted={combat.IsTargeted} preserved for auto-attack");

        // Fire completion event immediately
        CombatCompleted?.Invoke(completedEntry);
        MonsterDeath?.Invoke($"Combat with '{combat.MonsterName}' ended");
        
        // XP checking is separate and optional
        RequestExperienceCheck?.Invoke();
    }

    private void AssignExperienceToRecentCombats(int experience)
    {
        lock (_sync)
        {
            // Look for recently completed combats that haven't been assigned XP yet
            // Order by END TIME (when combat actually finished) to get the most recent death, not the most recent start
            var recentCompletedCombats = _completedCombats
                .Where(c => c.ExperienceGained == 0 && // No XP assigned yet
                           c.EndTime.HasValue &&
                           (DateTime.UtcNow - c.EndTime.Value) <= _experienceTimeout)
                .OrderByDescending(c => c.EndTime) // Most recent death FIRST, not last
                .ToList();

            if (recentCompletedCombats.Count > 0)
            {
                var targetCombat = recentCompletedCombats.First(); // Take the FIRST (most recent death)
                
                // Assign XP to the most recent combat death
                targetCombat.ExperienceGained = experience;
                
                System.Diagnostics.Debug.WriteLine($"?? XP ASSIGNED: {experience} XP assigned to completed combat '{targetCombat.MonsterName}' (combat ended {(DateTime.UtcNow - (targetCombat.EndTime ?? DateTime.UtcNow)).TotalSeconds:F1}s ago)");
                
                // Fire completion event again to trigger UI update with the XP value
                CombatCompleted?.Invoke(targetCombat);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"?? XP GAINED (no recent combat): {experience} XP - likely from login, quest, or other source");
                
                // Log the recent combats for debugging
                var allRecent = _completedCombats
                    .Where(c => c.EndTime.HasValue && (DateTime.UtcNow - c.EndTime.Value) <= TimeSpan.FromMinutes(2))
                    .OrderByDescending(c => c.EndTime)
                    .Take(5)
                    .ToList();
                    
                foreach (var combat in allRecent)
                {
                    var timeSinceEnd = combat.EndTime.HasValue ? (DateTime.UtcNow - combat.EndTime.Value).TotalSeconds : -1;
                    System.Diagnostics.Debug.WriteLine($"   Recent combat: '{combat.MonsterName}' ended {timeSinceEnd:F1}s ago, XP={combat.ExperienceGained}");
                }
            }
            
            // Clean up old targeting entries that are no longer needed
            var expiredTargetingEntries = _combatsAwaitingExperience
                .Where(c => c.DeathTime.HasValue && (DateTime.UtcNow - c.DeathTime.Value) > TimeSpan.FromMinutes(2))
                .ToList();
                
            foreach (var expired in expiredTargetingEntries)
            {
                _combatsAwaitingExperience.Remove(expired);
                System.Diagnostics.Debug.WriteLine($"?? TARGETING EXPIRED: Removed expired targeting for '{expired.MonsterName}'");
            }
        }
    }

    // Public interface methods
    public void NotifyMonsterDeath(List<string> monsterNames, string deathLine) => ProcessMonsterDeath(monsterNames);

    /// <summary>
    /// Manually assign experience to the most recent combat that hasn't received XP yet.
    /// This should be called when XP is gained through XP commands or other means.
    /// </summary>
    public bool TryAssignExperienceToRecentCombat(int experience)
    {
        lock (_sync)
        {
            var recentCombat = _completedCombats
                .Where(c => c.ExperienceGained == 0 && // No XP assigned yet
                           c.EndTime.HasValue &&
                           (DateTime.UtcNow - c.EndTime.Value) <= _experienceTimeout)
                .OrderByDescending(c => c.EndTime)
                .FirstOrDefault();

            if (recentCombat != null)
            {
                recentCombat.ExperienceGained = experience;
                System.Diagnostics.Debug.WriteLine($"?? MANUAL XP ASSIGNMENT: {experience} XP assigned to '{recentCombat.MonsterName}'");
                return true;
            }

            System.Diagnostics.Debug.WriteLine($"?? MANUAL XP ASSIGNMENT FAILED: No recent combat found for {experience} XP");
            return false;
        }
    }

    public ActiveCombat? GetTargetedMonster()
    {
        lock (_sync)
        {
            // First check active combats
            var targetedCombat = _activeCombats.Values.FirstOrDefault(c => c.IsTargeted);
            
            // If no active targeted combat found, check combats awaiting experience
            // This allows auto-attack to continue with newly summoned monsters of the same type
            if (targetedCombat == null)
            {
                targetedCombat = _combatsAwaitingExperience.FirstOrDefault(c => c.IsTargeted);
                
                // Debug: Log when we find targeting in awaiting experience
                if (targetedCombat != null)
                {
                    System.Diagnostics.Debug.WriteLine($"?? TARGETING FOUND in awaiting experience: '{targetedCombat.MonsterName}' (dead for {(DateTime.UtcNow - (targetedCombat.DeathTime ?? DateTime.UtcNow)).TotalSeconds:F1}s)");
                }
                
                // Check if the dead monster's targeting has expired (2 minutes after death)
                if (targetedCombat != null && targetedCombat.DeathTime.HasValue && 
                    (DateTime.UtcNow - targetedCombat.DeathTime.Value) > TimeSpan.FromMinutes(2))
                {
                    // Clear expired targeting
                    System.Diagnostics.Debug.WriteLine($"?? TARGETING EXPIRED for dead monster: '{targetedCombat.MonsterName}' (dead for {(DateTime.UtcNow - targetedCombat.DeathTime.Value).TotalMinutes:F1} minutes)");
                    targetedCombat.IsTargeted = false;
                    targetedCombat.TargetedTime = null;
                    targetedCombat = null;
                }
            }
            else
            {
                // Debug: Log when we find targeting in active combats
                System.Diagnostics.Debug.WriteLine($"?? TARGETING FOUND in active combats: '{targetedCombat.MonsterName}'");
            }
            
            if (targetedCombat == null) 
            {
                System.Diagnostics.Debug.WriteLine("?? NO TARGETED MONSTER found");
                return null;
            }

            // Return a copy to avoid thread safety issues
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

    public CombatStatistics GetStatistics()
    {
        // Use a non-blocking approach to prevent UI thread deadlocks
        // If we can't get the lock quickly, return cached/default stats
        bool lockTaken = false;
        try
        {
            Monitor.TryEnter(_sync, TimeSpan.FromMilliseconds(100), ref lockTaken);
            if (!lockTaken)
            {
                // Return empty stats if lock is contended to prevent UI freezing
                System.Diagnostics.Debug.WriteLine("?? COMBAT STATS: Lock timeout - returning empty stats to prevent deadlock");
                return new CombatStatistics();
            }

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
        finally
        {
            if (lockTaken)
            {
                Monitor.Exit(_sync);
            }
        }
    }

    public void ClearHistory()
    {
        lock (_sync)
        {
            _completedCombats.Clear();
            _lastExperienceLeft = -1;
            _lastCurrentExperience = -1;
        }
    }

    public void ResetFirstStatsTracking() => _hasSeenFirstStats = false;

    public void CleanupStaleCombats()
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            
            // Clean up stale active combats (shouldn't happen often with immediate completion)
            var staleCombats = _activeCombats.Values.Where(c => c.IsStale(_combatTimeout)).ToList();
            foreach (var staleCombat in staleCombats)
            {
                var key = _activeCombats.FirstOrDefault(kvp => kvp.Value == staleCombat).Key;
                if (key != null)
                {
                    var completedEntry = staleCombat.Complete("Timeout");
                    _completedCombats.Add(completedEntry);
                    _activeCombats.Remove(key);
                    CombatCompleted?.Invoke(completedEntry);
                    System.Diagnostics.Debug.WriteLine($"?? STALE COMBAT: Timed out combat '{staleCombat.MonsterName}'");
                }
            }

            // Clean up old targeting entries (these are just for auto-attack, not XP)
            var staleTargetingEntries = _combatsAwaitingExperience
                .Where(c => c.DeathTime.HasValue && (now - c.DeathTime.Value) > TimeSpan.FromMinutes(2))
                .ToList();
                
            foreach (var staleEntry in staleTargetingEntries)
            {
                _combatsAwaitingExperience.Remove(staleEntry);
                System.Diagnostics.Debug.WriteLine($"?? STALE TARGETING: Removed stale targeting for '{staleEntry.MonsterName}'");
            }
        }
    }

    public string GetActiveCombatsDebugInfo()
    {
        lock (_sync)
        {
            var sb = new System.Text.StringBuilder();
            
            if (_activeCombats.Count > 0)
            {
                sb.AppendLine($"Active Combats ({_activeCombats.Count}):");
                foreach (var kvp in _activeCombats)
                {
                    var combat = kvp.Value;
                    sb.AppendLine($"  '{kvp.Key}': Dealt={combat.DamageDealt}, Taken={combat.DamageTaken}, " +
                                 $"Duration={combat.DurationSeconds:F1}s, Targeted={combat.IsTargeted}");
                }
            }

            if (_combatsAwaitingExperience.Count > 0)
            {
                sb.AppendLine($"Targeting Info for Auto-Attack ({_combatsAwaitingExperience.Count}):");
                foreach (var combat in _combatsAwaitingExperience)
                {
                    var timeSinceDeath = combat.DeathTime.HasValue ? 
                        $"{(DateTime.UtcNow - combat.DeathTime.Value).TotalSeconds:F1}s ago" : "unknown";
                    sb.AppendLine($"  '{combat.MonsterName}': Dead {timeSinceDeath}, Targeted={combat.IsTargeted}");
                }
            }
            
            var recentCompletedCount = _completedCombats.Count(c => 
                c.EndTime.HasValue && (DateTime.UtcNow - c.EndTime.Value).TotalMinutes < 5);
            sb.AppendLine($"Recent Completed Combats (last 5 min): {recentCompletedCount}");

            if (_activeCombats.Count == 0 && _combatsAwaitingExperience.Count == 0)
            {
                return "No active combats or targeting info";
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// Create combat tracking for a summoned monster to ensure auto-attack can target it
    /// This is called when monsters are summoned but haven't engaged in combat yet
    /// </summary>
    public void EnsureMonsterTracked(string monsterName)
    {
        lock (_sync)
        {
            var resolvedName = _nameResolver.ResolveToRoomMonsterName(monsterName);
            
            // Only create if not already tracked
            if (!_activeCombats.ContainsKey(resolvedName))
            {
                var combat = new ActiveCombat
                {
                    MonsterName = resolvedName,
                    StartTime = DateTime.UtcNow,
                    LastDamageTime = DateTime.UtcNow,
                    IsTargeted = false,
                    TargetedTime = null
                };
                _activeCombats[resolvedName] = combat;
                
                System.Diagnostics.Debug.WriteLine($"?? MONSTER TRACKED: '{resolvedName}' added for auto-attack targeting");
                
                Task.Run(() => CombatStarted?.Invoke(combat));
            }
        }
    }

    public void MarkCombatEnded(string monsterName) => ProcessMonsterDeath(new List<string> { monsterName });
}