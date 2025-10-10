# Gong Ring During Healing Mode Fix

## Issue
The AutoGong system was occasionally ringing the gong just as the character entered healing mode, despite the warning heal logic being in place. This happened because of race conditions in the timing logic where the gong command was sent before the warning heal state was fully processed.

## Root Cause Analysis

### **Race Condition in AutoGong Logic**
The issue occurred in the `EvaluateAutomation` method where multiple checks happened in sequence:

1. **Warning heal logic** runs and sets `_waitingForHealTimers = true`
2. **AutoGong logic** runs and checks various conditions
3. **Gong command decision** made based on conditions from a few milliseconds ago
4. **HP drops further** between the checks, triggering warning heal
5. **"r g" command sent** despite now being in healing mode

### **Critical Timing Window**
```
Time: 0ms    - HP: 71% (above warning heal 70%)
Time: 100ms  - AutoGong logic evaluates, sees HP OK
Time: 200ms  - HP drops to 69% (now needs healing)
Time: 300ms  - Warning heal logic sets _waitingForHealTimers = true  
Time: 400ms  - AutoGong sends "r g" based on 100ms evaluation ?
```

### **Problematic Code Section**
```csharp
else if (!aggressivePresent && !_inGongCycle)
{
    // Start new cycle only if no aggressive monsters present
    if (timersReady && (now - _lastGongAction).TotalMilliseconds >= minGongIntervalMs)
    {
        _inGongCycle = true;
        _attackedMonsters.Clear();
        _lastGongAction = now;
        _client.SendCommand("r g"); // ? Could ring during healing mode
        _logger.LogInformation("AutoGong rung gong (r g) - starting new cycle");
    }
}
```

## Solution Implemented

### **Last-Second Warning Heal Checks**
**File:** `DoorTelnet.Wpf/Services/AutomationFeatureService.cs`

#### **1. Pre-Gong Safety Check**
```csharp
else if (!aggressivePresent && !_inGongCycle)
{
    // Start new cycle only if no aggressive monsters present
    if (timersReady && (now - _lastGongAction).TotalMilliseconds >= minGongIntervalMs)
    {
        // CRITICAL FIX: Double-check warning heal state before ringing gong
        // This prevents gong from ringing just as we enter healing mode
        var currentHpPercent = _hpPct;
        if (currentHpPercent <= th.WarningHealHpPercent && th.WarningHealHpPercent > 0)
        {
            _logger.LogInformation("AutoGong prevented - HP {hp}% at warning heal level {thresh}% (last-second check)", currentHpPercent, th.WarningHealHpPercent);
            if (!_waitingForHealTimers)
            {
                _waitingForHealTimers = true;
                _client.SendCommand("stop"); // Ensure we stop any ongoing actions
            }
            return; // Exit without ringing gong
        }
        
        _inGongCycle = true;
        _attackedMonsters.Clear();
        _lastGongAction = now;
        _client.SendCommand("r g");
        _logger.LogInformation("AutoGong rung gong (r g) - starting new cycle (HP: {hp}%)", currentHpPercent);
    }
}
```

#### **2. Combat Entry Safety Check**
```csharp
// If we're waiting for timers but aggressive monsters are present, reset immediately and attack
if (_waitingForTimers && aggressivePresent)
{
    // SAFETY CHECK: Don't enter combat if we're at warning heal level
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
        _inGongCycle = true; // Re-enter combat mode to attack the monsters
        _logger.LogInformation("AutoGong entering combat mode due to aggressive monsters - stopping timer wait");
    }
}
```

#### **3. Enhanced Monster Aggression Checks**
```csharp
private void OnMonsterBecameAggressive(string monsterName)
{
    // ... existing code ...
    
    // ENHANCED CHECK: Verify both GongMinHpPercent and WarningHealHpPercent
    if (hpPercent >= th.GongMinHpPercent && !_waitingForHealTimers)
    {
        // Additional safety check for warning heal level
        if (hpPercent <= th.WarningHealHpPercent && th.WarningHealHpPercent > 0)
        {
            _logger.LogInformation("AutoGong cannot attack '{monster}' - HP {hp}% at warning heal level {thresh}%", monsterName, hpPercent, th.WarningHealHpPercent);
            if (!_waitingForHealTimers)
            {
                _waitingForHealTimers = true;
                _client.SendCommand("stop");
            }
            return;
        }
        
        // ... continue with combat logic ...
    }
}
```

