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

    public void RecordSelection(string username, string characterName, string house)
    {
        lock (_sync)
        {
            var user = _users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            if (user == null) { user = new UserCharacters { Username = username }; _users.Add(user); }
            var rec = user.Characters.FirstOrDefault(c => c.Name.Equals(characterName, StringComparison.OrdinalIgnoreCase));
            if (rec == null) { rec = new CharacterRecord { Name = characterName, House = house }; user.Characters.Add(rec); } else { if (!string.IsNullOrWhiteSpace(house)) rec.House = house; }
            rec.LastSelectedUtc = DateTime.UtcNow;
            user.LastCharacter = rec.Name;
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
