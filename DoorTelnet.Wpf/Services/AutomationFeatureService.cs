using System.Text.RegularExpressions;
using DoorTelnet.Core.Automation;
using DoorTelnet.Core.Player;
using DoorTelnet.Core.Telnet;
using DoorTelnet.Core.World; // added for RoomTracker
using DoorTelnet.Core.Combat; // added for CombatTracker
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Wpf.Services;

/// <summary>
/// Lightweight automation layer for character feature flags (auto shield / gong / heal / pickup gold & silver / attack)
/// Evaluates periodically instead of only on stats update so that features keep working even when stats lines pause.
/// 
/// AUTO-GONG BEHAVIOR:
/// - Auto-gong enables auto-attack automatically
/// - Gong only rings when: no AC/AT timers AND no aggressive monsters in room
/// - Auto-attack handles all monsters (including summoned ones)
/// - Both features understand that AC=0 AND AT=0 = not in combat
/// </summary>
public class AutomationFeatureService : IDisposable
{
    private readonly StatsTracker _stats;
    private readonly PlayerProfile _profile;
    private readonly TelnetClient _client;
    private readonly RoomTracker _room; // inject room tracker to mirror CLI auto gong logic
    private readonly CombatTracker _combat; // inject combat tracker for targeting logic
    private readonly CharacterProfileStore _charStore; // NEW for obtaining character name
    private readonly NavigationFeatureService? _navigationService; // NEW for travel gold pickup
    private readonly ILogger<AutomationFeatureService> _logger;
    private DateTime _lastShield = DateTime.MinValue;
    private DateTime _lastHeal = DateTime.MinValue;
    private DateTime _lastRoomEntry = DateTime.MinValue; // NEW for tracking room entry cooldown
    private readonly Dictionary<string, DateTime> _roomGoldPickupAttempts = new(); // NEW for tracking gold pickup attempts per room
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
    private readonly Regex _shieldCastRegex = new(@"(magical shield surrounds you|You are surrounded by a magical shield|You are shielded\.|You imbue the power of the Aegis onto)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly Regex _shieldFadeRegex = new(@"(shield fades|magical shield shatters|shield disipated|shield dissipated|Your magical shield shimmers and dissapears!)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private bool _lastAutoGongEnabled;
    private string? _selectedUser; // future: could come from credentials dialog

    public AutomationFeatureService(StatsTracker stats, PlayerProfile profile, TelnetClient client, RoomTracker room, CombatTracker combat, CharacterProfileStore charStore, ILogger<AutomationFeatureService> logger, NavigationFeatureService? navigationService = null)
    {
        _stats = stats; _profile = profile; _client = client; _room = room; _combat = combat; _charStore = charStore; _navigationService = navigationService; _logger = logger;
        _stats.Updated += OnStatsUpdated; // update HP snapshot quickly
        _client.LineReceived += OnLine;
        _profile.Updated += () => EvaluateAutomation(true);
        _room.RoomChanged += OnRoomChanged; // NEW for tracking room entry
        _timer = new Timer(_ => EvaluateAutomation(false), null, 2000, 500); // Reduced from 1000ms to 500ms for more responsive automation
        _lastAutoGongEnabled = profile.Features.AutoGong;
        
        // Subscribe to combat targeting events
        _combat.MonsterTargeted += OnMonsterTargeted;
        _combat.MonsterDeath += OnMonsterDeath;
        _combat.MonsterBecameAggressive += OnMonsterBecameAggressive;
        
        _logger.LogInformation("AutomationFeatureService initialized with 500ms evaluation interval");
    }

    private void RaiseProfileUpdated()
    {
        try
        {
            var method = _profile.GetType().GetMethod("RaiseUpdated", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            method?.Invoke(_profile, null);
        }
        catch { }
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
            
            // AUTO-ENABLE AutoAttack when AutoGong is enabled
            // This ensures monsters are attacked after summoning
            if (feats.AutoGong && !feats.AutoAttack)
            {
                _profile.Features.AutoAttack = true;
                RaiseProfileUpdated();
                _logger.LogInformation("AutoAttack automatically enabled by AutoGong");
            }
            
            // NEW: Detect when player can't afford gong and auto-disable AutoGong
            if (line.Contains("You can't afford to ring the gong", StringComparison.OrdinalIgnoreCase))
            {
                if (feats.AutoGong)
                {
                    _profile.Features.AutoGong = false;
                    _inGongCycle = false;
                    _waitingForTimers = false;
                    _waitingForHealTimers = false;
                    RaiseProfileUpdated();
                    _logger.LogWarning("AutoGong automatically disabled - insufficient funds to ring gong");
                }
            }
            
            // NEW: Detect summoned monsters and ensure they are immediately marked as aggressive
            if (line.Contains("summoned for combat", StringComparison.OrdinalIgnoreCase))
            {
                var summonMatch = Regex.Match(line, @"A\s+([a-zA-Z\s]+?)\s+is\s+summoned\s+for\s+combat", RegexOptions.IgnoreCase);
                if (summonMatch.Success)
                {
                    var monsterName = summonMatch.Groups[1].Value.Trim();
                    _logger.LogInformation("Detected summoned monster: '{monster}' - ensuring it's tracked and marked aggressive", monsterName);
                    
                    // Ensure the monster is tracked in combat system
                    _combat.EnsureMonsterTracked(monsterName);
                    
                    // Mark it as aggressive in the room immediately
                    _room.UpdateMonsterDisposition(monsterName, "aggressive");
                    
                    // If AutoGong is enabled, auto-attack will handle the monster
                    if (feats.AutoGong && _stats.MaxHp > 0)
                    {
                        var hpPercent = _hpPct;
                        var th = _profile.Thresholds;
                        
                        if (hpPercent >= th.GongMinHpPercent)
                        {
                            _waitingForTimers = false;
                            _inGongCycle = true;
                            _logger.LogInformation("AutoGong entering combat mode - AutoAttack will handle summoned monster '{monster}'", monsterName);
                        }
                    }
                }
            }
            
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
            // IMPORTANT: Check for warning heal state to avoid attacking when healing is needed
            if (feats.AutoAttack && !feats.AutoGong) // Only do independent AutoAttack if AutoGong is disabled
            {
                // Don't attack if we're waiting for heal timers due to warning heal level
                if (!_waitingForHealTimers)
                {
                    AttackAggressiveMonsters("AutoAttack");
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Unified method to attack aggressive monsters, used by both AutoGong and AutoAttack
    /// COMBAT DETECTION: AC=0 AND AT=0 means not in combat
    /// </summary>
    private void AttackAggressiveMonsters(string context)
    {
        var room = _room.CurrentRoom;
        if (room == null) return;

        // COMBAT DETECTION: Check if we're actually in combat based on timers
        bool inCombat = _stats.Ac > 0 || _stats.At > 0;
        
        var aggressiveMobs = room.Monsters
            .Where(m => m.Disposition.Equals("aggressive", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (aggressiveMobs.Count > 0)
        {
            // Reduced reset timeout for more responsive attacking of new spawns
            var now = DateTime.UtcNow;
            if ((now - _lastAttackReset).TotalSeconds >= 15)
            {
                _attackedMonsters.Clear();
                _lastAttackReset = now;
                _logger.LogTrace("{context} reset attacked monsters list after 15 seconds", context);
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
                    var combatStatus = inCombat ? " [IN COMBAT]" : " [NOT IN COMBAT]";
                    _logger.LogInformation("{context} attacking mob '{mob}' (letter '{letter}'){targetStatus}{combatStatus} (remaining targets: {count})", 
                        context, next.Name, letterLower, targetStatus, combatStatus, aggressiveMobs.Count(m => !_attackedMonsters.Contains(m.Name)));
                }
                else
                {
                    // If name starts with non letter, try full name attack (some servers support this)
                    _client.SendCommand($"a {next.Name.Split(' ').FirstOrDefault()}");
                    _attackedMonsters.Add(next.Name);
                    var targetStatus = targetedMonster != null && string.Equals(next.Name?.Replace(" (summoned)", ""), targetedMonster.MonsterName, StringComparison.OrdinalIgnoreCase) ? " [TARGETED]" : "";
                    var combatStatus = inCombat ? " [IN COMBAT]" : " [NOT IN COMBAT]";
                    _logger.LogInformation("{context} attacking mob '{mob}'{targetStatus}{combatStatus} via name fallback (remaining targets: {count})", 
                        context, next.Name, targetStatus, combatStatus, aggressiveMobs.Count(m => !_attackedMonsters.Contains(m.Name)));
                }
            }
            else
            {
                _logger.LogDebug("{context} - all {count} aggressive monsters already attacked", context, aggressiveMobs.Count);
            }
        }
        else
        {
            // No aggressive monsters - clear attack tracking for next wave
            if (_attackedMonsters.Count > 0)
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

            // AUTO-ENABLE AutoAttack when AutoGong is enabled
            if (feats.AutoGong && !feats.AutoAttack)
            {
                _profile.Features.AutoAttack = true;
                RaiseProfileUpdated();
                _logger.LogInformation("AutoAttack automatically enabled by AutoGong (in evaluation loop)");
            }

            // Check critical health first
            if (_stats.MaxHp > 0 && _hpPct <= th.CriticalHpPercent && th.CriticalHpPercent > 0)
            {
                HandleCriticalHealth(th.CriticalAction);
                return; // Exit early on critical health
            }

            // Travel Gold Pickup - NEW feature for picking up gold during navigation
            if (feats.PickupGold)
            {
                TryPickupTravelGold();
            }

            // ---------- AUTO SHIELD (Enhanced with timer coordination) ----------
            // Auto shield should have high priority but respect gong cycles and timer states
            if (feats.AutoShield && !_profile.Effects.Shielded)
            {
                var timeSinceLastShield = (now - _lastShield).TotalSeconds;
                var shieldRefreshInterval = Math.Max(5, Math.Max(th.ShieldRefreshSec, 10));
                
                // Enhanced shield timing logic
                bool shouldCastShield = false;
                string reason = "";
                
                if (timeSinceLastShield >= shieldRefreshInterval)
                {
                    // Priority 1: Always cast shield if not in combat and timers are ready
                    if (_stats.At == 0 && _stats.Ac == 0 && !_inGongCycle && !_waitingForTimers && !_waitingForHealTimers)
                    {
                        shouldCastShield = true;
                        reason = "Timers ready, not in gong cycle";
                    }
                    // Priority 2: Cast shield before starting new gong cycle (preemptive shielding)
                    else if (feats.AutoGong && !_inGongCycle && !_waitingForTimers && !_waitingForHealTimers && 
                             _stats.At == 0 && _stats.Ac == 0 && _stats.MaxHp > 0 && _hpPct >= th.GongMinHpPercent)
                    {
                        var room = _room.CurrentRoom;
                        if (room != null && !room.Monsters.Any(m => m.Disposition.Equals("aggressive", StringComparison.OrdinalIgnoreCase)))
                        {
                            // About to start new gong cycle, cast shield first
                            // Check if we're close to starting a gong (within the next few cycles)
                            var timeSinceLastGong = (now - _lastGongAction).TotalMilliseconds;
                            if (timeSinceLastGong >= 1200) // Close to 1500ms gong interval
                            {
                                shouldCastShield = true;
                                reason = "Preemptive shielding before gong cycle";
                            }
                        }
                    }
                    // Priority 3: Emergency shielding during combat if timers allow brief interruption
                    else if ((_inGongCycle || (feats.AutoAttack && !feats.AutoGong)) && _stats.At == 0 && _stats.Ac == 0)
                    {
                        // Only do emergency shield if we haven't shielded recently and it's really needed
                        if (timeSinceLastShield >= 30) // Emergency threshold - longer interval during combat
                        {
                            shouldCastShield = true;
                            reason = "Emergency shielding during combat (timers ready)";
                        }
                    }
                }
                
                if (shouldCastShield)
                {
                    var bestShield = SelectBestShieldSpell();
                    if (bestShield != null)
                    {
                        var target = GetCharacterName()?.Split(" ")[0]; // Use only first name
                        if (string.IsNullOrWhiteSpace(target))
                        {
                            _logger.LogDebug("AutoShield skipped - no character name yet");
                        }
                        else
                        {
                            var cmd = $"cast {bestShield.Nick} {target}";
                            _client.SendCommand(cmd);
                            _lastShield = now;
                            _logger.LogInformation("AutoShield cast {spell} on {target} - {reason}", bestShield.Nick, target, reason);
                        }
                    }
                    else
                    {
                        var shield = _profile.Player.Shields.FirstOrDefault();
                        if (!string.IsNullOrWhiteSpace(shield))
                        {
                            _client.SendCommand(shield);
                            _lastShield = now;
                            _logger.LogInformation("AutoShield (fallback) '{spell}' - {reason}", shield, reason);
                        }
                    }
                }
                else if (feats.AutoShield && !_profile.Effects.Shielded && timeSinceLastShield >= shieldRefreshInterval)
                {
                    // Log why we're not shielding for debugging
                    _logger.LogTrace("AutoShield waiting - AT:{at} AC:{ac} InGong:{gong} WaitTimers:{waitT} WaitHeal:{waitH}", 
                        _stats.At, _stats.Ac, _inGongCycle, _waitingForTimers, _waitingForHealTimers);
                }
            }
            // ---------- End AUTO SHIELD ----------

            // WARNING HEAL LOGIC - Shared by both AutoGong and AutoAttack
            // This must be evaluated before AutoGong and AutoAttack logic
            if (_stats.MaxHp > 0)
            {
                var hpPercent = _hpPct;
                
                // Check warning heal level - stop automation and wait for heal timers
                if (hpPercent <= th.WarningHealHpPercent && th.WarningHealHpPercent > 0 && !_waitingForHealTimers)
                {
                    if (_inGongCycle || (feats.AutoAttack && !feats.AutoGong))
                    {
                        _client.SendCommand("stop");
                        if (_inGongCycle)
                        {
                            _inGongCycle = false;
                            _logger.LogInformation("AutoGong stopped due to warning heal level - HP {hp}% below {thresh}%", hpPercent, th.WarningHealHpPercent);
                        }
                        if (feats.AutoAttack && !feats.AutoGong)
                        {
                            _logger.LogInformation("AutoAttack stopped due to warning heal level - HP {hp}% below {thresh}%", hpPercent, th.WarningHealHpPercent);
                        }
                        _waitingForHealTimers = true;
                    }
                }
                
                // If waiting for heal timers, check if we can resume
                if (_waitingForHealTimers)
                {
                    if (_stats.At == 0 && _stats.Ac == 0)
                    {
                        _waitingForHealTimers = false;
                        _logger.LogInformation("Heal timers ready - automation can resume (HP: {hp}%)", hpPercent);
                    }
                }
            }

            // ---------- Auto Gong (rings gong only when no timers and no aggressive monsters) ----------
            if (feats.AutoGong)
            {
                // On enable edge, reset state so we can begin fresh
                if (!_lastAutoGongEnabled)
                {
                    _inGongCycle = false;
                    _waitingForTimers = false;
                    _waitingForHealTimers = false;
                    _attackedMonsters.Clear();
                    _logger.LogDebug("AutoGong enabled - resetting all state");
                }

                // Need valid stats & HP threshold
                if (_stats.MaxHp > 0)
                {
                    var hpPercent = _hpPct;
                    
                    if (hpPercent < th.GongMinHpPercent)
                    {
                        if (_inGongCycle || _waitingForTimers)
                        {
                            _inGongCycle = false; 
                            _waitingForTimers = false;
                            _logger.LogDebug("AutoGong paused - HP {hp}% below threshold {thresh}%", hpPercent, th.GongMinHpPercent);
                        }
                    }
                    else if (!_waitingForHealTimers)
                    {
                        // COMBAT DETECTION: timersReady = true when AC=0 AND AT=0 (not in combat)
                        bool timersReady = _stats.At == 0 && _stats.Ac == 0;
                        var room = _room.CurrentRoom;
                        
                        if (room != null)
                        {
                            const int minGongIntervalMs = 1500;
                            var aggressivePresent = room.Monsters.Any(m => m.Disposition.Equals("aggressive", StringComparison.OrdinalIgnoreCase));

                            // If we're waiting for timers but aggressive monsters are present, enter combat mode
                            if (_waitingForTimers && aggressivePresent)
                            {
                                var currentHpPercent = _hpPct;
                                if (currentHpPercent <= th.WarningHealHpPercent && th.WarningHealHpPercent > 0)
                                {
                                    _logger.LogInformation("AutoGong prevented combat entry - HP {hp}% at warning heal level {thresh}%", currentHpPercent, th.WarningHealHpPercent);
                                    if (!_waitingForHealTimers)
                                    {
                                        _waitingForHealTimers = true;
                                        _client.SendCommand("stop");
                                    }
                                }
                                else
                                {
                                    _waitingForTimers = false;
                                    _inGongCycle = true;
                                    _logger.LogInformation("AutoGong entering combat mode - AutoAttack will handle aggressive monsters");
                                }
                            }
                            // Normal timer reset when no aggressive monsters
                            else if (!aggressivePresent && _waitingForTimers)
                            {
                                if (timersReady)
                                {
                                    _waitingForTimers = false;
                                    _inGongCycle = false;
                                    _logger.LogTrace("AutoGong timers reset - ready for next cycle (AC={ac} AT={at})", _stats.Ac, _stats.At);
                                }
                            }
                            // MODIFIED: Ring gong only when no aggressive monsters AND timers ready
                            else if (!aggressivePresent && !_inGongCycle && timersReady)
                            {
                                if ((now - _lastGongAction).TotalMilliseconds >= minGongIntervalMs)
                                {
                                    var currentHpPercent = _hpPct;
                                    if (currentHpPercent <= th.WarningHealHpPercent && th.WarningHealHpPercent > 0)
                                    {
                                        _logger.LogInformation("AutoGong prevented - HP {hp}% at warning heal level {thresh}%", currentHpPercent, th.WarningHealHpPercent);
                                        if (!_waitingForHealTimers)
                                        {
                                            _waitingForHealTimers = true;
                                            _client.SendCommand("stop");
                                        }
                                        return;
                                    }
                                    
                                    _inGongCycle = true;
                                    _attackedMonsters.Clear();
                                    _lastGongAction = now;
                                    _client.SendCommand("r g");
                                    _logger.LogInformation("AutoGong rung gong (r g) - no aggressive monsters, timers ready (AC={ac} AT={at}, HP={hp}%)", 
                                        _stats.Ac, _stats.At, currentHpPercent);
                                }
                            }
                            // If in gong cycle but no aggressive monsters yet, wait
                            else if (_inGongCycle && !aggressivePresent)
                            {
                                if (!_waitingForTimers)
                                {
                                    // Loot after clearing monsters
                                    if (feats.PickupGold && room.Monsters.Count == 0) { _client.SendCommand("g gold"); _logger.LogTrace("AutoGong loot gold"); }
                                    if (feats.PickupSilver) { _client.SendCommand("g sil"); _logger.LogTrace("AutoGong loot silver"); }
                                    _waitingForTimers = true;
                                    _logger.LogDebug("AutoGong waiting for timers reset (AC={ac} AT={at})", _stats.Ac, _stats.At);
                                }
                            }
                        }
                        else
                        {
                            _logger.LogTrace("AutoGong: No room information available yet");
                        }
                    }
                }
                else
                {
                    _logger.LogTrace("AutoGong: No valid stats available yet (MaxHp={maxHp})", _stats.MaxHp);
                }
            }
            _lastAutoGongEnabled = feats.AutoGong;
            // ---------- End Auto Gong ----------

            // ---------- AutoAttack (handles all aggressive monsters) ----------
            // This runs when AutoAttack is enabled (either manually or via AutoGong)
            if (feats.AutoAttack && _stats.MaxHp > 0)
            {
                var hpPercent = _hpPct;
                
                // Only attack if HP is above minimum threshold and not waiting for heal timers
                if (hpPercent >= th.GongMinHpPercent && !_waitingForHealTimers)
                {
                    var room = _room.CurrentRoom;
                    if (room != null)
                    {
                        var aggressivePresent = room.Monsters.Any(m => m.Disposition.Equals("aggressive", StringComparison.OrdinalIgnoreCase));
                        if (aggressivePresent)
                        {
                            // Determine context based on whether AutoGong is enabled
                            var context = feats.AutoGong ? "AutoGong->AutoAttack" : "AutoAttack";
                            AttackAggressiveMonsters(context);
                        }
                    }
                }
                else if (hpPercent < th.GongMinHpPercent)
                {
                    _logger.LogDebug("AutoAttack paused - HP {hp}% below threshold {thresh}%", hpPercent, th.GongMinHpPercent);
                }
            }
            // ---------- End AutoAttack ----------

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
                    var target = GetCharacterName()?.Split(" ")[0]; // Use only first name
                    if (spell != null && target != null && _stats.Mp >= spell.Mana)
                    {
                        _client.SendCommand($"cast {spell.Nick} {target}");
                        _lastHeal = now;
                        _logger.LogTrace("AutoHeal cast {spell} on {target} (deficit={def}, hp%={pct})", spell.Nick, target, deficit, hpPercent);
                    }
                }
            }
        }
        catch { }
    }

    private SpellInfo? SelectBestShieldSpell()
    {
        // Aegis always takes priority if available and caster has sufficient mana
        var aegis = _profile.Spells.FirstOrDefault(sp => sp.Nick.Equals("aegis", StringComparison.OrdinalIgnoreCase));
        if (aegis != null && _stats.Mp >= aegis.Mana) 
        {
            _logger.LogTrace("AutoShield selected Aegis as best shield (always prioritized)");
            return aegis;
        }

        // Fallback priority order for other shields
        var order = new[] { "gshield", "shield", "paura" };
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
        _room.RoomChanged -= OnRoomChanged; // NEW cleanup
        _combat.MonsterTargeted -= OnMonsterTargeted;
        _combat.MonsterDeath -= OnMonsterDeath;
        _combat.MonsterBecameAggressive -= OnMonsterBecameAggressive;
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
                    // Stop all automation first
                    _inGongCycle = false;
                    _waitingForTimers = false;
                    _waitingForHealTimers = false;
                    
                    // Execute disconnect
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _client.StopAsync();
                            _logger.LogWarning("Successfully disconnected due to critical health");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to disconnect on critical health");
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to initiate disconnect on critical health");
                }
                break;
            case "stop":
                _client.SendCommand("stop");
                _inGongCycle = false;
                _waitingForTimers = false;
                _waitingForHealTimers = false;
                _logger.LogWarning("Stopped all actions due to critical health");
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

    private void OnMonsterBecameAggressive(string monsterName)
    {
        _logger.LogInformation("Monster '{monster}' became aggressive - AutoAttack will handle it", monsterName);
        
        var feats = _profile.Features;
        
        // Auto-attack will handle aggressive monsters (whether triggered by AutoGong or standalone)
        if (feats.AutoAttack && _stats.MaxHp > 0)
        {
            var hpPercent = _hpPct;
            var th = _profile.Thresholds;
            
            if (hpPercent >= th.GongMinHpPercent && !_waitingForHealTimers)
            {
                // Additional safety check for warning heal level
                if (hpPercent <= th.WarningHealHpPercent && th.WarningHealHpPercent > 0)
                {
                    _logger.LogInformation("AutoAttack cannot attack '{monster}' - HP {hp}% at warning heal level {thresh}%", monsterName, hpPercent, th.WarningHealHpPercent);
                    if (!_waitingForHealTimers)
                    {
                        _waitingForHealTimers = true;
                        _client.SendCommand("stop");
                    }
                    return;
                }
                
                // If AutoGong is enabled, ensure we're in combat mode
                if (feats.AutoGong)
                {
                    if (_waitingForTimers)
                    {
                        _waitingForTimers = false;
                        _inGongCycle = true;
                        _logger.LogInformation("AutoGong entering combat mode due to aggressive monster '{monster}'", monsterName);
                    }
                    else if (!_inGongCycle)
                    {
                        _inGongCycle = true;
                        _logger.LogInformation("AutoGong forced into combat mode due to aggressive monster '{monster}'", monsterName);
                    }
                }
                
                // Trigger immediate attack check
                var context = feats.AutoGong ? "AutoGong->AutoAttack-Immediate" : "AutoAttack-Immediate";
                AttackAggressiveMonsters(context);
            }
            else if (_waitingForHealTimers)
            {
                _logger.LogInformation("AutoAttack cannot attack '{monster}' - waiting for heal timers (HP: {hp}%)", monsterName, hpPercent);
            }
            else
            {
                _logger.LogDebug("AutoAttack cannot attack '{monster}' - HP {hp}% below threshold {thresh}%", monsterName, hpPercent, th.GongMinHpPercent);
            }
        }
        else
        {
            _logger.LogDebug("No auto-attack configured for aggressive monster '{monster}' (AutoAttack={attack})", monsterName, feats.AutoAttack);
        }
    }

    /// <summary>
    /// Handles room changes to track entry times for travel gold pickup
    /// </summary>
    private void OnRoomChanged(RoomState newRoom)
    {
        _lastRoomEntry = DateTime.UtcNow;
        _logger.LogTrace("Room changed to '{room}' - entry cooldown started", newRoom.Name);
    }

    /// <summary>
    /// Attempts to pickup gold during travel if conditions are met
    /// </summary>
    private void TryPickupTravelGold()
    {
        try
        {
            var room = _room.CurrentRoom;
            if (room == null) return;

            var now = DateTime.UtcNow;
            
            // Check room entry cooldown (3 seconds after entering room)
            var timeSinceRoomEntry = now - _lastRoomEntry;
            if (timeSinceRoomEntry.TotalSeconds < 3.0)
            {
                return; // Still in cooldown period
            }

            // Check if room has gold items to pickup
            var hasGold = room.Items.Any(item => 
                item.Contains("gold", StringComparison.OrdinalIgnoreCase) && 
                item.Contains("coin", StringComparison.OrdinalIgnoreCase));

            if (!hasGold) return;

            // Create unique room identifier for tracking pickup attempts
            var roomKey = $"{room.Name}_{room.Exits.Count}_{string.Join(",", room.Exits.OrderBy(x => x))}";
            
            // Check if we've already attempted to pickup gold in this room recently
            if (_roomGoldPickupAttempts.TryGetValue(roomKey, out var lastAttempt))
            {
                if ((now - lastAttempt).TotalSeconds < 10) // Avoid spam pickup attempts
                {
                    return;
                }
            }

            // Attempt gold pickup
            _client.SendCommand("get gold");
            _roomGoldPickupAttempts[roomKey] = now;
            
            // Determine context for logging
            var isNavigating = _navigationService != null && _room.CurrentRoom != null;
            var context = isNavigating ? "Travel" : "Room";
            _logger.LogInformation("{context} gold pickup attempted in '{room}' (cooldown: {cooldown:F1}s)", 
                context, room.Name, timeSinceRoomEntry.TotalSeconds);

            // Cleanup old pickup attempts (older than 5 minutes)
            var expiredKeys = _roomGoldPickupAttempts
                .Where(kvp => (now - kvp.Value).TotalMinutes > 5)
                .Select(kvp => kvp.Key)
                .ToList();
            
            foreach (var key in expiredKeys)
            {
                _roomGoldPickupAttempts.Remove(key);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in travel gold pickup");
        }
    }
}
