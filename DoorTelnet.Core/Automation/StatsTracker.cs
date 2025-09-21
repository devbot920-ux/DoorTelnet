using System.Text.RegularExpressions;

namespace DoorTelnet.Core.Automation;

/// <summary>
/// Tracks player statistics parsed from a Rose COG status line.
/// </summary>
public class StatsTracker
{
    private readonly object _sync = new();
    public int Hp { get; private set; }
    public int Mp { get; private set; }
    public int Mv { get; private set; }
    public int At { get; private set; }
    public int Ac { get; private set; }
    public string? State { get; private set; }
    public int MaxHp { get; private set; }
    public string LastProcessedLine { get; private set; } = string.Empty;

    public event Action? Updated;

    public double HpRatio
    {
        get { lock (_sync) return MaxHp > 0 ? (double)Hp / MaxHp : 1.0; }
    }

    // Original CLI pattern
    private readonly Regex _statsRegex = new(@"\[Hp=(?<hp>\d+)/Mp=(?<mp>\d+)/Mv=(?<mv>\d+)(?:/At=(?<at>\d+))?(?:/Ac=(?<ac>\d+))?(?: \((?<state>resting|healing)\))?\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Loose pattern to tolerate truncated leading characters, missing '[', missing 'H' in 'Hp', and trailing text
    // Examples matched: "p=2018/Mp=750/Mv=1051/Ac=2]", "Hp=123/Mp=50/Mv=99/At=10/Ac=2] You are getting pretty hungry." etc.
    private readonly Regex _looseRegex = new(@"(?:(?:\[)?(?:H)?p=(?<hp>\d+)/Mp=(?<mp>\d+)/Mv=(?<mv>\d+)(?:/At=(?<at>\d+))?(?:/Ac=(?<ac>\d+))?\]?)(?:[^\[]*?\((?<state>resting|healing)\))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Hitpoints line from 'stats' / 'st' command: e.g. "Hitpoints:  1518/2115"
    private readonly Regex _hitpointsRegex = new(@"Hitpoints:\s*(?<hp>\d+)\s*/\s*(?<max>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Attempt to parse a line; returns true if it matched any stats-related pattern.
    /// </summary>
    public bool ParseIfStatsLine(string line)
    {
        if (TryParseLine(line, _statsRegex)) return true;
        if (TryParseLine(line, _looseRegex)) return true;
        if (TryParseHitpointsLine(line)) return true; // new fallback source for MaxHp when using 'stats' command
        return false;
    }

    public (int hp, int max, int mp, int mv, int at, int ac, string? state) GetSnapshot()
    {
        lock (_sync)
        {
            return (Hp, MaxHp, Mp, Mv, At, Ac, State);
        }
    }

    public bool TryParseLine(string line, Regex regex)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var m = regex.Match(line);
        if (!m.Success) return false;
        LastProcessedLine = line.TrimEnd();
        bool changed = false;
        lock (_sync)
        {
            if (int.TryParse(m.Groups["hp"].Value, out var hp)) { if (hp != Hp) { Hp = hp; changed = true; } if (hp > MaxHp) { MaxHp = hp; changed = true; } }
            if (int.TryParse(m.Groups["mp"].Value, out var mp)) { if (mp != Mp) { Mp = mp; changed = true; } }
            if (int.TryParse(m.Groups["mv"].Value, out var mv)) { if (mv != Mv) { Mv = mv; changed = true; } }

            // AT optional
            if (m.Groups["at"].Success && int.TryParse(m.Groups["at"].Value, out var atv))
            {
                if (atv != At) { At = atv; changed = true; }
            }
            else if (At != 0)
            {
                At = 0; changed = true;
            }

            // AC optional
            if (m.Groups["ac"].Success && int.TryParse(m.Groups["ac"].Value, out var acv))
            {
                if (acv != Ac) { Ac = acv; changed = true; }
            }
            else if (Ac != 0)
            {
                Ac = 0; changed = true;
            }

            if (m.Groups["state"].Success)
            {
                var state = m.Groups["state"].Value;
                if (state != State) { State = state; changed = true; }
            }
            else if (State != null)
            {
                State = null; changed = true;
            }
        }
        if (changed) Updated?.Invoke();
        return true;
    }

    private bool TryParseHitpointsLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var m = _hitpointsRegex.Match(line);
        if (!m.Success) return false;
        bool changed = false;
        lock (_sync)
        {
            if (int.TryParse(m.Groups["hp"].Value, out var hp))
            {
                if (hp != Hp) { Hp = hp; changed = true; }
            }
            if (int.TryParse(m.Groups["max"].Value, out var max))
            {
                // Accept new max if different (the stats command is authoritative)
                if (max != MaxHp) { MaxHp = max; changed = true; }
                // If current hp somehow exceeds new max (rare), clamp
                if (Hp > MaxHp) { Hp = MaxHp; changed = true; }
            }
        }
        if (changed) Updated?.Invoke();
        return true;
    }

    public string ToStatusString()
    {
        lock (_sync)
        {
            return $"HP:{Hp}/{MaxHp} MP:{Mp} MV:{Mv} AT:{At} AC:{Ac} {(State ?? string.Empty)}".Trim();
        }
    }

    /// <summary>
    /// Reset all tracked statistics to default values
    /// </summary>
    public void Reset()
    {
        lock (_sync)
        {
            Hp = 0;
            Mp = 0;
            Mv = 0;
            At = 0;
            Ac = 0;
            State = null;
            MaxHp = 0;
            LastProcessedLine = string.Empty;
        }
        Updated?.Invoke();
    }
}
