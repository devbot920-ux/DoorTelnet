# Warning Heal AutoAttack Restart Fix

## Issue
When fighting with AutoAttack enabled (independent of AutoGong), the system would stop attacking when reaching the warning heal percentage, but would not restart attacking after the healing was completed and timers were ready.

## Root Cause
The warning heal logic was only implemented for AutoGong in the `EvaluateAutomation` method. Independent AutoAttack had no awareness of the warning heal state (`_waitingForHealTimers`), so it couldn't properly pause during healing or resume afterward.

### Code Flow Problem:
1. **Combat starts** ? AutoAttack begins attacking aggressive monsters
2. **HP drops to warning level** ? AutoGong stops (if enabled), but AutoAttack logic doesn't check warning heal state
3. **Healing completes** ? `_waitingForHealTimers` gets reset, but AutoAttack never checks this flag
4. **AutoAttack never resumes** ? Player must manually restart combat

## Solution Implemented

### **Restructured Warning Heal Logic**
**File:** `DoorTelnet.Wpf/Services/AutomationFeatureService.cs`

#### 1. **Unified Warning Heal State Management**
```csharp
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
```

#### 2. **Independent AutoAttack Section**
```csharp
// ---------- Independent AutoAttack (when AutoGong is disabled) ----------
// This provides attack functionality separate from gong automation
if (feats.AutoAttack && !feats.AutoGong && _stats.MaxHp > 0)
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
                AttackAggressiveMonsters("AutoAttack-Independent");
            }
        }
    }
    else if (hpPercent < th.GongMinHpPercent)
    {
        _logger.LogDebug("AutoAttack paused - HP {hp}% below threshold {thresh}%", hpPercent, th.GongMinHpPercent);
    }
}
```

#### 3. **Updated OnLine AutoAttack Check**
```csharp
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
```

#### 4. **Enhanced OnMonsterBecameAggressive**
```csharp
else if (feats.AutoAttack && !feats.AutoGong && _stats.MaxHp > 0)
{
    var hpPercent = _hpPct;
    var th = _profile.Thresholds;
    
    // Independent AutoAttack mode - respect warning heal state
    if (hpPercent >= th.GongMinHpPercent && !_waitingForHealTimers)
    {
        _logger.LogDebug("AutoAttack triggered for aggressive monster '{monster}'", monsterName);
        AttackAggressiveMonsters("AutoAttack-Immediate");
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
```

## How The Fix Works

### **Before Fix:**
1. ? AutoAttack starts attacking when monsters become aggressive
2. ? HP drops to warning level ? AutoAttack doesn't stop
3. ? OR AutoAttack stops but never checks heal timer completion
4. ? AutoAttack never resumes after healing
5. ? Player must manually restart combat

### **After Fix:**
1. ? AutoAttack starts attacking when monsters become aggressive
2. ? HP drops to warning level ? AutoAttack stops and sends "stop" command
3. ? `_waitingForHealTimers = true` ? All attack logic pauses
4. ? Healing completes ? AutoHeal casts heal spells
5. ? AT/AC timers reach 0 ? `_waitingForHealTimers = false`
6. ? AutoAttack automatically resumes attacking aggressive monsters

## Technical Benefits

### **Unified Warning Heal Logic:**
- **Both AutoGong and AutoAttack** now share the same warning heal state management
- **Consistent behavior** across all automation features
- **Proper timer coordination** between combat and healing
- **Clean state transitions** from combat ? healing ? combat

### **Independent AutoAttack Support:**
- **Works without AutoGong** - fully independent automation
- **Respects all HP thresholds** - GongMinHpPercent and WarningHealHpPercent
- **Automatic restart** after healing completion
- **Proper monster tracking** shared with AutoGong

### **Comprehensive State Checking:**
- **Main evaluation loop** checks warning heal state every 500ms
- **Line-by-line processing** respects healing state
- **Monster aggression events** check healing state before attacking
- **Consistent logging** shows exactly why automation pauses/resumes

## Combat Scenarios Fixed

### **Scenario 1: Independent AutoAttack with Warning Heal**
```
> AutoAttack enabled, AutoGong disabled
> Monster becomes aggressive ? AutoAttack starts
> HP drops to 70% (warning heal) ? AutoAttack stops
> AutoHeal casts heal ? HP restored
> AT/AC timers reach 0 ? AutoAttack resumes automatically ?
```

### **Scenario 2: AutoGong with Warning Heal (Still Works)**
```
> AutoGong enabled
> Gong rings ? Monster summoned
> HP drops to 70% (warning heal) ? AutoGong stops
> AutoHeal casts heal ? HP restored  
> AT/AC timers reach 0 ? AutoGong resumes automatically ?
```

### **Scenario 3: Combat Restart After Multiple Heals**
```
> Combat continues through multiple warning heal cycles
> Each heal cycle: Stop ? Heal ? Resume ?
> No manual intervention required ?
```

## Logging Improvements

### **Clear State Transitions:**
- `"AutoAttack stopped due to warning heal level - HP 65% below 70%"`
- `"AutoAttack cannot attack 'demon' - waiting for heal timers (HP: 68%)"`
- `"Heal timers ready - automation can resume (HP: 85%)"`
- `"AutoAttack triggered for aggressive monster 'demon'"`

### **Debug Information:**
- **Why attacks are paused** - HP threshold vs heal timers
- **When automation resumes** - exact HP and timer state
- **What triggered restart** - timer completion vs new aggressive monsters

## Files Modified
1. `DoorTelnet.Wpf/Services/AutomationFeatureService.cs` - Restructured warning heal logic for both AutoGong and AutoAttack

## Result
AutoAttack now properly pauses during warning heal periods and automatically resumes attacking when healing is complete and timers are ready. This provides seamless, hands-off combat automation that respects healing requirements while maintaining aggressive combat engagement.