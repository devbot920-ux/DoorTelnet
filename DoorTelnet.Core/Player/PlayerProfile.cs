namespace DoorTelnet.Core.Player;

public class PlayerProfile
{
    public PlayerInfo Player { get; set; } = new();
    public Thresholds Thresholds { get; set; } = new();
    public FeatureFlags Features { get; set; } = new();
    public List<SpellInfo> Spells { get; set; } = new();
    public StatusEffects Effects { get; set; } = new();

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
    }
}

public class PlayerInfo
{
    public string Name { get; set; } = string.Empty;
    public string Class { get; set; } = string.Empty;
    public List<HealSpell> Heals { get; set; } = new();
    public List<string> Shields { get; set; } = new();
    public List<string> Inventory { get; set; } = new();
    public string ArmedWith { get; set; } = string.Empty;
    public string Encumbrance { get; set; } = string.Empty;
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
    public string LongName { get; set; } = string.Empty;
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
    public int GongMinHpPercent { get; set; } = 60;
    // Critical health percentage for emergency healing
    public int CriticalHpPercent { get; set; } = 25;
    // Auto-heal health percentage threshold
    public int AutoHealHpPercent { get; set; } = 70;
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
}
