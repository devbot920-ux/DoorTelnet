using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DoorTelnet.Core.Terminal;

namespace DoorTelnet.Core.World;

/// <summary>
/// Represents a line of text with color information for enhanced parsing
/// </summary>
public class ColoredLine
{
    public string Text { get; set; } = string.Empty;
    public List<ColorSegment> Segments { get; set; } = new();
    
    /// <summary>
    /// Dominant foreground color of the line (most common non-default color)
    /// </summary>
    public int? DominantForegroundColor { get; set; }
    
    /// <summary>
    /// Line type based on MUD color conventions
    /// </summary>
    public LineType Type { get; set; } = LineType.Unknown;
}

/// <summary>
/// Represents a colored segment of text within a line
/// </summary>
public class ColorSegment
{
    public string Text { get; set; } = string.Empty;
    public int ForegroundColor { get; set; } = -1; // -1 = default
    public int BackgroundColor { get; set; } = -1; // -1 = default
    public bool Bold { get; set; }
}

/// <summary>
/// Line types based on MUD color conventions
/// </summary>
public enum LineType
{
    Unknown,
    RoomName,      // Cyan (color 6) - room short description
    Monster,       // Red (color 1) - monster lines ending in "is/are here"
    Player,        // Magenta/Purple (color 5) - other player lines ending in "is/are here"  
    Item,          // Cyan (color 6) or Teal - item lines with "lay here"
    Exit,          // Various colors - "Exits:" line
    Combat,        // Various colors - combat messages
    Stats          // Stats line [Hp=/Mp=/Mv=]
}

