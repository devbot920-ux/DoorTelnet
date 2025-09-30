namespace DoorTelnet.Core.Player;

public class PlayerProfile
{
    public PlayerInfo Player { get; set; } = new();
    public Thresholds Thresholds { get; set; } = new();
    public FeatureFlags Features { get; set; } = new();
    public List<SpellInfo> Spells { get; set; } = new();
    public StatusEffects Effects { get; set; } = new();

    public event Action? Updated;
    private void RaiseUpdated() => Updated?.Invoke();

    public void AddOrUpdateSpell(SpellInfo info)
    {
        var existing = Spells.FirstOrDefault(s => s.Nick.Equals(info.Nick, StringComparison.OrdinalIgnoreCase));
        if (existing == null) { Spells.Add(info); RaiseUpdated(); }
    }
    public void AddInventoryItem(string item)
    {
        if (!Player.Inventory.Contains(item, StringComparer.OrdinalIgnoreCase)) { Player.Inventory.Add(item); RaiseUpdated(); }
    }
    public void AddHeal(HealSpell heal)
    {
        if (!Player.Heals.Any(h => h.Short.Equals(heal.Short, StringComparison.OrdinalIgnoreCase))) { Player.Heals.Add(heal); RaiseUpdated(); }
    }
    public void AddShield(string shield)
    {
        if (!Player.Shields.Contains(shield, StringComparer.OrdinalIgnoreCase)) { Player.Shields.Add(shield); RaiseUpdated(); }
    }
    public void SetNameClass(string name, string @class) => SetIdentity(name, Player.Race, Player.Walk, @class, Player.Level);

    public void SetIdentity(string? name, string? race, string? walk, string? @class, int? level)
    {
        bool ch = false;
        if (!string.IsNullOrWhiteSpace(name) && name != Player.Name) { Player.Name = name; ch = true; }
        if (!string.IsNullOrWhiteSpace(race) && race != Player.Race) { Player.Race = race; ch = true; }
        if (!string.IsNullOrWhiteSpace(walk) && walk != Player.Walk) { Player.Walk = walk; ch = true; }
        if (!string.IsNullOrWhiteSpace(@class) && @class != Player.Class) { Player.Class = @class; ch = true; }
        if (level.HasValue && level.Value > 0 && level.Value != Player.Level) { Player.Level = level.Value; ch = true; }
        if (!string.IsNullOrWhiteSpace(Player.Name))
        {
            var parts = Player.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0] != Player.FirstName) { Player.FirstName = parts[0]; ch = true; }
            if (parts.Length > 1)
            {
                var ln = string.Join(' ', parts.Skip(1));
                if (ln != Player.LastName) { Player.LastName = ln; ch = true; }
            }
        }
        if (ch) RaiseUpdated();
    }
    public void SetShielded(bool value) { if (Effects.Shielded != value) { Effects.Shielded = value; RaiseUpdated(); } }

    public void SetExperience(long xp)
    { if (Player.Experience != xp) { Player.Experience = xp; RaiseUpdated(); } }
    public void SetXpLeft(long left)
    { if (Player.XpLeft != left) { Player.XpLeft = left; RaiseUpdated(); } }

    public void ReplaceInventory(IEnumerable<string> items)
    {
        var list = items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!list.SequenceEqual(Player.Inventory, StringComparer.OrdinalIgnoreCase))
        {
            Player.Inventory = list;
            RaiseUpdated();
        }
    }
    public void ReplaceSpells(IEnumerable<SpellInfo> spells)
    {
        var ordered = spells.OrderBy(s => s.Nick, StringComparer.OrdinalIgnoreCase).ToList();
        bool changed = ordered.Count != Spells.Count || ordered.Where((t, i) => !Spells[i].Nick.Equals(t.Nick, StringComparison.OrdinalIgnoreCase)).Any();
        if (changed)
        {
            Spells = ordered;
            RaiseUpdated();
        }
    }
    public void ReplaceHeals(IEnumerable<HealSpell> heals)
    {
        var ordered = heals.OrderByDescending(h => h.Heals).ThenBy(h => h.Short).ToList();
        bool changed = ordered.Count != Player.Heals.Count || ordered.Where((t, i) => !string.Equals(Player.Heals[i].Short, t.Short, StringComparison.OrdinalIgnoreCase)).Any();
        if (changed)
        {
            Player.Heals = ordered;
            RaiseUpdated();
        }
    }
    public void ReplaceShields(IEnumerable<string> shields)
    {
        var list = shields.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
        if (!list.SequenceEqual(Player.Shields, StringComparer.OrdinalIgnoreCase))
        {
            Player.Shields = list;
            RaiseUpdated();
        }
    }
    public void UpdateEffects(StatusEffects effects)
    {
        bool changed = Effects.Shielded != effects.Shielded
                       || Effects.Poisoned != effects.Poisoned
                       || !Effects.Boosts.SequenceEqual(effects.Boosts, StringComparer.OrdinalIgnoreCase)
                       || !Effects.Drains.SequenceEqual(effects.Drains, StringComparer.OrdinalIgnoreCase)
                       || !string.Equals(Effects.HungerState, effects.HungerState, StringComparison.OrdinalIgnoreCase)
                       || !string.Equals(Effects.ThirstState, effects.ThirstState, StringComparison.OrdinalIgnoreCase);
        if (changed)
        {
            Effects = effects;
            Effects.LastUpdated = DateTime.UtcNow;
            RaiseUpdated();
        }
    }

    /// <summary>
    /// Reset the player profile to default state
    /// </summary>
    public void Reset()
    {
        Player = new PlayerInfo();
        Thresholds = new Thresholds();
        Features = new FeatureFlags();
        Spells = new List<SpellInfo>();
        Effects = new StatusEffects();
        RaiseUpdated();
    }
}

