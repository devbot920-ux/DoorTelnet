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
    private readonly Dictionary<string, ActiveCombat> _activeCombats = new();
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
    }

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
        combat.AwaitingExperience = true;

        if (combat.IsTargeted)
        {
            combat.IsTargeted = false;
            combat.TargetedTime = null;
        }

        _combatsAwaitingExperience.Add(combat);
        _activeCombats.Remove(monsterKey);

        MonsterDeath?.Invoke($"Combat with '{combat.MonsterName}' ended");
        RequestExperienceCheck?.Invoke();
    }

    private void AssignExperienceToRecentCombats(int experience)
    {
        lock (_sync)
        {
            var eligibleCombats = _combatsAwaitingExperience
                .Where(c => c.AwaitingExperience && 
                           c.DeathTime.HasValue && 
                           (DateTime.UtcNow - c.DeathTime.Value) <= _experienceTimeout)
                .OrderBy(c => c.DeathTime)
                .ToList();

            if (eligibleCombats.Count > 0)
            {
                var targetCombat = eligibleCombats.Last();
                var completedEntry = targetCombat.Complete("Victory", experience);

                _completedCombats.Add(completedEntry);
                _combatsAwaitingExperience.Remove(targetCombat);
                CombatCompleted?.Invoke(completedEntry);
            }
        }
    }

    // Public interface methods
    public void NotifyMonsterDeath(List<string> monsterNames, string deathLine) => ProcessMonsterDeath(monsterNames);

    public ActiveCombat? GetTargetedMonster()
    {
        lock (_sync)
        {
            var targetedCombat = _activeCombats.Values.FirstOrDefault(c => c.IsTargeted);
            if (targetedCombat == null) return null;

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
                }
            }

            var staleExperienceEntries = _combatsAwaitingExperience
                .Where(c => c.DeathTime.HasValue && (now - c.DeathTime.Value) > _experienceTimeout)
                .ToList();
                
            foreach (var staleEntry in staleExperienceEntries)
            {
                var completedEntry = staleEntry.Complete("Victory", 0);
                _completedCombats.Add(completedEntry);
                _combatsAwaitingExperience.Remove(staleEntry);
                CombatCompleted?.Invoke(completedEntry);
            }
        }
    }

    public string GetActiveCombatsDebugInfo()
    {
        lock (_sync)
        {
            if (_activeCombats.Count == 0) return "No active combats";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Active Combats ({_activeCombats.Count}):");

            foreach (var kvp in _activeCombats)
            {
                var combat = kvp.Value;
                sb.AppendLine($"  '{kvp.Key}': Dealt={combat.DamageDealt}, Taken={combat.DamageTaken}, " +
                             $"Duration={combat.DurationSeconds:F1}s, Targeted={combat.IsTargeted}");
            }

            if (_combatsAwaitingExperience.Count > 0)
            {
                sb.AppendLine($"Awaiting Experience ({_combatsAwaitingExperience.Count}):");
                foreach (var combat in _combatsAwaitingExperience)
                {
                    sb.AppendLine($"  '{combat.MonsterName}': Dealt={combat.DamageDealt}");
                }
            }

            return sb.ToString();
        }
    }

    public void MarkCombatEnded(string monsterName) => ProcessMonsterDeath(new List<string> { monsterName });
}