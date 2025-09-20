using System;
using System.Text.RegularExpressions;
using System.Threading;
using DoorTelnet.Core.Automation;
using DoorTelnet.Core.Player;
using DoorTelnet.Core.Telnet;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Wpf.Services;

/// <summary>
/// Lightweight automation layer for character feature flags (auto shield / gong / heal / pickup gold & silver / attack)
/// Evaluates periodically instead of only on stats update so that features keep working even when stats lines pause.
/// </summary>
public class AutomationFeatureService : IDisposable
{
    private readonly StatsTracker _stats;
    private readonly PlayerProfile _profile;
    private readonly TelnetClient _client;
    private readonly ILogger<AutomationFeatureService> _logger;
    private DateTime _lastShield = DateTime.MinValue;
    private DateTime _lastGong = DateTime.MinValue;
    private DateTime _lastHeal = DateTime.MinValue;
    private DateTime _lastAttack = DateTime.MinValue;
    private Timer _timer;

    private readonly Regex _coinRegex = new("(gold|silver) coin", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Regex _mobHereRegex = new(@"(?i)^(?:You see|A|An|The) +([A-Za-z][A-Za-z'\-]+) +(?:is here|stands here|lurks here)\.");
    private readonly Regex _shieldCastRegex = new(@"(magical shield surrounds you|You are surrounded by a magical shield)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Regex _shieldFadeRegex = new(@"(shield fades|magical shield shatters)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public AutomationFeatureService(StatsTracker stats, PlayerProfile profile, TelnetClient client, ILogger<AutomationFeatureService> logger)
    {
        _stats = stats; _profile = profile; _client = client; _logger = logger;
        _stats.Updated += OnStatsUpdated; // update HP snapshot quickly
        _client.LineReceived += OnLine;
        _profile.Updated += () => EvaluateAutomation(true);
        _timer = new Timer(_ => EvaluateAutomation(false), null, 2000, 1000); // evaluate every second
    }

    private int _hpPct; // cached
    private void OnStatsUpdated()
    {
        if (_stats.MaxHp > 0)
        {
            _hpPct = (int)Math.Round((double)_stats.Hp / _stats.MaxHp * 100);
        }
    }

    private void OnLine(string line)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            var feats = _profile.Features;
            // Coin pickup
            if (_coinRegex.IsMatch(line))
            {
                if (feats.PickupGold) _client.SendCommand("get gold");
                if (feats.PickupSilver) _client.SendCommand("get silver");
            }
            // Shield state detection
            if (_shieldCastRegex.IsMatch(line)) _profile.SetShielded(true);
            if (_shieldFadeRegex.IsMatch(line)) _profile.SetShielded(false);

            // AutoAttack detection (opportunistic). If a mob is described in room text.
            if (feats.AutoAttack && (DateTime.UtcNow - _lastAttack).TotalSeconds > 2)
            {
                var mobM = _mobHereRegex.Match(line.Trim());
                if (mobM.Success)
                {
                    var mob = mobM.Groups[1].Value.ToLower();
                    if (!string.IsNullOrWhiteSpace(mob))
                    {
                        _client.SendCommand($"kill {mob}");
                        _lastAttack = DateTime.UtcNow;
                        _logger.LogDebug("AutoAttack -> kill {mob}", mob);
                    }
                }
            }
        }
        catch { }
    }

    private void EvaluateAutomation(bool immediate)
    {
        try
        {
            var feats = _profile.Features;
            var th = _profile.Thresholds;
            var now = DateTime.UtcNow;

            // Auto shield (maintain) - cast if not shielded and interval elapsed
            if (feats.AutoShield && !_profile.Effects.Shielded && th.ShieldRefreshSec >= 0)
            {
                if ((now - _lastShield).TotalSeconds >= Math.Max(5, th.ShieldRefreshSec))
                {
                    var shield = _profile.Player.Shields.FirstOrDefault() ?? "shield";
                    _client.SendCommand(shield);
                    _lastShield = now;
                    _logger.LogTrace("AutoShield cast {spell}", shield);
                }
            }

            // Auto gong: use HP threshold if set (>0) else always (slow interval). Ring actual gong object.
            if (feats.AutoGong)
            {
                bool should = th.GongMinHpPercent > 0 ? _hpPct <= th.GongMinHpPercent : true;
                if (should && (now - _lastGong).TotalSeconds >= 25) // cooldown
                {
                    _client.SendCommand("ring gong");
                    _lastGong = now;
                    _logger.LogTrace("AutoGong ring gong (hp%={pct})", _hpPct);
                }
            }

            // Auto heal
            if (feats.AutoHeal && th.AutoHealHpPercent > 0 && _hpPct > 0 && _hpPct <= th.AutoHealHpPercent)
            {
                if ((now - _lastHeal).TotalSeconds >= 3)
                {
                    var heal = _profile.Player.Heals.OrderByDescending(h => h.Heals).FirstOrDefault();
                    if (heal != null)
                    {
                        _client.SendCommand(heal.Spell);
                        _lastHeal = now;
                        _logger.LogTrace("AutoHeal cast {spell} (hp%={pct})", heal.Spell, _hpPct);
                    }
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        try { _timer?.Dispose(); } catch { }
        _stats.Updated -= OnStatsUpdated;
        _client.LineReceived -= OnLine;
    }
}
