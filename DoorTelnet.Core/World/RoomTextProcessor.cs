using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DoorTelnet.Core.World;

/// <summary>
/// Handles cleaning and processing of room-related text content
/// </summary>
public static class RoomTextProcessor
{
    /// <summary>
    /// Clean room line content for processing
    /// </summary>
    public static string CleanLineContent(string line)
    {
        if (string.IsNullOrEmpty(line)) return string.Empty;

        var originalLine = line;

        // Enhanced raw input logging for debugging
        if (ShouldLogRawInput())
        {
            LogRawInput(originalLine);
        }

        var cleanedLine = PerformTextCleaning(line);

        // Log cleaning if it was applied
        if (originalLine != cleanedLine && ShouldLogCleaning())
        {
            System.Diagnostics.Debug.WriteLine("?? CLEANING APPLIED:");
            System.Diagnostics.Debug.WriteLine($"   RAW: '{originalLine}'");
            System.Diagnostics.Debug.WriteLine($"   CLEAN: '{cleanedLine}'");
        }

        return cleanedLine;
    }

    /// <summary>
    /// Strip leading partial stats from a line
    /// </summary>
    public static (string stripped, bool hadFragments) StripLeadingPartialStats(string line)
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

    /// <summary>
    /// Check if a line (after stats removal) contains potential room content
    /// </summary>
    public static bool HasPotentialRoomContent(string cleanedLine)
    {
        if (string.IsNullOrWhiteSpace(cleanedLine)) return false;

        // Check for room-like content indicators
        if (cleanedLine.Contains("Exits:", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains(" is here", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains(" are here", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains(" lay here", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains(" lays here", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains("summoned for combat", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains(" enters ", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains(" leaves ", StringComparison.OrdinalIgnoreCase)
            || cleanedLine.Contains(" follows you", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check if it looks like a room title/description (more than 5 chars, contains letters)
        if (cleanedLine.Length >= 5 && cleanedLine.Count(char.IsLetter) >= 3)
        {
            // Exclude common non-room messages
            if (!cleanedLine.StartsWith("You ", StringComparison.OrdinalIgnoreCase)
                && !cleanedLine.StartsWith("Your ", StringComparison.OrdinalIgnoreCase)
                && !cleanedLine.Contains("suffered", StringComparison.OrdinalIgnoreCase)
                && !cleanedLine.Contains("damage", StringComparison.OrdinalIgnoreCase)
                && !cleanedLine.Contains("experience", StringComparison.OrdinalIgnoreCase)
                && !cleanedLine.Contains("Welcome", StringComparison.OrdinalIgnoreCase)
                && !cleanedLine.Contains("Goodbye", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ShouldLogRawInput() => 
        // Could be made configurable, for now just log first few lines for debugging
        true;

    private static bool ShouldLogCleaning() => true;

    private static void LogRawInput(string originalLine)
    {
        var hexRep = new StringBuilder();
        var printableRep = new StringBuilder();

        foreach (char c in originalLine.Take(100))
        {
            hexRep.Append($"{(int)c:X2} ");
            if (c >= 32 && c <= 126)
            {
                printableRep.Append(c);
            }
            else if (c == '\n')
            {
                printableRep.Append("\\n");
            }
            else if (c == '\r')
            {
                printableRep.Append("\\r");
            }
            else if (c == '\t')
            {
                printableRep.Append("\\t");
            }
            else if (c == '\x1B')
            {
                printableRep.Append("\\ESC");
            }
            else
            {
                printableRep.Append($"[{(int)c:X2}]");
            }
        }

        System.Diagnostics.Debug.WriteLine($"??? RAW INPUT: '{printableRep}'");
        System.Diagnostics.Debug.WriteLine($"    HEX: {hexRep}");
    }

    private static string PerformTextCleaning(string line)
    {
        // Remove stats content from the line (TelnetClient should have done this, but safety net)
        line = Regex.Replace(line, @"\[Hp=\d+/Mp=\d+/Mv=\d+(?:/At=\d+)?(?:/Ac=\d+)?\]", "", RegexOptions.IgnoreCase);

        var (afterStrip, had) = StripLeadingPartialStats(line);
        line = afterStrip;

        if (had && string.IsNullOrWhiteSpace(line)) return string.Empty;

        if (Regex.IsMatch(line, @"^(p=|Mp=|Mv=)\d+", RegexOptions.IgnoreCase)) return string.Empty;

        var fragCount = Regex.Matches(line, @"(p=\d+|Mp=\d+|Mv=\d+)", RegexOptions.IgnoreCase).Count;
        if (fragCount >= 3 && !line.Contains("[Hp=")) return string.Empty;

        // TelnetClient should have already cleaned all ANSI sequences - this is just a safety net
        var beforeBackupClean = line;

        // Minimal backup ANSI cleaning (should not be needed with proper TelnetClient)
        line = Regex.Replace(line, @"\x1B\[[0-9;]*[A-Za-z@]", "", RegexOptions.IgnoreCase);

        // Log if we found ANSI content that TelnetClient missed (indicates a problem)
        if (beforeBackupClean != line && beforeBackupClean.Length > 0)
        {
            System.Diagnostics.Debug.WriteLine($"??? BACKUP ANSI CLEANING TRIGGERED: '{beforeBackupClean}' -> '{line}' (TelnetClient should have handled this!)");
        }

        // Movement command filtering (TelnetClient should handle this, but safety net)
        if (Regex.IsMatch(line, @"^\d*[A-Za-z]*[nsewud]$", RegexOptions.IgnoreCase))
        {
            System.Diagnostics.Debug.WriteLine($"??? MOVEMENT FILTERED IN ROOMTRACKER: '{line}' (TelnetClient should have filtered this)");
            return string.Empty;
        }

        // Basic character sanitization
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

        // Final length check
        if (result.Length < 2)
        {
            if (line != result && line.Length > 2)
            {
                System.Diagnostics.Debug.WriteLine($"??? FILTERED TOO SHORT: '{result}' (from original: '{line}')");
            }
            return string.Empty;
        }

        return result;
    }

    /// <summary>
    /// Check if a line looks like room content vs other content
    /// </summary>
    public static bool HasBasicRoomContent(string c)
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
               || Regex.IsMatch(c, @"^(?:.+?)\s+(?:is|are)\s+here\.?$", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Check if a line should be filtered out as obviously non-room content
    /// </summary>
    public static bool IsObviouslyNonRoomLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return true;

        line = line.Trim();

        // First, try to strip out stats blocks and see if remaining content could be room info
        var originalLine = line;

        // Remove complete stats blocks like [Hp=2018/Mp=750/Mv=921/At=3/Ac=3]
        line = Regex.Replace(line, @"\[Hp=\d+/Mp=\d+/Mv=\d+(?:/At=\d+)?(?:/Ac=\d+)?\]", "", RegexOptions.IgnoreCase);

        // Remove partial stats fragments
        line = Regex.Replace(line, @"^(?:[\x00-\x20]*p=\d+/Mp=\d+/Mv=\d+\]?\s*)+", "", RegexOptions.IgnoreCase);
        line = Regex.Replace(line, @"^(p=|Mp=|Mv=|Ac=)\d+[^\]]*\]?", "", RegexOptions.IgnoreCase);

        // Clean up whitespace
        line = line.Trim();

        // If we stripped stats and found potential room content, don't filter it out
        if (originalLine != line && !string.IsNullOrWhiteSpace(line))
        {
            // Check if the remaining content looks like room information
            if (HasPotentialRoomContent(line))
            {
                return false; // Don't filter - this could be room info with stats prefix
            }
        }

        // Use the cleaned line for further checks
        line = string.IsNullOrWhiteSpace(line) ? originalLine : line;

        // Check for obvious stats-only lines (handled by TelnetClient, but keep as safety net)
        if (RoomParser.IsStatsLine(line)) return true;

        // Check for stats fragments (handled by TelnetClient PostProcessLine, but keep as safety net)
        if (Regex.IsMatch(line, @"^(p=|Mp=|Mv=|Ac=)\d+", RegexOptions.IgnoreCase)) return true;
        if (line.Contains("p=") && line.Contains("Mp=") && line.Contains("Mv=") && !line.Contains("[Hp=")) return true;

        // Movement commands (should be filtered by TelnetClient, but keep as safety net)
        if (Regex.IsMatch(line, @"^\d*[A-Za-z]*[nsewud]$", RegexOptions.IgnoreCase)) return true;

        // System/connection messages
        if (line.StartsWith("}")
            || line.StartsWith(">")
            || line.StartsWith("Press")
            || line.StartsWith("Type")
            || line.StartsWith("Command:")
            || line.StartsWith("Password:")
            || line.StartsWith("Username:")
            || line.StartsWith("Login:"))
        {
            return true;
        }

        // Very short lines
        if (line.Length < 3) return true;

        // Connection status messages
        if (line.Contains("connected")
            || line.Contains("logged in")
            || line.Contains("disconnected")
            || line.Contains("*** "))
        {
            return true;
        }

        // Malformed bracket lines
        if (Regex.IsMatch(line, @"^[\]})][^a-zA-Z]*$")) return true;

        return false;
    }
}