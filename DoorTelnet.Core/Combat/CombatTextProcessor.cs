using System;
using System.Text.RegularExpressions;

namespace DoorTelnet.Core.Combat;

/// <summary>
/// Handles cleaning and pre-processing of combat-related text lines
/// </summary>
public class CombatTextProcessor
{
    // Enhanced stats pattern to detect stats lines that need to be stripped
    private static readonly Regex StatsPattern = new(
        @"\[Hp=\d+/Mp=\d+/Mv=\d+(?:/At=\d+)?(?:/Ac=\d+)?\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExperiencePattern = new(
        @"\[Cur:\s*(?<current>\d+)\s+Nxt:\s*(?<next>\d+)\s+Left:\s*(?<left>\d+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Death detection patterns - using the same logic as RoomModels
    private static readonly string[] DeathWords =
    {
        "banished", "cracks", "darkness", "dead", "death", "defeated", "dies", "disappears",
        "earth", "exhausted", "existance", "existence", "flames", "goddess", "gone", "ground",
        "himself", "killed", "lifeless", "mana", "manaless", "nothingness", "over", "pieces",
        "portal", "scattered", "silent", "slain", "still", "vortex"
    };

    /// <summary>
    /// Clean line content and detect if this is the first stats line (triggers initial commands)
    /// </summary>
    public string CleanLineContent(string line, bool hasSeenFirstStats, out bool triggersInitialCommands)
    {
        triggersInitialCommands = false;
        
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        var originalLine = line;

        // TelnetClient should have already cleaned all ANSI sequences - this is just a safety net
        var beforeBackupClean = line;
        line = Regex.Replace(line, @"\x1B\[[0-9;]*[a-zA-Z]", "", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Log if we found ANSI content that TelnetClient missed (indicates a problem)
        if (beforeBackupClean != line && beforeBackupClean.Length > 0)
        {
            System.Diagnostics.Debug.WriteLine($"??? COMBAT ANSI CLEANING TRIGGERED: '{beforeBackupClean}' -> '{line}' (TelnetClient should have handled this!)");
        }

        // NEW: Check if this is the first stats line we've seen (trigger initial commands)
        // This detects when we've entered the game by seeing our first stats line
        if (!hasSeenFirstStats && StatsPattern.IsMatch(originalLine))
        {
            triggersInitialCommands = true;
            System.Diagnostics.Debug.WriteLine($"??? FIRST STATS LINE DETECTED - triggering initial commands: '{originalLine}'");
        }

        // TelnetClient should have already handled stats removal while preserving XP lines
        // Only do minimal cleanup if we detect stats that might have been missed
        var beforeStatsClean = line;

        // Only remove stats blocks if this isn't an XP line
        if (!ExperiencePattern.IsMatch(line))
        {
            line = StatsPattern.Replace(line, "").Trim();
        }

        // Log if we had to do stats cleaning (indicates TelnetClient might need adjustment)
        if (beforeStatsClean != line && beforeStatsClean.Length > 0)
        {
            System.Diagnostics.Debug.WriteLine($"??? COMBAT STATS CLEANING TRIGGERED: '{beforeStatsClean}' -> '{line}' (TelnetClient should have handled this!)");
        }

        // Enhanced stats line handling similar to RoomTracker improvements
        // Check if line has stats prefix but contains valuable combat data
        if (originalLine != line && !string.IsNullOrWhiteSpace(line))
        {
            // Check if remaining content looks like combat information
            if (HasPotentialCombatContent(line))
            {
                // Use the cleaned line for processing
                line = line.Trim();
            }
        }

        // Minimal partial stats cleaning (TelnetClient should handle most of this)
        line = StripLeadingPartialStats(line);

        // Remove extra whitespace
        line = Regex.Replace(line, @"\s+", " ").Trim();

        return line;
    }

    /// <summary>
    /// Strip leading partial statistics - simplified since TelnetClient should handle most cases
    /// </summary>
    private string StripLeadingPartialStats(string line)
    {
        if (string.IsNullOrEmpty(line))
            return line;

        var originalLine = line;

        // Remove leading partial brackets like "[C" that interfere with parsing
        // This is a safety net since TelnetClient should handle most cases
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

                    // Log if we had to do partial stats cleaning (indicates TelnetClient might need adjustment)
                    if (originalLine != line)
                    {
                        System.Diagnostics.Debug.WriteLine($"??? COMBAT PARTIAL STATS CLEANING: '{originalLine}' -> '{line}' (TelnetClient should have handled this!)");
                    }
                }
            }
            else
            {
                // No closing bracket found, likely partial - remove the opening part
                var spaceIndex = line.IndexOf(' ');
                if (spaceIndex > 0 && spaceIndex < 5) // Only if it's a short partial
                {
                    line = line.Substring(spaceIndex + 1).TrimStart();

                    // Log the cleaning
                    if (originalLine != line)
                    {
                        System.Diagnostics.Debug.WriteLine($"??? COMBAT PARTIAL STATS CLEANING (no bracket): '{originalLine}' -> '{line}' (TelnetClient should have handled this!)");
                    }
                }
            }
        }

        return line;
    }

    /// <summary>
    /// Check if a line (after stats removal) contains potential combat content
    /// </summary>
    public static bool HasPotentialCombatContent(string cleanedLine)
    {
        if (string.IsNullOrWhiteSpace(cleanedLine)) return false;

        // Check for combat-like content indicators
        if (cleanedLine.Contains("damage", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains("You circle", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains("prepare to attack", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains("You suffered", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains("strikes", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains("attacks", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains("hits", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains("slashes", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains("pierces", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check for death-related content
        var trimmed = cleanedLine.TrimEnd('.', '!', '?', ' ');
        var words = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length > 0)
        {
            var lastWord = words[^1].ToLowerInvariant();
            var firstWord = words[0].ToLowerInvariant();

            // Death line pattern: starts with "the/a/an" and ends with death word
            if ((firstWord == "the" || firstWord == "a" || firstWord == "an") &&
                DeathWords.Contains(lastWord))
            {
                return true;
            }
        }

        // Check for experience content
        if (cleanedLine.Contains("Cur:", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains("Nxt:", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains("Left:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}