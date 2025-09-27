using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DoorTelnet.Core.World;

namespace DoorTelnet.Core.Combat;

/// <summary>
/// Handles parsing of combat-related text lines to extract damage, death, and experience events
/// </summary>
public class CombatLineParser
{
    // Simple regex patterns for combat detection
    private static readonly Regex NumberRegex = new(@"\d+", RegexOptions.Compiled);
    private static readonly Regex ExperiencePattern = new(
        @"\[Cur:\s*(?<current>\d+)\s+Nxt:\s*(?<next>\d+)\s+Left:\s*(?<left>\d+)\]",
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

    private readonly RoomTracker? _roomTracker;

    public CombatLineParser(RoomTracker? roomTracker = null)
    {
        _roomTracker = roomTracker;
    }

    /// <summary>
    /// Simple player damage detection: "You ... damage" with room monster matching
    /// TelnetClient should have already cleaned punctuation, but we keep basic cleanup for safety
    /// </summary>
    public bool TryParsePlayerDamage(string line, out (string target, int damage) result)
    {
        result = default;

        // Minimal cleanup - TelnetClient should have handled most punctuation
        var originalLine = line;
        line = line.Replace("!", "").Replace(".", "").Trim();

        // Log if we had to clean punctuation (indicates TelnetClient might need adjustment)
        if (originalLine != line && originalLine.Length > 0)
        {
            System.Diagnostics.Debug.WriteLine($"??? COMBAT PUNCTUATION CLEANING: '{originalLine}' -> '{line}' (TelnetClient should have handled this!)");
        }

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
    /// TelnetClient should have already cleaned punctuation, but we keep basic cleanup for safety
    /// </summary>
    public bool TryParseMonsterDamage(string line, out (string monster, int damage) result)
    {
        result = default;

        // Minimal cleanup - TelnetClient should have handled most punctuation
        var originalLine = line;
        line = line.Replace("!", "").Replace(".", "").Trim();

        // Log if we had to clean punctuation (indicates TelnetClient might need adjustment)
        if (originalLine != line && originalLine.Length > 0)
        {
            System.Diagnostics.Debug.WriteLine($"??? COMBAT PUNCTUATION CLEANING: '{originalLine}' -> '{line}' (TelnetClient should have handled this!)");
        }

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
    /// Death detection using unified logic shared with RoomTracker
    /// </summary>
    public bool TryParseDeathEvent(string line, out (List<string> monsters, string deathLine) result)
    {
        result = default;

        if (!IsDeathLine(line))
            return false;

        // Use the same monster matching logic as RoomTracker for consistency
        var foundMonsters = FindMatchingMonstersForDeath(line);

        if (foundMonsters.Count == 0)
            return false;

        result = (foundMonsters, line);
        return true;
    }

    /// <summary>
    /// Parse experience and track the difference to calculate actual gains
    /// Enhanced to handle first-time XP and provide better debugging
    /// </summary>
    public bool TryParseExperience(string line, out int experienceGained, int lastCurrentExperience, int lastExperienceLeft)
    {
        experienceGained = 0;

        var match = ExperiencePattern.Match(line);
        if (!match.Success)
            return false;

        if (int.TryParse(match.Groups["current"].Value, out var current) &&
            int.TryParse(match.Groups["left"].Value, out var left))
        {
            // Debug: Log the XP line and parsing
            System.Diagnostics.Debug.WriteLine($"?? XP PARSING: Line='{line}' Current={current} Left={left} LastCurrent={lastCurrentExperience} LastLeft={lastExperienceLeft}");

            // If we have previous experience data, calculate the actual gain
            if (lastCurrentExperience >= 0 && lastExperienceLeft >= 0)
            {
                // Primary calculation: difference in current experience
                var currentDiff = current - lastCurrentExperience;
                
                // Alternative calculation: difference in left field (should be negative when we gain XP)
                var leftDiff = lastExperienceLeft - left;

                System.Diagnostics.Debug.WriteLine($"?? XP CALCULATION: CurrentDiff={currentDiff} LeftDiff={leftDiff}");

                // Use the most reliable calculation
                if (currentDiff > 0 && currentDiff <= 50000) // Increased upper limit for high-level gains
                {
                    // Current experience increased by a reasonable amount
                    experienceGained = currentDiff;
                    System.Diagnostics.Debug.WriteLine($"?? XP GAIN (current): {experienceGained} XP");
                    return true;
                }
                else if (leftDiff > 0 && leftDiff <= 50000) // Increased upper limit
                {
                    // Left experience decreased by a reasonable amount
                    experienceGained = leftDiff;
                    System.Diagnostics.Debug.WriteLine($"?? XP GAIN (left): {experienceGained} XP");
                    return true;
                }
                else if (currentDiff == 0 && leftDiff == 0)
                {
                    // Same values - no experience change
                    System.Diagnostics.Debug.WriteLine($"?? XP NO CHANGE: Same values as before");
                    return false;
                }
                else
                {
                    // Values changed but not in expected way - might be level up, quest reward, etc.
                    System.Diagnostics.Debug.WriteLine($"?? XP UNUSUAL CHANGE: CurrentDiff={currentDiff} LeftDiff={leftDiff} (might be level up or large reward)");
                    
                    // For very large positive changes, consider it valid XP
                    if (currentDiff > 0)
                    {
                        experienceGained = Math.Min(currentDiff, 100000); // Cap at 100k for sanity
                        System.Diagnostics.Debug.WriteLine($"?? XP LARGE GAIN: {experienceGained} XP (capped from {currentDiff})");
                        return true;
                    }
                    return false;
                }
            }
            else
            {
                // First time seeing XP data - store for next comparison but no gain to report
                System.Diagnostics.Debug.WriteLine($"?? XP BASELINE SET: Current={current} Left={left} (first XP line - no gain calculated)");
                return false;
            }
        }

        System.Diagnostics.Debug.WriteLine($"?? XP PARSE FAILED: Could not parse numbers from '{line}'");
        return false;
    }

    /// <summary>
    /// Parse melee targeting line - "You circle the challenger and prepare to attack!"
    /// </summary>
    public bool TryParseMeleeTargeting(string line, out string targetedMonster)
    {
        targetedMonster = string.Empty;

        var match = MeleeTargetingPattern.Match(line);
        if (!match.Success)
            return false;

        // Extract and normalize the monster name from the targeting message
        var parsedMonsterName = match.Groups[1].Value.Trim();
        targetedMonster = NormalizeMonsterName(parsedMonsterName);

        return !string.IsNullOrWhiteSpace(targetedMonster);
    }

    /// <summary>
    /// Find the first room monster that matches the damage line
    /// </summary>
    public string? FindMatchingRoomMonster(string line)
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
    /// Unified death line detection - same logic as RoomTracker.IsDeathLine
    /// </summary>
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

        // Check that it starts with an article like RoomTracker expects
        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;
        var firstWord = words[0].ToLowerInvariant();
        if (!(firstWord == "the" || firstWord == "a" || firstWord == "an")) return false;

        return DeathWords.Contains(lastWord);
    }

    /// <summary>
    /// Unified monster matching for death events - mirrors RoomTracker.FindMatchingMonsterNames
    /// but also considers active combat entries
    /// </summary>
    private List<string> FindMatchingMonstersForDeath(string line)
    {
        var foundMonsters = new List<string>();

        // Try to match monsters from current room (same as RoomTracker)
        if (_roomTracker?.CurrentRoom?.Monsters != null)
        {
            foreach (var roomMonster in _roomTracker.CurrentRoom.Monsters)
            {
                var monsterName = roomMonster.Name?.Replace(" (summoned)", "") ?? "";
                if (string.IsNullOrWhiteSpace(monsterName))
                    continue;

                // Use the same matching logic as RoomTracker
                if (line.Contains(monsterName, StringComparison.OrdinalIgnoreCase))
                {
                    foundMonsters.Add(monsterName);
                }
            }
        }

        return foundMonsters.Distinct().ToList();
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
}