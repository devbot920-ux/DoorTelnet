using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DoorTelnet.Core.World;

/// <summary>
/// Handles parsing of room content from text blocks
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
            rel = lines.Skip(idx + 1).ToList();
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
            rel = lastCmd >= 0 ? lines.Skip(lastCmd + 1).ToList() : lines;
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

        int exitIdx = lines.FindIndex(l => ExitsLine.IsMatch(l));
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

        foreach (var line in lines)
        {
            if (MonsterLine.IsMatch(line))
            {
                var listPart = Regex.Replace(line, @"\s+(?:is|are)\s+here\.?$", string.Empty, RegexOptions.IgnoreCase).Trim();
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

        return names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
    }

    public static bool IsStatsLine(string line) => StatsLine.IsMatch(line);

    private static List<string> ExtractItems(string block)
    {
        var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var items = new List<string>();

        foreach (var line in lines)
        {
            if (line.IndexOf(" lay here", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var item = line.Substring(0, line.IndexOf(" lay here", StringComparison.OrdinalIgnoreCase)).Trim();
                if (!string.IsNullOrWhiteSpace(item) && !items.Contains(item))
                {
                    items.Add(item);
                }
            }
            else if (line.IndexOf(" lays here", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var item = line.Substring(0, line.IndexOf(" lays here", StringComparison.OrdinalIgnoreCase)).Trim();
                if (!string.IsNullOrWhiteSpace(item) && !items.Contains(item))
                {
                    items.Add(item);
                }
            }
            else if (LayHere.IsMatch(line))
            {
                var match = LayHere.Match(line);
                if (match.Groups.Count > 1)
                {
                    var item = match.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(item) && !items.Contains(item))
                    {
                        items.Add(item);
                    }
                }
            }
        }

        return items;
    }
}