public class PlayerInfo
{
    public string Name { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Race { get; set; } = string.Empty;
    public string Walk { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public int Level { get; set; }
    public List<HealSpell> Heals { get; set; } = new();
    public List<string> Shields { get; set; } = new();
    public List<string> Inventory { get; set; } = new();
    public string ArmedWith { get; set; } = string.Empty;
    public string Encumbrance { get; set; } = string.Empty;
    public long Experience { get; set; } // current total xp
    public long XpLeft { get; set; } // xp to next level if parsed
}

public class HealSpell
{
    public string Short { get; set; } = string.Empty;
    public string Spell { get; set; } = string.Empty;
    public int Heals { get; set; }
}

public class SpellInfo
{
    public string Nick { get; set; } = string.Empty;
    public string LongName { get; set; } = string.Empty; // forces, life, etc
    public string Sphere { get; set; } = string.Empty; // forces, life, etc
    public int Mana { get; set; }
    public int Diff { get; set; }
    public char SphereCode { get; set; }
}

public class StatusEffects
{
    public List<string> Boosts { get; set; } = new();
    public List<string> Drains { get; set; } = new();
    public bool Shielded { get; set; }
    public bool Poisoned { get; set; }
    public string HungerState { get; set; } = string.Empty; // e.g. satiated, hungry
    public string ThirstState { get; set; } = string.Empty; // e.g. not thirsty
    public DateTime LastUpdated { get; set; } = DateTime.MinValue;
}

public class Thresholds
{
    public int HpMin { get; set; }
    public int MpMin { get; set; }
    public int HealMargin { get; set; }
    public int RestThreshold { get; set; }
    public int PanicHp { get; set; }
    public int ShieldRefreshSec { get; set; }
    // Minimum HP percentage (0-100) required to continue gong automation cycle
    public int GongMinHpPercent { get; set; } = 95;
    // Critical health percentage for emergency healing
    public int CriticalHpPercent { get; set; } = 30;
    // Auto-heal health percentage threshold
    public int AutoHealHpPercent { get; set; } = 95;
    // Warning heal percentage - stops gong and waits for timers before healing
    public int WarningHealHpPercent { get; set; } = 70;
    // Critical action when CriticalHpPercent is reached
    public string CriticalAction { get; set; } = "stop"; // "stop", "disconnect", "script:{command}"
    
    // Navigation thresholds
    public int NavigationMinHpPercent { get; set; } = 30;
    public int MaxRoomDangerLevel { get; set; } = 5;
}

public class FeatureFlags
{
    public bool AutoRing { get; set; }
    public bool AutoAttack { get; set; }
    public bool Detect { get; set; }
    // New automation flags
    public bool AutoGong { get; set; }
    public bool PickupGold { get; set; }
    public bool PickupSilver { get; set; }
    public bool AutoShield { get; set; } // Add auto-shield feature
    public bool AutoHeal { get; set; } // Add auto-heal feature
    
    // Navigation feature flags - Enable by default for testing
    public bool AutoNavigation { get; set; } = true;
    public bool AvoidDangerousRooms { get; set; } = true;
    public bool PauseNavigationInCombat { get; set; } = true;
}