## How The Fix Works

### **Before Fix:**
```
Scenario: HP drops from 71% to 69% during gong evaluation
1. ? AutoGong evaluates at 71% HP ? Plans to ring gong
2. ? HP drops to 69% ? Warning heal activates
3. ? AutoGong still rings gong ? Enters combat at low HP
4. ? Player takes damage while healing needed
```

### **After Fix:**
```
Scenario: HP drops from 71% to 69% during gong evaluation  
1. ? AutoGong evaluates at 71% HP ? Plans to ring gong
2. ? HP drops to 69% ? Warning heal activates
3. ? Last-second check sees 69% HP ? Prevents gong
4. ? System enters healing mode ? Safe healing occurs
```

## Technical Benefits

### **Multi-Layer Safety:**
- **Primary warning heal logic** - Main system prevention
- **Pre-gong safety check** - Last-second verification before "r g"
- **Combat entry prevention** - Blocks combat mode during healing
- **Monster aggression safety** - Prevents reaction combat during healing

### **Race Condition Elimination:**
- **Real-time HP checks** before every gong command
- **Immediate healing state enforcement** when triggered
- **Stop command sending** to halt any ongoing actions
- **Early return logic** to prevent subsequent automation

### **Enhanced Logging:**
- **"Last-second check" messages** - Shows when prevention occurs
- **HP percentage logging** - Tracks exact HP when decisions made
- **Prevention reason tracking** - Clear indication why gong was stopped
- **Real-time status updates** - Shows current HP in gong log messages

## Safety Improvements

### **Immediate Response:**
- **Stop command sent** immediately when healing needed
- **State flags updated** instantly to prevent conflicts
- **Early exits** from automation logic when unsafe
- **Real-time HP evaluation** at decision points

### **Comprehensive Coverage:**
- **New gong cycles** protected by pre-ring checks
- **Combat entry** protected by warning heal verification
- **Monster reactions** protected by enhanced safety logic
- **Timer-based cycles** protected by state validation

### **Defensive Programming:**
- **Multiple check points** throughout the automation cycle
- **Redundant safety measures** to catch edge cases
- **Fail-safe defaults** that prioritize character safety
- **Comprehensive logging** for troubleshooting timing issues

## Test Scenarios

### **Scenario 1: HP Drop During Gong Planning**
```
Initial: HP 72%, AutoGong planning cycle
Step 1: HP drops to 68% (below 70% warning)
Step 2: Pre-gong check detects 68% HP
Result: ? Gong prevented, healing mode activated
```

### **Scenario 2: Monster Becomes Aggressive During Healing**
```
Initial: HP 68%, healing mode active
Step 1: Monster becomes aggressive
Step 2: OnMonsterBecameAggressive safety check
Result: ? Combat prevented, healing continues
```

### **Scenario 3: Timer Reset During Low HP**
```
Initial: HP 69%, waiting for timers
Step 1: Timers reset, aggressive monsters present
Step 2: Combat entry safety check detects low HP
Result: ? Combat entry prevented, healing prioritized
```

## Files Modified
1. **DoorTelnet.Wpf/Services/AutomationFeatureService.cs**
   - Enhanced `EvaluateAutomation` with pre-gong safety checks
   - Added combat entry prevention logic
   - Enhanced `OnMonsterBecameAggressive` with warning heal checks

## Result
The AutoGong system now has comprehensive protection against ringing the gong during healing mode. Multiple safety checks ensure that healing is prioritized over combat initiation, eliminating the race condition that caused occasional gong rings at inappropriate times. The system now provides safer, more reliable automation during critical health situations.