/// <summary>
/// Handles parsing of room content from text blocks
/// 
/// ENHANCED COLOR-BASED PARSING:
/// The parser now prioritizes color information over text patterns for maximum reliability.
/// 
/// COLOR CONVENTIONS (standard ANSI colors):
/// - Color 1 (Red) or 9 (Bright Red): Monsters
/// - Color 5 (Magenta/Purple): Other players  
/// - Color 6 (Cyan): Room names and items
/// - RGB values like ff0000 (red), 800080 (purple), 00d9ff (cyan), 008080 (teal) map to these indices
/// 
/// ROOM STRUCTURE:
/// 1. Stats line: [Hp=1532/Mp=596/Mv=684/At=1/Ac=1]
/// 2. Room short description (cyan): "You are in Torith Arena."
/// 3. Other players (purple): "HealKind is here."
/// 4. Monsters (red): "Gremlin and forest frig are here."
/// 5. Items (cyan/teal): "Quartz, quartz, lynx eye... lay here."
/// 6. Exits: "Exits: south."
/// 7. Combat/Action lines
/// 
/// PARSING STRATEGY:
/// Primary: Use color information to classify lines
/// Fallback: Use text patterns when color info is unavailable or ambiguous
/// </summary>
public static class RoomParser
{
    private static readonly Regex ExitsLine = new(@"^(?:Obvious )?Exits?:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex MonsterLine = new(@"^(?<list>.+?)\s+(?:is|are|stands|lurks|waits)\s+here\.?$", RegexOptions.IgnoreCase);
    private static readonly Regex LayHere = new(@"^(.+?)\s+lay\s+here\.?(\s+|\s*[\.,])$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex TitleLike = new(@"^[A-Za-z0-9' ,\-()]{3,60}$");
    private static readonly Regex StatsLine = new(@"\[Hp=\d+/Mp=\d+/Mv=\d+(?:/At=\d+)?(?:/Ac=\d+)?\]", RegexOptions.Compiled);
    private static readonly Regex MovementCommands = new(@"^\[Hp=.*?\]\s*\d*[A-Za-z]*[nsewud](?:\s|$)|^\[Hp=.*?\]\s*(north|south|east|west|northeast|northwest|southeast|southwest|up|down)(?:\s|$)", RegexOptions.IgnoreCase);
    private static readonly Regex InvalidRoomNames = new(@"(exits|sorry|no such exit|you peer)", RegexOptions.IgnoreCase);
    private static readonly Regex LongDescriptionTrigger = new(@"^Looking about you notice", RegexOptions.IgnoreCase);

    // MUD color conventions (ANSI color indices)
    private const int COLOR_RED = 1;           // Monsters
    private const int COLOR_BRIGHT_RED = 9;   // Monsters (bright)
    private const int COLOR_MAGENTA = 5;      // Other players
    private const int COLOR_CYAN = 6;         // Room names and items
    private const int COLOR_GREEN = 2;        // Sometimes used for items
    private const int COLOR_BRIGHT_CYAN = 14; // Bright cyan sometimes used

    // Color logging
    public static bool EnableColorLogging { get; set; } = false;

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

    /// <summary>
    /// Parse room state from plain text (backward compatible - no color information)
    /// </summary>
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

    /// <summary>
    /// Parse room state with color information for enhanced accuracy
    /// This is the PRIMARY parsing method when color information is available
    /// </summary>
    public static RoomState? ParseWithColor(List<ColoredLine> coloredLines)
    {
        if (coloredLines == null || coloredLines.Count == 0)
            return null;

        // Classify lines based on color and content - COLOR TAKES PRIORITY
        ClassifyLines(coloredLines);

        // Extract room components using color hints
        var name = ExtractRoomNameWithColor(coloredLines);
        var exits = ExtractExitsWithColor(coloredLines);
        var monsters = DetectMonstersWithColor(coloredLines);
        var items = ExtractItemsWithColor(coloredLines);

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

    /// <summary>
    /// Convert ScreenBuffer snapshot to colored lines for color-aware parsing
    /// </summary>
    public static List<ColoredLine> SnapshotToColoredLines((char ch, ScreenBuffer.CellAttribute attr)[,] snapshot, int rows, int cols)
    {
        var lines = new List<ColoredLine>();

        for (int y = 0; y < rows; y++)
        {
            var line = new ColoredLine();
            var segments = new List<ColorSegment>();
            var lineText = new StringBuilder();
            
            // Track current segment
            ColorSegment? currentSegment = null;
            int currentFg = -1;
            int currentBg = -1;
            bool currentBold = false;

            for (int x = 0; x < cols; x++)
            {
                var (ch, attr) = snapshot[y, x];
                
                // Check if we need to start a new segment
                if (currentSegment == null || attr.Fg != currentFg || attr.Bg != currentBg || attr.Bold != currentBold)
                {
                    // Save previous segment if it exists
                    if (currentSegment != null && currentSegment.Text.Length > 0)
                    {
                        segments.Add(currentSegment);
                    }

                    // Start new segment
                    currentSegment = new ColorSegment
                    {
                        Text = string.Empty,
                        ForegroundColor = attr.Fg,
                        BackgroundColor = attr.Bg,
                        Bold = attr.Bold
                    };
                    currentFg = attr.Fg;
                    currentBg = attr.Bg;
                    currentBold = attr.Bold;
                }

                // Add character to current segment and line
                currentSegment.Text += ch;
                lineText.Append(ch);
            }

            // Save final segment
            if (currentSegment != null && currentSegment.Text.Length > 0)
            {
                segments.Add(currentSegment);
            }

            line.Text = lineText.ToString().TrimEnd();
            line.Segments = segments;
            
            // Calculate dominant color (excluding default/white)
            var colorCounts = segments
                .Where(s => s.ForegroundColor >= 0 && s.ForegroundColor != 7) // Exclude default and white
                .GroupBy(s => s.ForegroundColor)
                .ToDictionary(g => g.Key, g => g.Sum(s => s.Text.Length));
            
            if (colorCounts.Any())
            {
                line.DominantForegroundColor = colorCounts.OrderByDescending(kv => kv.Value).First().Key;
            }

            if (!string.IsNullOrWhiteSpace(line.Text))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    /// <summary>
    /// Classify lines based on COLOR FIRST, then content patterns
    /// This is the key to accurate room parsing
    /// </summary>
    private static void ClassifyLines(List<ColoredLine> lines)
    {
        if (EnableColorLogging)
        {
            System.Diagnostics.Debug.WriteLine("═══════════════════════════════════════════════════════");
            System.Diagnostics.Debug.WriteLine("🎨 COLOR CLASSIFICATION STARTING");
            System.Diagnostics.Debug.WriteLine("═══════════════════════════════════════════════════════");
        }

        int lineNumber = 0;
        foreach (var line in lines)
        {
            lineNumber++;
            
            if (EnableColorLogging)
            {
                LogLineColors(lineNumber, line);
            }

            // Check for stats line first
            if (StatsLine.IsMatch(line.Text))
            {
                line.Type = LineType.Stats;
                if (EnableColorLogging)
                {
                    System.Diagnostics.Debug.WriteLine($"  ✓ Classified as: STATS");
                }
                continue;
            }

            // Check for exits line
            if (ExitsLine.IsMatch(line.Text))
            {
                line.Type = LineType.Exit;
                if (EnableColorLogging)
                {
                    System.Diagnostics.Debug.WriteLine($"  ✓ Classified as: EXIT");
                }
                continue;
            }

            // COLOR-BASED CLASSIFICATION: Check "is/are here" lines
            // Use color to distinguish between monsters (red) and players (purple)
            if (line.Text.IndexOf(" here", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (line.Text.EndsWith(" here.", StringComparison.OrdinalIgnoreCase) || 
                 line.Text.EndsWith(" here", StringComparison.OrdinalIgnoreCase)))
            {
                // PRIORITY 1: Use dominant color to classify
                if (line.DominantForegroundColor == COLOR_RED || line.DominantForegroundColor == COLOR_BRIGHT_RED)
                {
                    line.Type = LineType.Monster;
                    if (EnableColorLogging)
                    {
                        System.Diagnostics.Debug.WriteLine($"  ✓ Classified as: MONSTER (by dominant color {line.DominantForegroundColor})");
                    }
                    continue;
                }
                else if (line.DominantForegroundColor == COLOR_MAGENTA)
                {
                    line.Type = LineType.Player;
                    if (EnableColorLogging)
                    {
                        System.Diagnostics.Debug.WriteLine($"  ✓ Classified as: PLAYER (by dominant color {line.DominantForegroundColor})");
                    }
                    continue;
                }
                
                // PRIORITY 2: Check individual segments for color hints
                bool hasRedSegment = line.Segments.Any(s => s.ForegroundColor == COLOR_RED || s.ForegroundColor == COLOR_BRIGHT_RED);
                bool hasMagentaSegment = line.Segments.Any(s => s.ForegroundColor == COLOR_MAGENTA);
                
                if (hasRedSegment)
                {
                    line.Type = LineType.Monster;
                    if (EnableColorLogging)
                    {
                        System.Diagnostics.Debug.WriteLine($"  ✓ Classified as: MONSTER (by segment color)");
                    }
                    continue;
                }
                else if (hasMagentaSegment)
                {
                    line.Type = LineType.Player;
                    if (EnableColorLogging)
                    {
                        System.Diagnostics.Debug.WriteLine($"  ✓ Classified as: PLAYER (by segment color)");
                    }
                    continue;
                }
                
                // PRIORITY 3: Fallback to text-based detection
                if (MonsterLine.IsMatch(line.Text))
                {
                    line.Type = LineType.Monster;
                    if (EnableColorLogging)
                    {
                        System.Diagnostics.Debug.WriteLine($"  ✓ Classified as: MONSTER (by text pattern fallback)");
                    }
                }
                else
                {
                    line.Type = LineType.Player;
                    if (EnableColorLogging)
                    {
                        System.Diagnostics.Debug.WriteLine($"  ✓ Classified as: PLAYER (by text pattern fallback)");
                    }
                }
                continue;
            }

            // COLOR-BASED: Check for item lines ("lay here")
            if (IsItemLine(line.Text))
            {
                line.Type = LineType.Item;
                if (EnableColorLogging)
                {
                    System.Diagnostics.Debug.WriteLine($"  ✓ Classified as: ITEM");
                }
                continue;
            }

            // COLOR-BASED: Check for room name (cyan color + title-like text)
            // Cyan is the key indicator for room names
            if ((line.DominantForegroundColor == COLOR_CYAN || line.DominantForegroundColor == COLOR_BRIGHT_CYAN) &&
                IsLikelyTitle(line.Text))
            {
                line.Type = LineType.RoomName;
                if (EnableColorLogging)
                {
                    System.Diagnostics.Debug.WriteLine($"  ✓ Classified as: ROOM NAME (cyan + title-like)");
                }
                continue;
            }

            // Default to unknown
            line.Type = LineType.Unknown;
            if (EnableColorLogging)
            {
                System.Diagnostics.Debug.WriteLine($"  ✗ Classified as: UNKNOWN");
            }
        }

        if (EnableColorLogging)
        {
            System.Diagnostics.Debug.WriteLine("═══════════════════════════════════════════════════════");
        }
    }

    private static void LogLineColors(int lineNumber, ColoredLine line)
    {
        var preview = line.Text.Length > 60 ? line.Text.Substring(0, 60) + "..." : line.Text;
        
        System.Diagnostics.Debug.WriteLine($"\n📝 Line {lineNumber}: \"{preview}\"");
        System.Diagnostics.Debug.WriteLine($"  Dominant Color: {FormatColor(line.DominantForegroundColor)}");
        
        if (line.Segments.Count > 0)
        {
            System.Diagnostics.Debug.WriteLine($"  Segments: {line.Segments.Count}");
            
            // Log first few segments with details
            int segCount = Math.Min(5, line.Segments.Count);
            for (int i = 0; i < segCount; i++)
            {
                var seg = line.Segments[i];
                var segPreview = seg.Text.Length > 20 ? seg.Text.Substring(0, 20) + "..." : seg.Text;
                segPreview = segPreview.Replace("\n", "\\n").Replace("\r", "\\r").Trim();
                
                if (!string.IsNullOrWhiteSpace(segPreview))
                {
                    System.Diagnostics.Debug.WriteLine($"    [{i}] FG:{FormatColor(seg.ForegroundColor)} BG:{FormatColor(seg.BackgroundColor)} Bold:{seg.Bold} Text:\"{segPreview}\"");
                }
            }
            
            if (line.Segments.Count > segCount)
            {
                System.Diagnostics.Debug.WriteLine($"    ... and {line.Segments.Count - segCount} more segments");
            }
        }
    }

    private static string FormatColor(int? color)
    {
        if (color == null) return "None";
        if (color < 0) return "Default";
        
        return color switch
        {
            0 => "Black(0)",
            1 => "Red(1)",
            2 => "Green(2)",
            3 => "Yellow(3)",
            4 => "Blue(4)",
            5 => "Magenta(5)",
            6 => "Cyan(6)",
            7 => "White(7)",
            9 => "BrightRed(9)",
            14 => "BrightCyan(14)",
            _ => $"Color({color})"
        };
    }

    /// <summary>
    /// Extract room name using COLOR as primary indicator
    /// ONLY Bold Cyan-colored title-like lines are room names (no fallbacks)
    /// </summary>
    private static string? ExtractRoomNameWithColor(List<ColoredLine> lines)
    {
        // Filter out long description content
        var filteredLines = FilterLongDescription(lines);

        // STRICT REQUIREMENT: Room name must be:
        // 1. Bold cyan (color 6 + bold attribute)
        // 2. Whole line must be cyan (all segments cyan)
        // 3. Title-like format
        // NO FALLBACKS - if we don't see bold cyan, there's no room name
        
        var roomNameCandidates = filteredLines
            .Where(l => l.Type == LineType.RoomName || 
                       (l.DominantForegroundColor == COLOR_CYAN || l.DominantForegroundColor == COLOR_BRIGHT_CYAN))
            .Where(l => !l.Text.Contains("Exits:", StringComparison.OrdinalIgnoreCase))
            .Where(l => !l.Text.Contains("lay here", StringComparison.OrdinalIgnoreCase))
            .Where(l => IsLikelyTitle(l.Text))
            .Where(l => {
                // Check if the line is bold cyan - ALL segments must be cyan and at least one must be bold
                bool allCyan = l.Segments.All(s => s.ForegroundColor == COLOR_CYAN || s.ForegroundColor == COLOR_BRIGHT_CYAN || s.ForegroundColor < 0);
                bool hasBold = l.Segments.Any(s => s.Bold && (s.ForegroundColor == COLOR_CYAN || s.ForegroundColor == COLOR_BRIGHT_CYAN));
                return allCyan && hasBold;
            })
            .Take(10) // Look at first several lines
            .ToList();

        if (roomNameCandidates.Any())
        {
            // Take the FIRST bold cyan title-like line as the room name
            return roomNameCandidates.First().Text.Trim();
        }

        // NO FALLBACKS - if we don't see bold cyan, return null
        if (EnableColorLogging)
        {
            System.Diagnostics.Debug.WriteLine("⚠️ NO ROOM NAME: No bold cyan line found (strict color requirement)");
        }
        
        return null;
    }

    /// <summary>
    /// Detect monsters using COLOR as primary indicator (red = monsters)
    /// </summary>
    private static List<MonsterInfo> DetectMonstersWithColor(List<ColoredLine> lines)
    {
        var monsters = new List<MonsterInfo>();
        var filteredLines = FilterLongDescription(lines);

        foreach (var line in filteredLines)
        {
            // COLOR-BASED: Red lines ending in "is/are here" = monsters
            if (line.Type == LineType.Monster)
            {
                var listPart = Regex.Replace(line.Text, @"\s+(?:is|are|stands|lurks|waits)\s+here\.?$", string.Empty, RegexOptions.IgnoreCase).Trim();
                if (listPart.Length == 0) continue;

                var names = ParseMonsterNames(listPart);
                foreach (var name in names)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        monsters.Add(new MonsterInfo(name, "neutral", false, null));
                }
            }
            // ADDITIONAL: Check for red-colored segments in unclassified lines
            else if (line.Type == LineType.Unknown && 
                     (line.DominantForegroundColor == COLOR_RED || line.DominantForegroundColor == COLOR_BRIGHT_RED))
            {
                // Check if this looks like a monster line
                if (line.Text.IndexOf(" here", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var match = MonsterLine.Match(line.Text);
                    if (match.Success)
                    {
                        var listPart = match.Groups["list"].Value.Trim();
                        var names = ParseMonsterNames(listPart);
                        foreach (var name in names)
                        {
                            if (!string.IsNullOrWhiteSpace(name))
                                monsters.Add(new MonsterInfo(name, "neutral", false, null));
                        }
                    }
                }
            }
        }

        return monsters;
    }

    /// <summary>
    /// Extract items using COLOR (cyan/teal) and handling multi-line wrapping
    /// </summary>
    private static List<string> ExtractItemsWithColor(List<ColoredLine> lines)
    {
        var items = new List<string>();
        var filteredLines = FilterLongDescription(lines);

        // Handle multi-line item wrapping using color hints
        var currentItemBlock = new StringBuilder();
        bool inItemBlock = false;

        foreach (var line in filteredLines)
        {
            bool hasLayHere = line.Text.IndexOf(" lay here", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             line.Text.IndexOf(" lays here", StringComparison.OrdinalIgnoreCase) >= 0;

            if (hasLayHere)
            {
                // This completes an item block
                currentItemBlock.Append(" ");
                currentItemBlock.Append(line.Text);

                var fullItemLine = currentItemBlock.ToString().Trim();
                var match = Regex.Match(fullItemLine, @"^(.+?)\s+(?:lay|lays)\s+here", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var itemText = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(itemText) && !items.Contains(itemText))
                    {
                        items.Add(itemText);
                    }
                }

                currentItemBlock.Clear();
                inItemBlock = false;
            }
            else if (inItemBlock || 
                     (line.DominantForegroundColor == COLOR_CYAN && 
                      line.Type != LineType.RoomName && 
                      line.Type != LineType.Monster && 
                      line.Type != LineType.Player &&
                      line.Type != LineType.Exit &&
                      line.Type != LineType.Stats))
            {
                // COLOR-BASED: Cyan-colored line that's not a room name/monster/player/exit/stats - likely item continuation
                currentItemBlock.Append(" ");
                currentItemBlock.Append(line.Text);
                inItemBlock = true;
            }
        }

        return items;
    }

    /// <summary>
    /// Extract exits with color information
    /// </summary>
    private static List<string> ExtractExitsWithColor(List<ColoredLine> lines)
    {
        var exitLine = lines.FirstOrDefault(l => l.Type == LineType.Exit);
        if (exitLine == null)
            return new List<string>();

        return ExtractExits(exitLine.Text);
    }

    /// <summary>
    /// Filter out long description content from colored lines
    /// </summary>
    private static List<ColoredLine> FilterLongDescription(List<ColoredLine> lines)
    {
        var filtered = new List<ColoredLine>();
        bool inLongDescription = false;

        foreach (var line in lines)
        {
            if (LongDescriptionTrigger.IsMatch(line.Text))
            {
                inLongDescription = true;
                continue;
            }

            if (inLongDescription)
            {
                // Exit long description when we hit recognizable room elements
                if (line.Type == LineType.Monster || line.Type == LineType.Item || 
                    line.Type == LineType.Player || line.Type == LineType.Exit)
                {
                    inLongDescription = false;
                    filtered.Add(line);
                }
                continue;
            }

            filtered.Add(line);
        }

        return filtered;
    }

    // ========== EXISTING TEXT-BASED METHODS (Backward compatible fallbacks) ==========

    private static RoomState? ParseBetweenStatsLines(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        string boundary = "You peer 1 room away";
        int peerIdx = lines.FindIndex(l => l.Contains(boundary));
        if (peerIdx >= 0)
        {
            var segment = string.Join('\n', lines.Skip(peerIdx + 1));
            if (HasRoomIndicators(segment))
            {
                var name = ExtractRoomName(segment);
                var exits = ExtractExits(segment);
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
                if ((!string.IsNullOrEmpty(name) && exits.Count > 0) || 
                    (exits.Count > 0 && (DetectMonsters(between).Count > 0 || ExtractItems(between).Count > 0)))
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

        var beforeClean = s;
        s = Regex.Replace(s, @"\x1B\[[0-9;?]*[A-Za-z]", "");
        s = Regex.Replace(s, @"\x1B[\[()][0-9;]*[A-Za-z@]", "");

        if (s != beforeClean)
        {
            System.Diagnostics.Debug.WriteLine("??? ROOMPARSER ANSI CLEANING: TelnetClient should have cleaned this!");
        }

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
        var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        string boundary = "You peer 1 room away";
        int idx = lines.FindIndex(l => l.Contains(boundary));

        List<string> rel;
        if (idx >= 0)
        {
            rel = [.. lines.Skip(idx + 1)];
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
            rel = lastCmd >= 0 ? [.. lines.Skip(lastCmd + 1)] : lines;
        }

        var exits = rel.Select((line, i) => new { line, i })
            .Where(x => ExitsLine.IsMatch(x.line))
            .ToList();

        if (exits.Count > 0)
        {
            var last = exits.Last().i;
            int start = Math.Max(0, last - 10);
            int end = Math.Min(rel.Count - 1, last + 5);
            var blk = rel.Skip(start).Take(end - start + 1)
                .Where(l => !l.Contains("[Hp=") && !MovementCommands.IsMatch(l) && l != "Sorry, no such exit exists!")
                .ToList();
            return string.Join('\n', blk);
        }

        var filtered = rel.Where(l => !l.Contains("[Hp=") && !MovementCommands.IsMatch(l) && l != "Sorry, no such exit exists!");
        return string.Join('\n', filtered);
    }

    private static string? ExtractRoomName(string block)
    {
        var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return null;

        // Filter out long description trigger and subsequent description text
        var filteredLines = new List<string>();
        bool inLongDescription = false;
        foreach (var line in lines)
        {
            if (LongDescriptionTrigger.IsMatch(line))
            {
                inLongDescription = true;
                continue;
            }
            
            if (inLongDescription)
            {
                if (IsMonsterLine(line) || IsItemLine(line) || ExitsLine.IsMatch(line))
                {
                    inLongDescription = false;
                    filteredLines.Add(line);
                }
                continue;
            }
            
            filteredLines.Add(line);
        }
        
        lines = filteredLines;
        if (lines.Count == 0) return null;

        // ENHANCED: Prioritize finding room name near the beginning (right after stats)
        // In the user's example: stats line, then "You are in Torith Arena.", then other content
        // Strategy: Look for the first valid title-like line before monsters/items
        
        int exitIdx = lines.FindIndex(l => ExitsLine.IsMatch(l));
        
        // First pass: Look for room name before any monster/item lines
        int firstMonsterIdx = lines.FindIndex(l => IsMonsterLine(l));
        int firstItemIdx = lines.FindIndex(l => IsItemLine(l));
        int firstContentIdx = -1;
        
        if (firstMonsterIdx >= 0 && firstItemIdx >= 0)
        {
            firstContentIdx = Math.Min(firstMonsterIdx, firstItemIdx);
        }
        else if (firstMonsterIdx >= 0)
        {
            firstContentIdx = firstMonsterIdx;
        }
        else if (firstItemIdx >= 0)
        {
            firstContentIdx = firstItemIdx;
        }
        
        // Look for room name in the first few lines, before monsters/items
        int searchLimit = firstContentIdx >= 0 ? firstContentIdx : Math.Min(5, lines.Count);
        for (int i = 0; i < searchLimit; i++)
        {
            var cand = lines[i];
            if (IsMonsterLine(cand) || IsItemLine(cand) || IsStatsLine(cand)) continue;
            if (cand.StartsWith("[Hp=", StringComparison.OrdinalIgnoreCase)) continue;
            if (cand.StartsWith("Exits:", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsLikelyTitle(cand))
            {
                return cand.Trim();
            }
        }
        
        // Second pass: Original logic - look backwards from exits line
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

        // Fallback: scan all lines
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
        if (LongDescriptionTrigger.IsMatch(cand)) return false;
        
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
            if (!string.IsNullOrEmpty(dir))
            {
                final.Add(dir);
            }
        }
        else
        {
            var parts = Regex.Split(raw, @"\s*,\s*|and", RegexOptions.IgnoreCase)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            foreach (var part in parts)
            {
                var dir = NormalizeDir(part.Replace(".", ""));
                if (!string.IsNullOrEmpty(dir))
                {
                    final.Add(dir);
                }
            }
        }

        return final;
    }

    private static List<MonsterInfo> DetectMonsters(string block)
    {
        var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var monsters = new List<MonsterInfo>();

        bool inLongDescription = false;
        var filteredLines = new List<string>();
        foreach (var line in lines)
        {
            if (LongDescriptionTrigger.IsMatch(line))
            {
                inLongDescription = true;
                continue;
            }
            
            if (inLongDescription)
            {
                if (IsMonsterLine(line) || IsItemLine(line) || ExitsLine.IsMatch(line))
                {
                    inLongDescription = false;
                    filteredLines.Add(line);
                }
                continue;
            }
            
            filteredLines.Add(line);
        }

        foreach (var line in filteredLines)
        {
            if (MonsterLine.IsMatch(line))
            {
                var listPart = Regex.Replace(line, @"\s+(?:is|are|stands|lurks|waits)\s+here\.?$", string.Empty, RegexOptions.IgnoreCase).Trim();
                if (listPart.Length == 0) continue;

                var names = ParseMonsterNames(listPart);
                foreach (var name in names)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        monsters.Add(new MonsterInfo(name, "neutral", false, null));
                }
            }
        }

        return monsters;
    }

    private static List<string> ParseMonsterNames(string listPart)
    {
        var names = new List<string>();
        
        if (listPart.Contains(','))
        {
            var commaParts = Regex.Split(listPart, @"\s*,\s*").Where(p => p.Length > 0).ToList();
            if (commaParts.Count > 0)
            {
                for (int i = 0; i < commaParts.Count - 1; i++)
                {
                    names.Add(commaParts[i].Trim());
                }

                var lastPart = commaParts[^1].Trim();
                if (Regex.IsMatch(lastPart, @"\s+and\s+", RegexOptions.IgnoreCase))
                {
                    var lastParts = Regex.Split(lastPart, @"\s+and\s+", RegexOptions.IgnoreCase)
                        .Where(p => p.Length > 0)
                        .Select(p => p.Trim());
                    names.AddRange(lastParts);
                }
                else
                {
                    names.Add(lastPart);
                }
            }
        }
        else if (Regex.IsMatch(listPart, @"\s+and\s+", RegexOptions.IgnoreCase))
        {
            names.AddRange(Regex.Split(listPart, @"\s+and\s+", RegexOptions.IgnoreCase)
                .Where(p => p.Length > 0)
                .Select(p => p.Trim()));
        }
        else
        {
            names.Add(listPart);
        }

        return [.. names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim())];
    }

    public static bool IsStatsLine(string line) => StatsLine.IsMatch(line);

    private static List<string> ExtractItems(string block)
    {
        var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var items = new List<string>();

        bool inLongDescription = false;
        var filteredLines = new List<string>();
        foreach (var line in lines)
        {
            if (LongDescriptionTrigger.IsMatch(line))
            {
                inLongDescription = true;
                continue;
            }
            
            if (inLongDescription)
            {
                if (IsMonsterLine(line) || IsItemLine(line) || ExitsLine.IsMatch(line))
                {
                    inLongDescription = false;
                    filteredLines.Add(line);
                }
                continue;
            }
            
            filteredLines.Add(line);
        }

        var currentItemBlock = new StringBuilder();
        
        foreach (var line in filteredLines)
        {
            bool hasLayHere = line.IndexOf(" lay here", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             line.IndexOf(" lays here", StringComparison.OrdinalIgnoreCase) >= 0;
            
            if (hasLayHere)
            {
                currentItemBlock.Append(" ");
                currentItemBlock.Append(line);
                
                var fullItemLine = currentItemBlock.ToString().Trim();
                var match = Regex.Match(fullItemLine, @"^(.+?)\s+(?:lay|lays)\s+here", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    var itemText = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(itemText) && !items.Contains(itemText))
                    {
                        items.Add(itemText);
                    }
                }
                
                currentItemBlock.Clear();
            }
            else if (currentItemBlock.Length > 0)
            {
                currentItemBlock.Append(" ");
                currentItemBlock.Append(line);
            }
            else
            {
                if (!IsMonsterLine(line) && !IsStatsLine(line) && !ExitsLine.IsMatch(line) &&
                    !IsLikelyTitle(line))
                {
                    currentItemBlock.Append(line);
                }
            }
        }

        return items;
    }
}