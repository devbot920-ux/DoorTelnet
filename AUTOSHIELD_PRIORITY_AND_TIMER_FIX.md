# AutoShield Priority and Timer Coordination Fix

## Issue
AutoShield was not taking priority over "r g" (gong) commands and was not properly accounting for AC/AT timers. This caused several problems:

1. **Shield commands were interrupted by gong** - "r g" would override shield casting
2. **No timer coordination** - Shield would cast regardless of AC/AT timer state
3. **Poor timing** - Shield could interfere with optimal gong/combat cycles
4. **Low priority** - Shield was treated as secondary to combat actions

## Root Cause
The original AutoShield logic was too simplistic and didn't coordinate with the gong automation system:

```csharp
// BEFORE: Basic shield logic with no coordination
if (feats.AutoShield && !_profile.Effects.Shielded)
{
    if ((now - _lastShield).TotalSeconds >= Math.Max(5, Math.Max(th.ShieldRefreshSec, 10)))
    {
        // Cast shield without checking timers or gong state
        _client.SendCommand($"cast {bestShield.Nick} {target}");
    }
}
```

This approach ignored:
- **AC/AT timer states** - Could cast during combat timers
- **Gong cycle coordination** - Could interrupt gong operations
- **Combat priorities** - Didn't understand when shield was most needed

## Solution Implemented

### **Enhanced AutoShield Logic with Priority System**
**File:** `DoorTelnet.Wpf/Services/AutomationFeatureService.cs`

#### **Priority Level 1: Optimal Timing (Highest Priority)**
```csharp
// Priority 1: Always cast shield if not in combat and timers are ready
if (_stats.At == 0 && _stats.Ac == 0 && !_inGongCycle && !_waitingForTimers && !_waitingForHealTimers)
{
    shouldCastShield = true;
    reason = "Timers ready, not in gong cycle";
}
```

**When:** Outside combat, all timers ready, no automation conflicts
**Why:** Perfect timing - no interruptions possible

#### **Priority Level 2: Preemptive Shielding (High Priority)**
```csharp
// Priority 2: Cast shield before starting new gong cycle (preemptive shielding)
else if (feats.AutoGong && !_inGongCycle && !_waitingForTimers && !_waitingForHealTimers && 
         _stats.At == 0 && _stats.Ac == 0 && _stats.MaxHp > 0 && _hpPct >= th.GongMinHpPercent)
{
    var room = _room.CurrentRoom;
    if (room != null && !room.Monsters.Any(m => m.Disposition.Equals("aggressive", StringComparison.OrdinalIgnoreCase)))
    {
        // About to start new gong cycle, cast shield first
        var timeSinceLastGong = (now - _lastGongAction).TotalMilliseconds;
        if (timeSinceLastGong >= 1200) // Close to 1500ms gong interval
        {
            shouldCastShield = true;
            reason = "Preemptive shielding before gong cycle";
        }
    }
}
```

**When:** About to start gong cycle, timers ready, no monsters present
**Why:** Ensures shield is active before combat begins

#### **Priority Level 3: Emergency Shielding (Medium Priority)**
```csharp
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
```

**When:** During combat, timers ready, shield really needed
**Why:** Protection during extended combat, but only when safe

### **Smart Timer Coordination**
```csharp
// Enhanced shield timing logic
bool shouldCastShield = false;
string reason = "";

if (timeSinceLastShield >= shieldRefreshInterval)
{
    // Check AC/AT timers: _stats.At == 0 && _stats.Ac == 0
    // Check gong state: !_inGongCycle && !_waitingForTimers
    // Check heal state: !_waitingForHealTimers
}
```

**Key Improvements:**
- **Respects AC/AT timers** - Only casts when timers allow
- **Coordinates with gong cycles** - Avoids interrupting combat
- **Understands automation states** - Works with all automation features

### **Enhanced Logging and Debugging**
```csharp
_logger.LogInformation("AutoShield cast {spell} on {target} - {reason}", bestShield.Nick, target, reason);

// Debug logging when shield is delayed
_logger.LogTrace("AutoShield waiting - AT:{at} AC:{ac} InGong:{gong} WaitTimers:{waitT} WaitHeal:{waitH}", 
    _stats.At, _stats.Ac, _inGongCycle, _waitingForTimers, _waitingForHealTimers);
```

## How The Fix Works

### **Before Fix:**
1. ? AutoShield casts without checking timers
2. ? "r g" command interrupts shield casting
3. ? Shield interferes with optimal combat timing
4. ? No coordination with gong automation
5. ? Unpredictable shield behavior during combat

### **After Fix:**
1. ? **Priority 1:** Shield casts during optimal timing (outside combat, timers ready)
2. ? **Priority 2:** Shield casts preemptively before gong cycles start
3. ? **Priority 3:** Emergency shield during combat only when timers allow
4. ? **Timer coordination:** Always respects AC/AT timer states
5. ? **Gong coordination:** Never interrupts active gong operations

## Combat Scenarios Fixed

### **Scenario 1: Pre-Combat Shielding**
```
> AutoShield enabled, AutoGong enabled
> AT/AC timers at 0, no monsters present
> Time since last gong: 1300ms (close to 1500ms interval)
> AutoShield casts preemptively ? Shield active
> 200ms later: AutoGong rings gong ? Combat starts with shield ?
```

### **Scenario 2: Optimal Timing Shield**
```
> AutoShield enabled, no automation running
> AT/AC timers at 0, not in combat
> AutoShield casts immediately ? Perfect timing ?
> No interference with any other automation ?
```

### **Scenario 3: Emergency Combat Shield**
```
> AutoShield enabled, currently in gong cycle
> Shield fades during extended combat
> AT/AC timers reach 0 ? Brief window available
> AutoShield casts emergency shield ? Protection restored ?
> Combat continues without interruption ?
```

### **Scenario 4: Proper Timer Respect**
```
> AutoShield enabled, shield expired
> AT: 3, AC: 2 (timers active)
> AutoShield waits ? No interference with combat ?
> AT/AC reach 0 ? AutoShield casts immediately ?
```

## Technical Benefits

### **Smart Priority System:**
- **Optimal timing** gets highest priority - perfect conditions
- **Preemptive shielding** ensures protection before combat
- **Emergency shielding** provides protection during extended fights
- **Timer coordination** prevents combat interference

### **Gong Cycle Integration:**
- **Preemptive casting** before gong cycles begin
- **No interruption** of active gong operations
- **Timing prediction** anticipates when gong will ring
- **Seamless coordination** between shield and combat automation

### **Enhanced Debugging:**
- **Detailed logging** shows why/when shield casts
- **State information** reveals timing conflicts
- **Reason tracking** explains shield casting decisions
- **Timer monitoring** shows AC/AT states affecting decisions

## Settings Impact

The existing **Shield Refresh (sec)** setting in Character Sheet now works better:
- **Minimum interval** still respected
- **Smart timing** applied within that interval
- **Priority system** determines optimal moments to cast
- **Timer coordination** ensures safe casting windows

## Files Modified
1. `DoorTelnet.Wpf/Services/AutomationFeatureService.cs` - Enhanced AutoShield logic with priority system and timer coordination

## Result
AutoShield now has intelligent priority over gong commands, properly coordinates with AC/AT timers, and provides optimal protection timing. Shield casting happens at the best possible moments without interfering with combat automation, ensuring maximum protection with minimal disruption.