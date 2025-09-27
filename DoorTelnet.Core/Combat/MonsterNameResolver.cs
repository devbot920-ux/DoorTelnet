using System;
using System.Collections.Generic;
using System.Linq;
using DoorTelnet.Core.World;

namespace DoorTelnet.Core.Combat;

/// <summary>
/// Handles monster name resolution and matching between combat events and room monsters
/// </summary>
public class MonsterNameResolver
{
    private readonly RoomTracker? _roomTracker;

    public MonsterNameResolver(RoomTracker? roomTracker = null)
    {
        _roomTracker = roomTracker;
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
    /// Find and return the exact room monster that matches any given monster reference
    /// This ensures all combat tracking uses consistent room-based monster names
    /// </summary>
    public string ResolveToRoomMonsterName(string monsterReference)
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
    public bool DoesMonsterMatch(string roomMonsterName, string monsterReference)
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

    /// <summary>
    /// Remove articles and common prefixes from monster names for matching
    /// </summary>
    public string RemoveArticles(string name)
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
    /// Keep original casing to match what appears in game text exactly
    /// </summary>
    public string NormalizeMonsterName(string name)
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

        // Keep the original casing from the game text - don't force title case
        // This prevents issues with case-sensitive dictionaries
        return normalized;
    }
}