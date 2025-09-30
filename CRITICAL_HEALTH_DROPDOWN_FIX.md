# Critical Health Dropdown Fix

## Issue
The dropdown for critical health action selection was not working properly. Users could not select actions like "disconnect" when health dropped to critical levels.

## Root Cause
The ComboBox in the CharacterSheetDialog.xaml was using hardcoded `ComboBoxItem` elements instead of proper data binding, causing the `SelectedItem` binding to fail.

## Fix Applied

### 1. **Fixed ComboBox Data Binding**
**File:** `DoorTelnet.Wpf/Views/Dialogs/CharacterSheetDialog.xaml`

**Before (Broken):**
```xaml
<ComboBox SelectedItem="{Binding CriticalAction}" Width="90">
    <ComboBoxItem Content="stop"/>
    <ComboBoxItem Content="disconnect"/>
    <ComboBoxItem Content="script:{command}"/>
</ComboBox>
```

**After (Fixed):**
```xaml
<ComboBox SelectedItem="{Binding CriticalAction}" 
          ItemsSource="{Binding CriticalActionOptions}"
          Width="90" 
          IsEditable="True"
          ToolTip="Select or enter custom action: stop, disconnect, or script:{your_command}"/>
```

### 2. **Added Options Collection to ViewModel**
**File:** `DoorTelnet.Wpf/ViewModels/CharacterSheetViewModel.cs`

**Added:**
```csharp
// Critical Action options for dropdown
public ObservableCollection<string> CriticalActionOptions { get; } = new ObservableCollection<string>
{
    "stop",
    "disconnect", 
    "script:quit",
    "script:heal",
    "script:{DISCONNECT}",
    "script:{custom command}"
};
```

### 3. **Enhanced User Experience**
- **Made ComboBox editable** - Users can type custom script commands
- **Added helpful predefined options** - Common script commands available for selection
- **Improved tooltip** - Better guidance on how to use the feature

## How It Works Now

### **Critical Health Monitoring**
The AutomationFeatureService monitors HP percentage every 500ms:
```csharp
// Check critical health first
if (_stats.MaxHp > 0 && _hpPct <= th.CriticalHpPercent && th.CriticalHpPercent > 0)
{
    HandleCriticalHealth(th.CriticalAction);
    return; // Exit early on critical health
}
```

### **Action Execution**
When critical health is detected, the system executes the selected action:
```csharp
private void HandleCriticalHealth(string action)
{
    switch (action.ToLowerInvariant())
    {
        case "disconnect":
            // Stops all automation and disconnects from server
            await _client.StopAsync();
            break;
        case "stop":
            // Sends 'stop' command and halts automation
            _client.SendCommand("stop");
            break;
        default:
            if (action.StartsWith("script:", StringComparison.OrdinalIgnoreCase))
            {
                // Executes custom script command
                var script = action.Substring(7);
                _ = ExecuteScriptAsync(script);
            }
            break;
    }
}
```

## Available Actions

### **Predefined Options:**
1. **"stop"** - Sends stop command to halt current actions
2. **"disconnect"** - Immediately disconnects from server
3. **"script:quit"** - Executes quit command via script
4. **"script:heal"** - Executes heal command via script
5. **"script:{DISCONNECT}"** - Executes disconnect token via script
6. **"script:{custom command}"** - Template for custom script commands

### **Custom Script Commands:**
Users can type custom script commands like:
- `script:recall` - Recalls to safety
- `script:quit{ENTER}yes` - Quits with confirmation
- `script:{WAIT:1000}heal` - Waits 1 second then heals

## Usage Instructions

### **Setting Critical Health Action:**
1. Open **Character Sheet** (Alt+C or menu)
2. In **Automation** section, find **Critical Action** dropdown
3. Set **Critical HP %** (e.g., 30 for 30% health)
4. Select or type the desired action:
   - **"disconnect"** - Recommended for safety
   - **"stop"** - Stops automation but stays connected
   - **Custom script** - For advanced users

### **Testing the Feature:**
1. Set Critical HP % to a safe test value (like 90%)
2. Set Critical Action to "stop" for testing
3. Watch for log messages when health drops below threshold
4. Reset to desired values after testing

## Safety Benefits

### **Automatic Disconnect Protection:**
- **Prevents character death** from continued automation at low health
- **Immediate disconnection** when health becomes critical
- **Stops all automation** before disconnecting for clean shutdown

### **Flexible Response Options:**
- **Conservative users** can use "disconnect" for maximum safety
- **Advanced users** can use custom scripts for specific responses
- **Debugging users** can use "stop" to halt automation without disconnecting

## Files Modified
1. `DoorTelnet.Wpf/Views/Dialogs/CharacterSheetDialog.xaml` - Fixed ComboBox binding
2. `DoorTelnet.Wpf/ViewModels/CharacterSheetViewModel.cs` - Added options collection

## Result
The critical health dropdown now works correctly, allowing users to select "disconnect" or other actions when health becomes critically low. This provides essential safety protection for automated gameplay.