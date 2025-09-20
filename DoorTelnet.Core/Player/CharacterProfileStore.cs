using System.Text.Json;
using System.Text.RegularExpressions;

namespace DoorTelnet.Core.Player;

public class CharacterProfileStore
{
    private readonly string _filePath;
    private readonly object _sync = new();

    public class CharacterRecord
    {
        public string Name { get; set; } = string.Empty;
        public string House { get; set; } = string.Empty;
        public DateTime LastSelectedUtc { get; set; }
        // Added persistent character meta
        public string Class { get; set; } = string.Empty; // e.g. cleric, mage
        public int Level { get; set; }
        public long Experience { get; set; }
        public DateTime LastStatsUpdateUtc { get; set; }
        public Thresholds Thresholds { get; set; } = new();
        public FeatureFlags Features { get; set; } = new();
        public string Notes { get; set; } = string.Empty; // free-form user notes
    }

    public class UserCharacters
    {
        public string Username { get; set; } = string.Empty;
        public List<CharacterRecord> Characters { get; set; } = new();
        public string? LastCharacter { get; set; }
    }

    private List<UserCharacters> _users = new();

    public CharacterProfileStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public IReadOnlyList<CharacterRecord> GetCharacters(string username)
    {
        lock (_sync)
        {
            return _users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase))?.Characters
                ?.OrderBy(c => c.Name).ToList() ?? new List<CharacterRecord>();
        }
    }

    public CharacterRecord? GetCharacter(string username, string character)
    {
        lock (_sync)
        {
            return _users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase))?
                .Characters.FirstOrDefault(c => c.Name.Equals(character, StringComparison.OrdinalIgnoreCase));
        }
    }

    private CharacterRecord GetOrCreateInternal(string username, string character, string house = "")
    {
        var user = _users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        if (user == null) { user = new UserCharacters { Username = username }; _users.Add(user); }
        var rec = user.Characters.FirstOrDefault(c => c.Name.Equals(character, StringComparison.OrdinalIgnoreCase));
        if (rec == null)
        {
            rec = new CharacterRecord { Name = character, House = house };
            user.Characters.Add(rec);
        }
        else if (!string.IsNullOrWhiteSpace(house))
        {
            rec.House = house; // update if provided
        }
        return rec;
    }

    public void RecordSelection(string username, string characterName, string house)
    {
        lock (_sync)
        {
            var rec = GetOrCreateInternal(username, characterName, house);
            rec.LastSelectedUtc = DateTime.UtcNow;
            var user = _users.First(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            user.LastCharacter = rec.Name;
            Save();
        }
    }

    public void UpdateStats(string username, string character, int level, long experience, string? @class = null)
    {
        lock (_sync)
        {
            var rec = GetOrCreateInternal(username, character);
            if (level > 0) rec.Level = level;
            if (experience >= 0) rec.Experience = experience;
            if (!string.IsNullOrWhiteSpace(@class)) rec.Class = @class;
            rec.LastStatsUpdateUtc = DateTime.UtcNow;
            Save();
        }
    }

    public void UpdateAutomationSettings(string username, string character, Thresholds thresholds, FeatureFlags features)
    {
        lock (_sync)
        {
            var rec = GetOrCreateInternal(username, character);
            // Shallow copy to avoid external references mutating silently later
            rec.Thresholds = thresholds == null ? new Thresholds() : new Thresholds
            {
                HpMin = thresholds.HpMin,
                MpMin = thresholds.MpMin,
                HealMargin = thresholds.HealMargin,
                RestThreshold = thresholds.RestThreshold,
                PanicHp = thresholds.PanicHp,
                ShieldRefreshSec = thresholds.ShieldRefreshSec,
                GongMinHpPercent = thresholds.GongMinHpPercent,
                CriticalHpPercent = thresholds.CriticalHpPercent,
                AutoHealHpPercent = thresholds.AutoHealHpPercent
            };
            rec.Features = features == null ? new FeatureFlags() : new FeatureFlags
            {
                AutoRing = features.AutoRing,
                AutoAttack = features.AutoAttack,
                Detect = features.Detect,
                AutoGong = features.AutoGong,
                PickupGold = features.PickupGold,
                PickupSilver = features.PickupSilver,
                AutoShield = features.AutoShield,
                AutoHeal = features.AutoHeal
            };
            Save();
        }
    }

    public string? GetLastCharacter(string username)
    {
        lock (_sync)
        {
            return _users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase))?.LastCharacter;
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<List<UserCharacters>>(json);
            if (data != null) _users = data;
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var json = JsonSerializer.Serialize(_users, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch { }
    }

    // Parser for the incarnations screen
    private static readonly Regex EntryRegex = new(@"(\d+)\)\s+([^\r\n]+?)\s+of the\s+([^\r\n]+?)\s+house", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IEnumerable<(int index, string name, string house)> ParseIncarnations(string screenText)
    {
        if (string.IsNullOrEmpty(screenText)) yield break;
        if (!screenText.Contains("Incarnations")) yield break;
        foreach (Match m in EntryRegex.Matches(screenText))
        {
            if (!m.Success) continue;
            if (int.TryParse(m.Groups[1].Value, out int idx))
            {
                var name = m.Groups[2].Value.Trim();
                var house = m.Groups[3].Value.Trim();
                yield return (idx, name, house);
            }
        }
    }
}
