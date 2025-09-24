using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks; // Added for Task
using DoorTelnet.Core.Automation;
using DoorTelnet.Core.Player;
using DoorTelnet.Core.Telnet;
using DoorTelnet.Core.World; // added for RoomTracker
using DoorTelnet.Core.Combat; // added for CombatTracker
using Microsoft.Extensions.Logging;
using System.Linq;
using DoorTelnet.Core.Player; // for SpellInfo

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
    private readonly RoomTracker _room; // inject room tracker to mirror CLI auto gong logic
    private readonly CombatTracker _combat; // inject combat tracker for targeting logic
    private readonly CharacterProfileStore _charStore; // NEW for obtaining character name
    private readonly ILogger<AutomationFeatureService> _logger;
    private DateTime _lastShield = DateTime.MinValue;
    private DateTime _lastHeal = DateTime.MinValue;
    private Timer _timer;

    // AutoGong state (mirrors CLI Runner.TryRunAutomation logic)
    private bool _inGongCycle;
    private bool _waitingForTimers; // after kill & loot, wait for AT/AC reset
    private bool _waitingForHealTimers; // NEW: waiting for timers due to warning heal level
    private DateTime _lastGongAction = DateTime.MinValue; // last time we sent 'r g'

    // Unified attack tracking - used by both AutoGong and AutoAttack
    private readonly HashSet<string> _attackedMonsters = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastAttackReset = DateTime.MinValue;

    private readonly Regex _coinRegex = new("(gold|silver) coin", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Regex _mobHereRegex = new(@"(?i)^(?:You see|A|An|The) +([A-Za-z][A-Za-z'\-]+) +(?:is here|stands here|lurks here)\.\s*$");
    private readonly Regex _shieldCastRegex = new(@"(magical shield surrounds you|You are surrounded by a magical shield|You are shielded\.)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Regex _shieldFadeRegex = new(@"(shield fades|magical shield shatters|shield disipated|shield dissipated)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private bool _lastAutoGongEnabled;
    private string? _selectedUser; // future: could come from credentials dialog

    public AutomationFeatureService(StatsTracker stats, PlayerProfile profile, TelnetClient client, RoomTracker room, CombatTracker combat, CharacterProfileStore charStore, ILogger<AutomationFeatureService> logger)
    {
        _stats = stats; _profile = profile; _client = client; _room = room; _combat = combat; _charStore = charStore; _logger = logger;
        _stats.Updated += OnStatsUpdated; // update HP snapshot quickly
        _client.LineReceived += OnLine;
        _profile.Updated += () => EvaluateAutomation(true);
        _timer = new Timer(_ => EvaluateAutomation(false), null, 2000, 1000); // evaluate every second
        _lastAutoGongEnabled = profile.Features.AutoGong;
        
        // Subscribe to combat targeting events
        _combat.MonsterTargeted += OnMonsterTargeted;
        _combat.MonsterDeath += OnMonsterDeath;
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
            // Coin pickup (immediate reactions to coin messages outside primary gong loop)
            if (_coinRegex.IsMatch(line))
            {
                if (feats.PickupGold) _client.SendCommand("get gold");
                if (feats.PickupSilver) _client.SendCommand("get silver");
            }
            // Shield state detection
            if (_shieldCastRegex.IsMatch(line)) _profile.SetShielded(true);
            if (_shieldFadeRegex.IsMatch(line)) _profile.SetShielded(false);

            // Hunger / thirst quick detection lines (mirrors CLI screen scanning but cheaper per-line triggers)
            if (line.Contains("stomach growls", StringComparison.OrdinalIgnoreCase))
            {
                if (_profile.Effects.HungerState != "hungry") { _profile.Effects.HungerState = "hungry"; _profile.Effects.LastUpdated = DateTime.UtcNow; }
            }
            if (line.Contains("entirely satiated", StringComparison.OrdinalIgnoreCase))
            {
                if (_profile.Effects.HungerState != "satiated") { _profile.Effects.HungerState = "satiated"; _profile.Effects.LastUpdated = DateTime.UtcNow; }
            }
            if (line.Contains("least bit thirsty", StringComparison.OrdinalIgnoreCase))
            {
                if (_profile.Effects.ThirstState != "not thirsty") { _profile.Effects.ThirstState = "not thirsty"; _profile.Effects.LastUpdated = DateTime.UtcNow; }
            }

            // AutoAttack (independent of gong cycle) - attacks any aggressive monsters immediately
            // This now shares the same attack tracking logic as AutoGong
            if (feats.AutoAttack && !feats.AutoGong) // Only do independent AutoAttack if AutoGong is disabled
            {
                AttackAggressiveMonsters("AutoAttack");
            }
        }
        catch { }
    }

    /// <summary>
    /// Unified method to attack aggressive monsters, used by both AutoGong and AutoAttack
    /// </summary>
    private void AttackAggressiveMonsters(string context)
    {
        var room = _room.CurrentRoom;
        if (room == null) return;

        var aggressiveMobs = room.Monsters
            .Where(m => m.Disposition.Equals("aggressive", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (aggressiveMobs.Count > 0)
        {
            // Reset attack tracking every 30 seconds to allow re-attacking new spawns
            var now = DateTime.UtcNow;
            if ((now - _lastAttackReset).TotalSeconds >= 30)
            {
                _attackedMonsters.Clear();
                _lastAttackReset = now;
                _logger.LogTrace("{context} reset attacked monsters list", context);
            }

            // Check if there's a targeted monster that's still alive and aggressive
            var targetedMonster = _combat.GetTargetedMonster();
            MonsterInfo? next = null;
            
            if (targetedMonster != null)
            {
                // Try to find the targeted monster in the current room's aggressive monsters
                next = aggressiveMobs.FirstOrDefault(m => 
                    string.Equals(m.Name?.Replace(" (summoned)", ""), targetedMonster.MonsterName, StringComparison.OrdinalIgnoreCase));
                
                if (next != null && !_attackedMonsters.Contains(next.Name))
                {
                    _logger.LogTrace("Prioritizing targeted monster '{monster}' for {context}", next.Name, context);
                }
                else if (next != null)
                {
                    next = null; // Already processed this targeted monster
                }
            }
            
            // Fallback to normal selection if no targeted monster or targeted monster already processed
            if (next == null)
            {
                // Select first not yet attacked
                next = aggressiveMobs.FirstOrDefault(m => !_attackedMonsters.Contains(m.Name));
            }
            
            if (next != null)
            {
                var firstLetter = next.Name.TrimStart().FirstOrDefault(char.IsLetter);
                if (firstLetter != '\0')
                {
                    var letterLower = char.ToLowerInvariant(firstLetter);
                    _client.SendCommand($"a {letterLower}");
                    _attackedMonsters.Add(next.Name);
                    var targetStatus = targetedMonster != null && string.Equals(next.Name?.Replace(" (summoned)", ""), targetedMonster.MonsterName, StringComparison.OrdinalIgnoreCase) ? " [TARGETED]" : "";
                    _logger.LogInformation("{context} attacking mob '{mob}' (letter '{letter}'){targetStatus} (remaining targets: {count})", context, next.Name, letterLower, targetStatus, aggressiveMobs.Count(m => !_attackedMonsters.Contains(m.Name)));
                }
                else
                {
                    // If name starts with non letter, try full name attack (some servers support this)
                    _client.SendCommand($"a {next.Name.Split(' ').FirstOrDefault()}");
                    _attackedMonsters.Add(next.Name);
                    var targetStatus = targetedMonster != null && string.Equals(next.Name?.Replace(" (summoned)", ""), targetedMonster.MonsterName, StringComparison.OrdinalIgnoreCase) ? " [TARGETED]" : "";
                    _logger.LogInformation("{context} attacking mob '{mob}'{targetStatus} via name fallback (remaining targets: {count})", context, next.Name, targetStatus, aggressiveMobs.Count(m => !_attackedMonsters.Contains(m.Name)));
                }
            }
        }
        else
        {
            // No aggressive monsters - clear attack tracking for next wave (but only in AutoAttack context)
            if (context == "AutoAttack" && _attackedMonsters.Count > 0)
            {
                _attackedMonsters.Clear();
                _logger.LogTrace("{context} cleared attacked monsters - no aggressive mobs remain", context);
            }
        }
    }

    private void EvaluateAutomation(bool immediate)
    {
        try
        {
            var feats = _profile.Features;
            var th = _profile.Thresholds;
            var now = DateTime.UtcNow;

            // Check critical health first
            if (_stats.MaxHp > 0 && _hpPct <= th.CriticalHpPercent && th.CriticalHpPercent > 0)
            {
                HandleCriticalHealth(th.CriticalAction);
                return; // Exit early on critical health
            }

            // Auto shield (improved: select best available shield spell and cast on character)
            if (feats.AutoShield && !_profile.Effects.Shielded)
            {
                if ((now - _lastShield).TotalSeconds >= Math.Max(5, Math.Max(th.ShieldRefreshSec, 10)))
                {
                    var bestShield = SelectBestShieldSpell();
                    if (bestShield != null)
                    {
                        var target = GetCharacterName();
                        if (string.IsNullOrWhiteSpace(target))
                        {
                            _logger.LogDebug("AutoShield skipped - no character name yet");
                        }
                        else
                        {
                            var cmd = $"cast {bestShield.Nick} {target}";
                            _client.SendCommand(cmd);
                            _lastShield = now;
                            _logger.LogTrace("AutoShield cast {spell}", bestShield.Nick);
                        }
                    }
                    else
                    {
                        var shield = _profile.Player.Shields.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(shield))
                        {
                            _client.SendCommand(shield);
                            _lastShield = now;
                            _logger.LogTrace("AutoShield (fallback) '{spell}'", shield);
                        }
                    }
                }
            }

            // ---------- Auto Gong (full cycle with integrated attack logic) ----------
            if (feats.AutoGong)
            {
                // On enable edge, reset state so we can begin fresh
                if (!_lastAutoGongEnabled)
                {
                    _inGongCycle = false;
                    _waitingForTimers = false;
                    _waitingForHealTimers = false;
                    _attackedMonsters.Clear(); // Clear the unified attack tracking
                }

                // Need valid stats & HP threshold
                if (_stats.MaxHp > 0)
                {
                    var hpPercent = _hpPct; // already cached
                    
                    // Check warning heal level - stop gong and wait for heal timers
                    if (hpPercent <= th.WarningHealHpPercent && th.WarningHealHpPercent > 0 && !_waitingForHealTimers)
                    {
                        if (_inGongCycle)
                        {
                            _client.SendCommand("stop");
                            _inGongCycle = false;
                            _waitingForHealTimers = true;
                            _logger.LogInformation("AutoGong stopped due to warning heal level - HP {hp}% below {thresh}%", hpPercent, th.WarningHealHpPercent);
                        }
                    }
                    
                    // If waiting for heal timers, check if we can resume
                    if (_waitingForHealTimers)
                    {
                        if (_stats.At == 0 && _stats.Ac == 0)
                        {
                            _waitingForHealTimers = false;
                            _logger.LogTrace("AutoGong heal timers ready - can resume");
                        }
                    }
                    
                    if (hpPercent < th.GongMinHpPercent)
                    {
                        if (_inGongCycle || _waitingForTimers)
                        {
                            _inGongCycle = false; 
                            _waitingForTimers = false;
                            _logger.LogDebug("AutoGong paused - HP {hp}% below threshold {thresh}%", hpPercent, th.GongMinHpPercent);
                        }
                    }
                    else if (!_waitingForHealTimers) // Don't start new cycles while waiting for heal timers
                    {
                        bool timersReady = _stats.At == 0 && _stats.Ac == 0; // conservative readiness (used for starting a new cycle)
                        var room = _room.CurrentRoom; // may be null if room not parsed yet
                        if (room != null)
                        {
                            const int minGongIntervalMs = 2000; // minimal guard between gong rings
                            var aggressivePresent = room.Monsters.Any(m => m.Disposition.Equals("aggressive", StringComparison.OrdinalIgnoreCase));

                            // Waiting for timers to reset after loot phase
                            if (!aggressivePresent && _waitingForTimers)
                            {
                                if (timersReady)
                                {
                                    _waitingForTimers = false;
                                    _inGongCycle = false;
                                    _logger.LogTrace("AutoGong timers reset - ready for next cycle");
                                }
                            }
                            else if (!aggressivePresent && !_inGongCycle)
                            {
                                // Start new cycle only if no aggressive monsters present
                                if (timersReady && (now - _lastGongAction).TotalMilliseconds >= minGongIntervalMs)
                                {
                                    _inGongCycle = true;
                                    _attackedMonsters.Clear(); // Clear attack tracking for new gong cycle
                                    _lastGongAction = now;
                                    _client.SendCommand("r g");
                                    _logger.LogInformation("AutoGong rung gong (r g)");
                                }
                            }
                            else if (_inGongCycle && aggressivePresent) // active cycle with monsters to attack
                            {
                                // Use the unified attack logic
                                AttackAggressiveMonsters("AutoGong");
                            }
                            else if (_inGongCycle && !aggressivePresent) // active cycle but no more monsters
                            {
                                if (!_waitingForTimers)
                                {
                                    if (feats.PickupGold && room.Monsters.Count == 0) { _client.SendCommand("g gold"); _logger.LogTrace("AutoGong loot gold"); }
                                    if (feats.PickupSilver) { _client.SendCommand("g sil"); _logger.LogTrace("AutoGong loot silver"); }
                                    _waitingForTimers = true; // Wait for AT/AC reset before next cycle
                                    _logger.LogDebug("AutoGong waiting for timers reset after clearing aggressive mobs");
                                }
                            }
                        }
                    }
                }
            }
            _lastAutoGongEnabled = feats.AutoGong;
            // ---------- End Auto Gong ----------

            // Auto heal (enhanced with warning level support)
            if (feats.AutoHeal && th.AutoHealHpPercent > 0 && _stats.MaxHp > 0)
            {
                var hpPercent = _hpPct;
                bool shouldHeal = false;
                
                // Check if we should heal due to warning level (and timers are ready)
                if (_waitingForHealTimers && _stats.At == 0 && _stats.Ac == 0)
                {
                    shouldHeal = true;
                }
                // Or normal auto-heal conditions
                else if (hpPercent <= th.AutoHealHpPercent && _stats.Ac == 0 && _stats.At == 0)
                {
                    shouldHeal = true;
                }
                
                if (shouldHeal && (now - _lastHeal).TotalSeconds >= 5)
                {
                    var deficit = _stats.MaxHp - _stats.Hp;
                    string desired = deficit < 250 ? "minheal" : deficit < 500 ? "superheal" : "tolife";
                    var spell = _profile.Spells.FirstOrDefault(s => s.Nick.Equals(desired, StringComparison.OrdinalIgnoreCase));
                    var target = GetCharacterName()?.Split(" ")[0];
                    if (spell != null && target != null && _stats.Mp >= spell.Mana)
                    {
                        _client.SendCommand($"cast {spell.Nick} {target}");
                        _lastHeal = now;
                        _logger.LogTrace("AutoHeal cast {spell} (deficit={def}, hp%={pct})", spell.Nick, deficit, hpPercent);
                    }
                }
            }
        }
        catch { }
    }

    private SpellInfo? SelectBestShieldSpell()
    {
        // priority order
        var order = new[] { "aegis", "gshield", "shield", "paura" };
        foreach (var o in order)
        {
            var s = _profile.Spells.FirstOrDefault(sp => sp.Nick.Equals(o, StringComparison.OrdinalIgnoreCase));
            if (s != null && _stats.Mp >= s.Mana) return s;
        }
        return null;
    }

    private string? GetCharacterName()
    {
        if (string.IsNullOrWhiteSpace(_selectedUser)) return _profile.Player.Name.Length > 0 ? _profile.Player.Name : null;
        var name = _charStore.GetLastCharacter(_selectedUser);
        if (!string.IsNullOrWhiteSpace(name)) return name;
        return _profile.Player.Name.Length > 0 ? _profile.Player.Name : null;
    }

    public void Dispose()
    {
        try { _timer?.Dispose(); } catch { }
        _stats.Updated -= OnStatsUpdated;
        _client.LineReceived -= OnLine;
        _combat.MonsterTargeted -= OnMonsterTargeted;
        _combat.MonsterDeath -= OnMonsterDeath;
    }

    // Simple script parser supporting {ENTER} and {WAIT:ms}
    private async Task ExecuteScriptAsync(string script)
    {
        if (string.IsNullOrEmpty(script)) return;
        var buffer = new System.Text.StringBuilder();
        for (int i = 0; i < script.Length; i++)
        {
            char ch = script[i];
            if (ch == '{')
            {
                int end = script.IndexOf('}', i + 1);
                if (end == -1)
                {
                    buffer.Append(ch); // treat as literal
                    continue;
                }
                var token = script.Substring(i + 1, end - i - 1).Trim();
                await FlushBufferAsync(buffer);
                if (string.Equals(token, "ENTER", StringComparison.OrdinalIgnoreCase))
                {
                    _client.SendCommand(string.Empty);
                }
                else if (string.Equals(token, "DISCONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _client.StopAsync();
                        _logger.LogInformation("Script executed disconnect command");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Script disconnect failed");
                    }
                }
                else if (token.StartsWith("WAIT:", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(token.Substring(5), out var ms) && ms > 0 && ms < 60000)
                    {
                        await Task.Delay(ms);
                    }
                }
                i = end; // advance past }
            }
            else
            {
                buffer.Append(ch);
            }
        }
        await FlushBufferAsync(buffer);
    }

    private Task FlushBufferAsync(System.Text.StringBuilder sb)
    {
        if (sb.Length > 0)
        {
            _client.SendCommand(sb.ToString());
            sb.Clear();
        }
        return Task.CompletedTask;
    }

    private void HandleCriticalHealth(string action)
    {
        _logger.LogWarning("Critical health reached! Action: {action}", action);
        
        switch (action.ToLowerInvariant())
        {
            case "disconnect":
                try
                {
                    _ = _client.StopAsync();
                    _logger.LogWarning("Disconnected due to critical health");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to disconnect on critical health");
                }
                break;
            case "stop":
                _client.SendCommand("stop");
                _inGongCycle = false;
                _waitingForTimers = false;
                _waitingForHealTimers = false;
                break;
            default:
                if (action.StartsWith("script:", StringComparison.OrdinalIgnoreCase))
                {
                    var script = action.Substring(7);
                    _ = ExecuteScriptAsync(script);
                }
                break;
        }
    }

    private void OnMonsterTargeted(string monsterName)
    {
        _logger.LogInformation("Monster '{monster}' is now targeted for combat", monsterName);
    }

    private void OnMonsterDeath(string deathMessage)
    {
        // Extract monster names from death message and remove from attack tracking
        // This allows attacking new monsters that spawn with the same name
        try
        {
            // Simple approach: clear the entire list when any monster dies
            // This ensures we can attack new spawns immediately
            if (_attackedMonsters.Count > 0)
            {
                _attackedMonsters.Clear();
                _logger.LogTrace("Cleared attacked monsters due to monster death");
            }
        }
        catch { }
    }
}
