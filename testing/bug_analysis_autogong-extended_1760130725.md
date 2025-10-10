# Bug Analysis: autogong-extended

Generated: 2025-10-10 16:16:02

## Root Cause

The failure is a state synchronization / parsing race between automation, the combat tracker, and room tracking after issuing an auto-attack toggle. Concretely:

- AutomationFeatureService issues "autoattack on" but does not optimistically flip its internal flag and/or does not reliably reconcile server confirmations. The UI/automation still shows autoAttack: false even after sending the command.
- The output parser either misses or does not propagate server messages that confirm auto-attack toggles, so ApplyAutoAttackConfirmed is never called.
- RoomTracker and CombatTracker get updated from parsed lines in different orders (or parser handlers are incomplete). RoomTracker clears its monsters when a "combat disengaged" style line arrives, but CombatTracker does not clear targetedMonster/inCombat (or is updated in the wrong order), leaving an inconsistent state: inCombat==true, targetedMonster=="orc" while RoomTracker.Monsters is empty.
- There is no short reconciliation/debounce: transient parsing ordering causes a stuck state instead of self-healing.
- TelnetClient.SendCommand may not expose a CommandSent event or return a Task that callers can use to optimistically update state and retry on timeout.

Symptoms: autoAttack remains false after sending "autoattack on", combat state stuck with no monsters, automation aborts intervention.

## Files to Check

- DoorTelnet.Core/Services/AutomationFeatureService.cs
  - Reason: This service should expose SetAutoAttackAsync, an optimistic local state update, ApplyAutoAttackConfirmed called by the parser, timeouts/retries, and an AutoAttackStateChanged event. It currently does not update optimistic state or handle confirmation/timeouts robustly.

- DoorTelnet.Core/Parsing/GameOutputParser.cs (or equivalent parser)
  - Reason: Parser needs regex handlers for server lines confirming auto-attack on/off and for combat disengaged messages. It must call AutomationFeatureService.ApplyAutoAttackConfirmed and CombatTracker.EndCombat appropriately. Missing or brittle patterns cause confirmations to be ignored.

- DoorTelnet.Core/Trackers/CombatTracker.cs
  - Reason: CombatTracker should react to explicit "combat end" parser events and to RoomTracker.Monsters changes. It needs an EndCombat method and a short debounce listener to clear targetedMonster/inCombat when RoomTracker shows zero monsters.

- DoorTelnet.Core/Trackers/RoomTracker.cs
  - Reason: RoomTracker must raise MonstersChanged events with count/timestamp when monsters go to zero. Ensure events are raised reliably so CombatTracker can reconcile.

- DoorTelnet.Core/Networking/TelnetClient.cs
  - Reason: SendCommand should write, flush, and expose a CommandSent event or return a Task that completes after write. AutomationFeatureService needs that to optimistically assume the command was dispatched and start confirm-timeouts.

- DoorTelnet.Core.Tests (unit tests)
  - Reason: Add tests simulating autoattack confirmation flows, missing confirmations, and parser ordering races (RoomTracker clears before CombatTracker receives combat end). Tests should validate optimistic state, confirmation application, retries, and combat/room reconciliation.

