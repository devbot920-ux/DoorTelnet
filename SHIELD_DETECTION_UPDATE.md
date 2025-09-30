# Shield Detection and Aegis Prioritization Update

## Changes Made

### 1. New Shield Unshielding Event Detection
**Added detection for the new unshielding message:**
```
"Your magical shield shimmers and dissapears!"
```

**Updated Regex Pattern:**
```csharp
private readonly Regex _shieldFadeRegex = new(@"(shield fades|magical shield shatters|shield disipated|shield dissipated|Your magical shield shimmers and dissapears!)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
```

### 2. New Aegis Shield Cast Detection  
**Added detection for the Aegis cast message:**
```
"You imbue the power of the Aegis onto HealKind!"
```

**Updated Regex Pattern:**
```csharp
private readonly Regex _shieldCastRegex = new(@"(magical shield surrounds you|You are surrounded by a magical shield|You are shielded\.|You imbue the power of the Aegis onto)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
```

### 3. Aegis Shield Spell Prioritization
**Modified `SelectBestShieldSpell()` method to always prioritize Aegis:**

```csharp
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
```

## How It Works

### Shield State Detection Flow:
1. **Game sends shield message** ? AutomationFeatureService.OnLine() receives it
2. **Shield Cast Detection**: When any of these messages appear:
   - "magical shield surrounds you"
   - "You are surrounded by a magical shield" 
   - "You are shielded."
   - **"You imbue the power of the Aegis onto"** ? NEW
3. **Sets shielded state**: `_profile.SetShielded(true)`
4. **Shield Fade Detection**: When any of these messages appear:
   - "shield fades"
   - "magical shield shatters"
   - "shield disipated" / "shield dissipated"
   - **"Your magical shield shimmers and dissapears!"** ? NEW
5. **Clears shielded state**: `_profile.SetShielded(false)`

### Auto-Shield Casting Logic:
1. **Check conditions**: AutoShield enabled + not currently shielded + sufficient time elapsed
2. **Select best shield**: Aegis is now ALWAYS checked first and prioritized
3. **Cast command**: `cast aegis {charactername}` (or fallback spell if Aegis unavailable)
4. **Logging**: Enhanced to show when Aegis is specifically selected

### Priority Order (New):
1. **Aegis** ? Always first priority if available
2. gshield
3. shield  
4. paura

## Result
- **Unshielding detection** now works for the "shimmers and dissapears" message
- **Aegis casting detection** now works for the "You imbue the power" message  
- **Aegis prioritization** ensures the best shield is always used when available
- **Better logging** to track when Aegis is specifically selected

## Files Modified
- `DoorTelnet.Wpf/Services/AutomationFeatureService.cs` - Updated shield detection patterns and spell selection logic

The shield system now properly handles the new messages and ensures Aegis is always the preferred shield when available!