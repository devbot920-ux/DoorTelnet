using System.Text.Json;
using System.Text.RegularExpressions;

namespace DoorTelnet.Core.Player;

public class PlayerStatsStore
{
    private readonly string _filePath;
    private readonly object _sync = new();

    public class StatSnapshot
    {
        public string Username { get; set; } = string.Empty;
        public string Character { get; set; } = string.Empty;
        public DateTime CapturedUtc { get; set; }
        public int Level { get; set; }
        public string LevelTitle { get; set; } = string.Empty;
        public int HP { get; set; }
        public int MaxHP { get; set; }
        public int Mana { get; set; }
        public int MaxMana { get; set; }
        public int Move { get; set; }
        public int MaxMove { get; set; }
        public int AC { get; set; }
        public int Absorb { get; set; }
        public int Lives { get; set; }
        public int Deaths { get; set; }
        public int QuestPoints { get; set; }
        public int DevPoints { get; set; }
        public long Experience { get; set; }
        public int AttributePoints { get; set; }
        public Dictionary<string, (int current, int baseVal)> Attributes { get; set; } = new();
        public string RawBlock { get; set; } = string.Empty;
    }

    private readonly List<StatSnapshot> _snapshots = new();

    public PlayerStatsStore(string filePath)
    { _filePath = filePath; Load(); }

    public void AddOrUpdate(StatSnapshot snap)
    {
        lock (_sync)
        {
            // Keep only the most recent per user+character
            _snapshots.RemoveAll(s => s.Username.Equals(snap.Username, StringComparison.OrdinalIgnoreCase) && s.Character.Equals(snap.Character, StringComparison.OrdinalIgnoreCase));
            _snapshots.Add(snap);
            Save();
        }
    }

    public StatSnapshot? Get(string username, string character)
    {
        lock (_sync)
        {
            return _snapshots.FirstOrDefault(s => s.Username.Equals(username, StringComparison.OrdinalIgnoreCase) && s.Character.Equals(character, StringComparison.OrdinalIgnoreCase));
        }
    }

    public IEnumerable<StatSnapshot> AllFor(string username)
    {
        lock (_sync) return _snapshots.Where(s => s.Username.Equals(username, StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => s.CapturedUtc).ToList();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<StatSnapshot>>(json);
            if (list != null) _snapshots.AddRange(list);
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(_snapshots, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }

    // Parser -------------------------------------------------
    private static readonly Regex NameLine = new(@"^Name\s*:\s*(?<name>[^,]+)", RegexOptions.Compiled);
    private static readonly Regex HpLine = new(@"Hitpoints:\s*(?<hp>\d+)\/(?<max>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ManaLine = new(@"Mana\s*:\s*(?<mana>\d+)\/(?<max>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MoveLine = new(@"Movement\s*:\s*(?<mv>\d+)\/(?<max>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LevelLine = new(@"Level\s*:\s*(?<lvl>\d+)\s*\((?<title>[^)]+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AcAbsLine = new(@"Ac/Absorb:\s*(?<ac>\d+)\/(?<abs>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex LivesLine = new(@"Lives/Deaths:\s*(?<l>\d+)\/(?<d>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex QuestLine = new(@"Quest Points:\s*(?<q>\d+).+Development points:\s*(?<dev>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ExpLine = new(@"Experience\s*:\s*(?<xp>\d+)\s+Attribute points\s*:\s*(?<ap>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AttributeLine = new(@"^(Strength|Dexterity|Constitution|Wisdom|Intellect|Perception|Charisma)\s*:\s*(?<cur>\d+)\s*\[(?<base>\d+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public StatSnapshot? TryParseBlock(string username, string character, string block)
    {
        if (string.IsNullOrWhiteSpace(block)) return null;
        if (!block.Contains("Hitpoints")) return null; // heuristic for stat sheet
        var lines = block.Split('\n');
        var snap = new StatSnapshot { Username = username, Character = character, CapturedUtc = DateTime.UtcNow, RawBlock = block };
        foreach (var ln in lines.Select(l => l.TrimEnd()))
        {
            var m = NameLine.Match(ln); if (m.Success) { snap.Character = snap.Character == string.Empty ? m.Groups["name"].Value.Trim() : snap.Character; continue; }
            m = HpLine.Match(ln); if (m.Success) { snap.HP = int.Parse(m.Groups["hp"].Value); snap.MaxHP = int.Parse(m.Groups["max"].Value); continue; }
            m = ManaLine.Match(ln); if (m.Success) { snap.Mana = int.Parse(m.Groups["mana"].Value); snap.MaxMana = int.Parse(m.Groups["max"].Value); continue; }
            m = MoveLine.Match(ln); if (m.Success) { snap.Move = int.Parse(m.Groups["mv"].Value); snap.MaxMove = int.Parse(m.Groups["max"].Value); continue; }
            m = LevelLine.Match(ln); if (m.Success) { snap.Level = int.Parse(m.Groups["lvl"].Value); snap.LevelTitle = m.Groups["title"].Value.Trim(); continue; }
            m = AcAbsLine.Match(ln); if (m.Success) { snap.AC = int.Parse(m.Groups["ac"].Value); snap.Absorb = int.Parse(m.Groups["abs"].Value); continue; }
            m = LivesLine.Match(ln); if (m.Success) { snap.Lives = int.Parse(m.Groups["l"].Value); snap.Deaths = int.Parse(m.Groups["d"].Value); continue; }
            m = QuestLine.Match(ln); if (m.Success) { snap.QuestPoints = int.Parse(m.Groups["q"].Value); snap.DevPoints = int.Parse(m.Groups["dev"].Value); continue; }
            m = ExpLine.Match(ln); if (m.Success) { snap.Experience = long.Parse(m.Groups["xp"].Value); snap.AttributePoints = int.Parse(m.Groups["ap"].Value); continue; }
            m = AttributeLine.Match(ln); if (m.Success) { var attr = m.Groups[1].Value; snap.Attributes[attr] = (int.Parse(m.Groups["cur"].Value), int.Parse(m.Groups["base"].Value)); continue; }
        }
        if (snap.MaxHP == 0) return null; // parsing likely failed
        return snap;
    }
}