## GitHub Copilot Prompt
```
Repository: DoorTelnet (C# .NET 8 WPF)
Goal: Fix auto-attack toggle and combat/room state desync so automation reliably enables auto-attack and trackers stay consistent.

Background:
- AutomationFeatureService exposes a boolean AutoAttack used by automation and UI. Currently toggling auto-attack via telnet does not reliably flip this flag nor recover if parser confirmations are missed.
- Output parser sometimes updates RoomTracker and CombatTracker in different orders or misses confirmation lines. CombatTracker may remain InCombat==true with TargetedMonster while RoomTracker.MonsterCount==0.
- TelnetClient.SendCommand should guarantee the command is written/flushed and expose a hook so AutomationFeatureService can optimistically update state and start confirmation timeout / retry.

High-level tasks to implement (make code changes in the listed files and tests):
1) AutomationFeatureService.cs
- Implement SetAutoAttackAsync(bool enabled):
  - Immediately set a private optimistic flag _autoAttackOptimistic = enabled, raise AutoAttackStateChanged event/property-changed so UI/automation sees new value.
  - Call TelnetClient.SendCommand($"autoattack {(enabled ? "on" : "off")}") and await that Task (SendCommand should flush and complete when written).
  - Start a confirmation timeout (2.5s). If ApplyAutoAttackConfirmed(false/true) is not received confirming the state within the timeout, resend the command once and log a warning. If still no confirmation after another timeout, leave optimistic state but mark last-known-server-state unchanged (and log).
  - Use CancellationTokenSource to cancel timeouts if confirmation arrives.
- Implement ApplyAutoAttackConfirmed(bool confirmed):
  - Set authoritative _autoAttackServer = confirmed and set _autoAttackOptimistic = confirmed.
  - Raise AutoAttackStateChanged/PropertyChanged (single source of truth exposed via AutoAttack property).
  - Cancel any pending confirmation timers/retries.
- Expose:
  - public bool AutoAttack => _autoAttackOptimistic; (but maintain a private _autoAttackServer for last-confirmed)
  - public event Action<bool> AutoAttackStateChanged;
- Thread-safety:
  - Use lock(this) or a private object _stateLock for state transitions and CancellationTokenSource swaps.
- Logging:
  - Log optimistic set, confirmation received, timeout + retry, final failure.

2) TelnetClient.cs
- Make SendCommand(string cmd) return Task:
  - Write cmd + "\r\n" to the network stream and flush.
  - Ensure the Task completes after write/flush.
  - Raise an event CommandSent(string command) when the write completes.
  - Ensure writes are not suppressed while in combat; use a dedicated writer queue but complete the task after actual write.
  - Provide a simple safe signature:
      public Task SendCommandAsync(string cmd, CancellationToken ct = default)
      public event Action<string>? CommandSent;
- Add debug log when commands are sent.

3) GameOutputParser.cs (or relevant parser)
- Add regex handlers for auto-attack confirmations:
  - Patterns (RegexOptions.IgnoreCase):
    - @"\b(auto-?attack|auto attacking|autoattacking)\b.*\bon\b"
    - @"\b(you (begin|start) auto-?attacking|you begin auto-attacking|you start auto-attacking)\b"
      => call automationService.ApplyAutoAttackConfirmed(true)
    - @"\b(auto-?attack|auto attacking)\b.*\boff\b"
    - @"\b(you stop auto-?attacking|you cease auto-attacking)\b"
      => call automationService.ApplyAutoAttackConfirmed(false)
- Add regex handlers for combat end/disengaged lines:
  - Patterns:
    - @"combat disengaged"
    - @"you are no longer engaged"
    - @"you disengage"
    - @"combat ends"
      => call combatTracker.EndCombat("parser:disengaged")
- Ensure parser calls RoomTracker before/after these calls in a deterministic manner where possible; but do not rely on strict ordering — instead emit events that trackers can reconcile.

4) CombatTracker.cs
- Add EndCombat(string reason = null) method:
  - Cancel any combat timers, set InCombat=false, TargetedMonster=null, raise CombatStateChanged event.
- Subscribe to RoomTracker.MonstersChanged event:
  - If event.Count == 0 && InCombat == true && TargetedMonster != null:
    - Start a short debounce (250-500ms) using a CancellationTokenSource. After delay, if RoomTracker.MonsterCount == 0 still and InCombat==true, then call EndCombat("room-empty-reconcile").
  - Ensure debounce cancels if new monsters arrive or EndCombat is received via parser.
- Raise events when combat ends so other services (AutomationFeatureService, UI) can reconcile.

5) RoomTracker.cs
- When setting monsters list to an empty collection, raise MonstersChanged with Count==0 and timestamp.
- Ensure event payload includes Count and DateTime timestamp.
- Keep updates lightweight and thread-safe.

6) Logging / diagnostics
- Wherever state flips occur (AutomationFeatureService optimistic/confirmed, CombatTracker.EndCombat, RoomTracker.MonstersChanged), log debug lines containing:
    AutoAttack:{value}, Combat.InCombat:{value}, Combat.Target:{value}, Room.MonsterCount:{count}, timestamp.
- Add a debug diagnostic method that dumps the four fields whenever an auto-attack toggle fails to confirm.

7) Unit / Integration tests (DoorTelnet.Core.Tests)
- Test 1: SetAutoAttackAsync(true) -> simulate TelnetClient.SendCommandAsync completing -> simulate parser line "You begin auto-attacking" -> assert AutoAttack==true, server-confirmation updated, timer canceled.
- Test 2: SetAutoAttackAsync(true) -> no confirmation -> ensure optimistic AutoAttack==true, after timeout it retries once (assert CommandSent called twice) and logs warning; if still no confirmation, keep optimistic but last-confirmed remains unchanged and log.
- Test 3: Parser ordering race: feed parser lines where RoomTracker.Monsters is cleared first and Combat disengaged line comes later or not at all -> CombatTracker should clear TargetedMonster after debounce when RoomTracker.Count==0.
- Test 4: Parser sends explicit "Combat disengaged" -> assert CombatTracker.InCombat==false and TargetedMonster==null and RoomTracker.MonsterCount==0.
- Use small timeouts to keep tests fast (use injectable debounce/timeout durations in ctor or via options).

Implementation hints / signatures:
- AutomationFeatureService:
  - private readonly object _stateLock = new();
  - private bool _autoAttackOptimistic;
  - private bool? _autoAttackServer; // null = unknown
  - private CancellationTokenSource? _confirmCts;
  - public bool AutoAttack => _autoAttackOptimistic;
  - public event Action<bool>? AutoAttackStateChanged;
  - public async Task SetAutoAttackAsync(bool enabled) { lock; set optimistic; raise; await telnetClient.SendCommandAsync(...); start confirm timeout; }
  - public void ApplyAutoAttackConfirmed(bool confirmed) { lock; set server; set optimistic; cancel cts; raise; }
- TelnetClient:
  - public event Action<string>? CommandSent;
  - public Task SendCommandAsync(string cmd, CancellationToken ct = default) { write flush; CommandSent?.Invoke(cmd); return Task.CompletedTask; }
- Parser:
  - use Regex with RegexOptions.Compiled|IgnoreCase
  - ensure it injects services via ctor and invokes ApplyAutoAttackConfirmed / CombatTracker.EndCombat
- CombatTracker:
  - public void EndCombat(string reason = null) { lock; cancel timers; _target = null; InCombat=false; raise event; }
  - Subscribe: roomTracker.MonstersChanged += OnMonstersChanged;
  - Debounce using Task.Delay with CancellationTokenSource injected per event.

Non-functional:
- Keep debounce durations configurable via feature flags or constructor parameters so tests can speed them up.
- Add debug logs via the existing logging facility (ILogger<T> if present).

Please modify these files:
- DoorTelnet.Core/Services/AutomationFeatureService.cs
- DoorTelnet.Core/Networking/TelnetClient.cs
- DoorTelnet.Core/Parsing/GameOutputParser.cs (or actual parser file)
- DoorTelnet.Core/Trackers/CombatTracker.cs
- DoorTelnet.Core/Trackers/RoomTracker.cs
- DoorTelnet.Core.Tests/AutomationFeatureTests.cs
- DoorTelnet.Core.Tests/CombatRoomReconcileTests.cs

Return minimal, well-typed changes (no breaking API changes) and add unit tests that simulate parser lines and telnet sends. Keep timeouts/debounce parameters injectable to facilitate deterministic tests.
```

