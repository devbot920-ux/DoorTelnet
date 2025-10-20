using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using DoorTelnet.Core.Telnet;
using DoorTelnet.Core.Automation;
using DoorTelnet.Core.Combat;
using DoorTelnet.Core.World; // NEW: Add for RoomTracker
using DoorTelnet.Wpf.Services;
using DoorTelnet.Wpf.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using DoorTelnet.Core.Player;
using System.Linq;
using System.Windows;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO; // added

namespace DoorTelnet.Wpf.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly TelnetClient _client;
    private readonly IConfiguration _config;
    private readonly StatsTracker _statsTracker;
    private readonly CombatTracker _combatTracker;
    private readonly RoomTracker _roomTracker; // NEW: Add RoomTracker dependency
    private readonly ISettingsService _settingsService;
    private readonly System.IServiceProvider _serviceProvider;
    private readonly CredentialStore _credentialStore;
    private readonly PlayerProfile _profile;
    private readonly UserSelectionService _userSelection; // new shared selection service

    public StatsViewModel Stats { get; }
    public RoomViewModel Room { get; }
    public CombatViewModel Combat { get; }

    // Status bar derived
    public string ProfileName { get { return _profile.Player.Name; } set { } }
    public int InventoryCount { get { return _profile.Player.Inventory.Count; } set { } }
    public int SpellsCount { get { return _profile.Spells.Count; } set { } }
    public string ShieldedStatus { get { return _profile.Effects.Shielded ? "Yes" : "No"; } set { } }
    public string HungerStatus { get { return string.IsNullOrWhiteSpace(_profile.Effects.HungerState) ? "?" : _profile.Effects.HungerState; } set { } }
    public string ThirstStatus { get { return string.IsNullOrWhiteSpace(_profile.Effects.ThirstState) ? "?" : _profile.Effects.ThirstState; } set { } }
    public long Experience { get { return _profile.Player.Experience; } set { } }
    public long XpLeft { get { return _profile.Player.XpLeft; } set { } }
    public string ArmedWith { get { return _profile.Player.ArmedWith; } set { } }
    public string FirstName { get { return _profile.Player.FirstName; } set { } }

    public ObservableCollection<MenuUserItem> UserMenuItems { get; } = new();

    // Tooltip properties for automation features
    public string AutoGongTooltip => "Auto Gong: Automatically rings the gong when no AC/AT timers and no aggressive monsters are present. Enables Auto Attack to handle summoned monsters. Requires sufficient HP to operate safely.";
    public string AutoAttackTooltip => "Auto Attack: Automatically attacks aggressive monsters in the current room. This is automatically enabled when Auto Gong is active, but can also be used independently.";

    public class MenuUserItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = string.Empty;
        private bool _isSelected;
        public bool IsSelected { get => _isSelected; set { if (_isSelected != value) { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); } } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private void RebuildUserMenu()
    {
        UserMenuItems.Clear();
        foreach (var u in _credentialStore.ListUsernames())
        {
            UserMenuItems.Add(new MenuUserItem { Name = u, IsSelected = string.Equals(u, SelectedUser, StringComparison.OrdinalIgnoreCase) });
        }
    }

    private void RaiseProfileBar()
    {
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(InventoryCount));
        OnPropertyChanged(nameof(SpellsCount));
        OnPropertyChanged(nameof(ShieldedStatus));
        OnPropertyChanged(nameof(HungerStatus));
        OnPropertyChanged(nameof(ThirstStatus));
        OnPropertyChanged(nameof(Experience));
        OnPropertyChanged(nameof(XpLeft));
        OnPropertyChanged(nameof(ArmedWith));
        OnPropertyChanged(nameof(FirstName));
        // Also propagate feature-related UI
        OnPropertyChanged(nameof(AutoGong));
        OnPropertyChanged(nameof(AutoAttack));
        OnPropertyChanged(nameof(AutoGongButtonText));
        OnPropertyChanged(nameof(AutoAttackButtonText));
    }

    public MainViewModel(
        TelnetClient client,
        IConfiguration config,
        ILogger<MainViewModel> logger,
        StatsTracker statsTracker,
        StatsViewModel statsViewModel,
        RoomViewModel roomViewModel,
        CombatViewModel combatViewModel,
        CombatTracker combatTracker,
        RoomTracker roomTracker, // NEW: Add RoomTracker parameter
        ISettingsService settingsService,
        System.IServiceProvider serviceProvider,
        CredentialStore credentialStore,
        PlayerProfile profile,
        UserSelectionService userSelection) : base(logger)
    {
        _client = client;
        _config = config;
        _statsTracker = statsTracker;
        _combatTracker = combatTracker;
        _roomTracker = roomTracker; // NEW: Assign RoomTracker
        _settingsService = settingsService;
        _serviceProvider = serviceProvider;
        _credentialStore = credentialStore;
        _profile = profile;
        _userSelection = userSelection;
        Stats = statsViewModel;
        Room = roomViewModel;
        Combat = combatViewModel;
        ConnectionStatus = "Disconnected";

        // Test logging functionality
        logger.LogInformation("MainViewModel initialized successfully");
        logger.LogDebug("Profile features - AutoGong: {AutoGong}, AutoAttack: {AutoAttack}", profile.Features.AutoGong, profile.Features.AutoAttack);

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected);
        ToggleConnectionCommand = new AsyncRelayCommand(ToggleConnectionAsync, () => !IsBusy);
        ShowSettingsCommand = new RelayCommand(OpenSettings);
        ShowCredentialsCommand = new RelayCommand(OpenCredentials);
        ShowCharactersCommand = new RelayCommand(OpenCharacters);
        ShowCharacterSheetCommand = new RelayCommand(OpenCharacterSheet);
        ShowHotKeysCommand = new RelayCommand(OpenHotKeys);
        RefreshStatsCommand = new RelayCommand(SendStatsRequest, () => IsConnected);
        SendInitialDataCommand = new RelayCommand(SendInitialData, () => IsConnected);
        SendUsernameCommand = new RelayCommand(SendUsername, () => IsConnected && GetPreferredUsername() != null);
        SendPasswordCommand = new RelayCommand(SendPassword, () => IsConnected && GetPreferredPassword() != null);
        QuickLoginCommand = new AsyncRelayCommand(QuickLoginAsync, () => IsConnected && GetPreferredUsername() != null && GetPreferredPassword() != null);
        SelectUserCommand = new RelayCommand<string>(u => { if (!string.IsNullOrWhiteSpace(u)) SelectedUser = u; });
        ClearCharacterDataCommand = new RelayCommand(ClearAllCharacterData); // NEW: Initialize clear command

        SelectedUser = _userSelection.SelectedUser ?? (!string.IsNullOrWhiteSpace(_settingsService.Get().Connection.LastUsername) ? _settingsService.Get().Connection.LastUsername : _credentialStore.ListUsernames().FirstOrDefault());
        RebuildUserMenu();

        _profile.Updated += () => App.Current.Dispatcher.Invoke(RaiseProfileBar);

        // Subscribe to stats changes to update auto-gong button status
        _statsTracker.Updated += () => App.Current.Dispatcher.Invoke(() => {
            OnPropertyChanged(nameof(AutoGongButtonText));
            OnPropertyChanged(nameof(AutoAttackButtonText));
            // Ensure the toggles reflect any external changes (MCP)
            OnPropertyChanged(nameof(AutoGong));
            OnPropertyChanged(nameof(AutoAttack));
        });

        _client.ConnectionFailed += msg =>
        {
            var disp = App.Current?.Dispatcher;
            if (disp != null)
            {
                disp.Invoke(() =>
                {
                    ConnectionStatus = msg;
                    IsConnected = false;
                    logger.LogWarning("Connection failed: {Message}", msg);
                    MessageBox.Show(msg, "Connection Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                });
            }
        };

        _combatTracker.RequestInitialCommands += () => SendInitialData();
    }

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ToggleConnectionCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand ShowCredentialsCommand { get; }
    public ICommand ShowCharactersCommand { get; }
    public ICommand ShowCharacterSheetCommand { get; }
    public ICommand ShowHotKeysCommand { get; }
    public ICommand RefreshStatsCommand { get; }
    public ICommand SendInitialDataCommand { get; }
    public ICommand SendUsernameCommand { get; }
    public ICommand SendPasswordCommand { get; }
    public ICommand QuickLoginCommand { get; }
    public ICommand SelectUserCommand { get; }
    public ICommand ClearCharacterDataCommand { get; } // NEW: Command to clear all character data

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
            {
                (ConnectCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                (DisconnectCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                (ToggleConnectionCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                (RefreshStatsCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (SendInitialDataCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (SendUsernameCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (SendPasswordCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (QuickLoginCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(ConnectButtonText));
                if (value)
                {
                    // Queue initial data after slight delay to let buffer settle
                    Task.Run(async () => { await Task.Delay(1500); SendInitialData(); });
                }
            }
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                (ToggleConnectionCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public string ConnectButtonText => IsConnected ? "Disconnect" : "Connect";
    
    public string AutoGongButtonText 
    { 
        get 
        {
            if (!AutoGong) 
                return "Auto Gong: OFF";
            
            // Check if HP is too low for gong operation
            if (_statsTracker.MaxHp > 0)
            {
                var hpPercent = (int)Math.Round((double)_statsTracker.Hp / _statsTracker.MaxHp * 100);
                var gongMinPercent = _profile.Thresholds.GongMinHpPercent;
                var warningPercent = _profile.Thresholds.WarningHealHpPercent;
                
                if (hpPercent <= warningPercent && warningPercent > 0)
                    return "Auto Gong: HEALING";
                else if (hpPercent < gongMinPercent)
                    return "Auto Gong: LOW HP";
            }
            
            return "Auto Gong: ON";
        } 
    }
    
    public string AutoAttackButtonText => AutoAttack ? "Auto Attack: ON" : "Auto Attack: OFF";
    public string CurrentUserButtonText => string.IsNullOrWhiteSpace(SelectedUser) ? "No User" : SelectedUser;

    private string _connectionStatus = string.Empty;
    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    private bool CanConnect() => !IsConnected;

    private async Task ToggleConnectionAsync()
    {
        if (IsConnected) await DisconnectAsync(); else await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (IsConnected) return;
        IsBusy = true;
        try
        {
            var host = _config["connection:host"] ?? _settingsService.Get().Connection.Host;
            var port = int.TryParse(_config["connection:port"], out var p) ? p : _settingsService.Get().Connection.Port;
            if (string.IsNullOrWhiteSpace(host)) host = "localhost";
            await _client.ConnectAsync(host, port);
            await _client.StartAsync();
            IsConnected = true;
            ConnectionStatus = $"Connected"; // simplified status text
        }
        finally { IsBusy = false; }
    }

    private async Task DisconnectAsync()
    {
        if (!IsConnected) return;
        IsBusy = true;
        try
        {
            await _client.StopAsync();
            IsConnected = false;
            ConnectionStatus = "Disconnected";
            
            // NEW: Automatically clear all character data when disconnecting
            ClearAllCharacterData();
        }
        finally { IsBusy = false; }
    }

    private void SendStatsRequest()
    {
        if (!IsConnected) return;
        _client.SendCommand("st2");
        _client.SendCommand("stats");
    }

    private void SendInitialData()
    {
        if (!IsConnected) return;
        _client.SendCommand("inv");
        _client.SendCommand("st2");
        _client.SendCommand("stats");
        _client.SendCommand("spells");
        _client.SendCommand("inv");
        _client.SendCommand("xp");
    }

    private string? GetPreferredUsername()
    {
        var user = SelectedUser ?? _settingsService.Get().Connection.LastUsername;
        if (!string.IsNullOrWhiteSpace(user) && _credentialStore.ListUsernames().Contains(user)) return user;
        return _credentialStore.ListUsernames().FirstOrDefault();
    }

    private string? GetPreferredPassword()
    {
        var user = GetPreferredUsername();
        if (user == null) return null;
        return _credentialStore.GetPassword(user);
    }

    private void SendUsername()
    {
        if (!IsConnected) return;
        var user = GetPreferredUsername();
        if (user != null)
        {
            _client.SendCommand(user);
        }
    }

    private void SendPassword()
    {
        if (!IsConnected) return;
        var pwd = GetPreferredPassword();
        if (!string.IsNullOrEmpty(pwd))
        {
            _client.SendCommand(pwd);
        }
    }

    private async Task QuickLoginAsync()
    {
        if (!IsConnected) return;
        var user = GetPreferredUsername();
        var pwd = GetPreferredPassword();
        if (user == null || pwd == null) return;
        _client.SendCommand(user);
        await Task.Delay(300);
        _client.SendCommand(pwd);
        // After quick login, optionally run login script
        try
        {
            var cfgPath = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (File.Exists(cfgPath))
            {
                var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfgPath));
                bool send = false; string script = string.Empty;
                if (json.RootElement.TryGetProperty("script", out var scriptObj))
                {
                    if (scriptObj.TryGetProperty("sendLoginScript", out var sendProp) && sendProp.ValueKind == System.Text.Json.JsonValueKind.True) send = true;
                    if (scriptObj.TryGetProperty("loginScript", out var loginProp) && loginProp.ValueKind == System.Text.Json.JsonValueKind.String) script = loginProp.GetString() ?? string.Empty;
                }
                if (send && !string.IsNullOrWhiteSpace(script))
                {
                    await ExecuteScriptAsync(script);
                }
            }
        }
        catch { }
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

    private void OpenSettings()
    {
        var vm = _serviceProvider.GetRequiredService<SettingsViewModel>();
        var win = new Views.Dialogs.SettingsDialog(vm) { Owner = App.Current.MainWindow };
        win.ShowDialog();
    }

    private void OpenCredentials()
    {
        var vm = _serviceProvider.GetRequiredService<CredentialsViewModel>();
        var win = new CredentialsDialog(vm) { Owner = App.Current.MainWindow };
        win.ShowDialog();
        (SendUsernameCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (SendPasswordCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (QuickLoginCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }

    private void OpenCharacters()
    {
        var vm = _serviceProvider.GetRequiredService<CharacterProfilesViewModel>();
        var win = new CharacterProfilesDialog(vm) { Owner = App.Current.MainWindow };
        win.ShowDialog();
    }

    public bool AutoGong
    {
        get => _profile.Features.AutoGong;
        set
        {
            if (_profile.Features.AutoGong != value)
            {
                _profile.Features.AutoGong = value;
                // Auto-enable AutoAttack when AutoGong is turned on from UI
                if (value && !_profile.Features.AutoAttack)
                {
                    _profile.Features.AutoAttack = true;
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(AutoGongButtonText));
                OnPropertyChanged(nameof(AutoAttack));
                OnPropertyChanged(nameof(AutoAttackButtonText));
            }
        }
    }

    public bool AutoAttack
    {
        get => _profile.Features.AutoAttack;
        set
        {
            if (_profile.Features.AutoAttack != value)
            {
                _profile.Features.AutoAttack = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AutoAttackButtonText));
            }
        }
    }

    private void OpenCharacterSheet()
    {
        var vm = _serviceProvider.GetService<CharacterSheetViewModel>();
        if (vm == null) return;
        vm.RefreshFromProfile();
        var win = new CharacterSheetDialog(vm) { Owner = App.Current.MainWindow };
        win.ShowDialog();
    }

    private void OpenHotKeys()
    {
        var vm = _serviceProvider.GetRequiredService<HotKeysViewModel>();
        var win = new Views.Dialogs.HotKeysDialog(vm) { Owner = App.Current.MainWindow };
        win.ShowDialog();
    }

    protected override void OnDisposing()
    {
        if (IsConnected)
        {
            _ = DisconnectAsync();
        }
    }
    private string? _selectedUser;
    public string? SelectedUser
    {
        get => _selectedUser;
        set
        {
            if (_selectedUser != value)
            {
                _selectedUser = value;
                _userSelection.SelectedUser = value;
                // Persist to settings
                try
                {
                    var s = _settingsService.Get();
                    s.Connection.LastUsername = value ?? string.Empty;
                    _settingsService.Save();
                }
                catch { }
                OnPropertyChanged();
                OnPropertyChanged(nameof(CurrentUserButtonText));
                RebuildUserMenu();
            }
        }
    }

    /// <summary>
    /// Clears all character data including HP, XP, location, monsters, combat info, etc.
    /// Used when disconnecting or manually resetting character data.
    /// </summary>
    private void ClearAllCharacterData()
    {
        try
        {
            _logger.LogInformation("Clearing all character data...");
            
            // 1. Clear PlayerProfile (resets all character info, stats, inventory, spells, etc.)
            _profile.Reset();
            
            // 2. Clear combat tracker (active combats, completed combats, experience tracking)
            _combatTracker.ClearHistory();
            
            // 3. Clear room tracker (current room, monsters, location data)
            // Use reflection to clear CurrentRoom since there's no public clear method
            try
            {
                var currentRoomField = _roomTracker.GetType().GetProperty("CurrentRoom");
                currentRoomField?.SetValue(_roomTracker, null);
                
                // Also clear internal room and edge data if accessible
                var roomsField = _roomTracker.GetType().GetField("_rooms", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var edgesField = _roomTracker.GetType().GetField("_edges", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var adjacentRoomDataField = _roomTracker.GetType().GetField("_adjacentRoomData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                var lineBufferField = _roomTracker.GetType().GetField("_lineBuffer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                
                if (roomsField?.GetValue(_roomTracker) is System.Collections.IDictionary rooms) rooms.Clear();
                if (edgesField?.GetValue(_roomTracker) is System.Collections.IDictionary edges) edges.Clear();
                if (adjacentRoomDataField?.GetValue(_roomTracker) is System.Collections.IDictionary adjRooms) adjRooms.Clear();
                
                // Clear line buffer if available
                if (lineBufferField?.GetValue(_roomTracker) != null)
                {
                    var clearMethod = lineBufferField.FieldType.GetMethod("Clear", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (clearMethod == null)
                    {
                        // Try to clear the internal lines collection
                        var linesField = lineBufferField.FieldType.GetField("_lines", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (linesField?.GetValue(lineBufferField.GetValue(_roomTracker)) is System.Collections.IList lines)
                        {
                            lines.Clear();
                        }
                    }
                    else
                    {
                        clearMethod.Invoke(lineBufferField.GetValue(_roomTracker), null);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fully clear room tracker data");
            }
            
            // 4. Clear stats tracker data (HP, MP, etc.)
            // Use reflection to reset stats since there's no public clear method
            try
            {
                var statsProps = _statsTracker.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                    .Where(p => p.CanWrite && (p.PropertyType == typeof(int) || p.PropertyType == typeof(long)))
                    .ToList();
                
                foreach (var prop in statsProps)
                {
                    if (prop.Name.Contains("Hp") || prop.Name.Contains("Mp") || prop.Name.Contains("Mv") || 
                        prop.Name.Contains("At") || prop.Name.Contains("Ac"))
                    {
                        prop.SetValue(_statsTracker, 0);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fully clear stats tracker data");
            }
            
            // 5. Clear ViewModels to refresh UI - just trigger property updates
            Room.Refresh();  // This should clear the room display
            Combat.ClearHistoryCommand?.Execute(null); // This clears the combat UI
            
            // 6. Update all UI bindings
            RaiseProfileBar(); // Updates all status bar properties
            
            _logger.LogInformation("Character data cleared successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing character data");
        }
    }
}
