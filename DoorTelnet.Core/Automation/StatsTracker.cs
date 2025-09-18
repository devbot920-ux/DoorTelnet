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

    public bool TryParseLine(string line, Regex regex)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        var m = regex.Match(line);
        if (!m.Success) return false;
        LastProcessedLine = line.TrimEnd();
        bool changed = false;
        lock (_sync)
        {
            if (int.TryParse(m.Groups["hp"].Value, out var hp)) { if (hp > MaxHp) { MaxHp = hp; changed = true; } if (hp != Hp) { Hp = hp; changed = true; } }
            if (int.TryParse(m.Groups["mp"].Value, out var mp)) { if (mp != Mp) { Mp = mp; changed = true; } }
            if (int.TryParse(m.Groups["mv"].Value, out var mv)) { if (mv != Mv) { Mv = mv; changed = true; } }
            var at = (m.Groups["at"].Success && int.TryParse(m.Groups["at"].Value, out var atv)) ? atv : 0; if (at != At) { At = at; changed = true; }
            var ac = (m.Groups["ac"].Success && int.TryParse(m.Groups["ac"].Value, out var acv)) ? acv : 0; if (ac != Ac) { Ac = ac; changed = true; }
            var state = m.Groups["state"].Success ? m.Groups["state"].Value : null; if (state != State) { State = state; changed = true; }
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
