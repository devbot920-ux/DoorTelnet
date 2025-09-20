using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using DoorTelnet.Core.Telnet;
using DoorTelnet.Core.Automation;
using DoorTelnet.Core.Combat;
using DoorTelnet.Wpf.Services;
using DoorTelnet.Wpf.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;
using DoorTelnet.Core.Player;
using System.Linq;
using System.Windows;

namespace DoorTelnet.Wpf.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly TelnetClient _client;
    private readonly IConfiguration _config;
    private readonly StatsTracker _statsTracker;
    private readonly CombatTracker _combatTracker;
    private readonly ISettingsService _settingsService;
    private readonly System.IServiceProvider _serviceProvider;
    private readonly CredentialStore _credentialStore;
    private readonly PlayerProfile _profile; // injected player profile shared state

    public StatsViewModel Stats { get; }
    public RoomViewModel Room { get; }
    public CombatViewModel Combat { get; }

    public MainViewModel(
        TelnetClient client,
        IConfiguration config,
        ILogger<MainViewModel> logger,
        StatsTracker statsTracker,
        StatsViewModel statsViewModel,
        RoomViewModel roomViewModel,
        CombatViewModel combatViewModel,
        CombatTracker combatTracker,
        ISettingsService settingsService,
        System.IServiceProvider serviceProvider,
        CredentialStore credentialStore,
        PlayerProfile profile)
        : base(logger)
    {
        _client = client;
        _config = config;
        _statsTracker = statsTracker;
        _combatTracker = combatTracker;
        _settingsService = settingsService;
        _serviceProvider = serviceProvider;
        _credentialStore = credentialStore;
        _profile = profile; // assign
        Stats = statsViewModel;
        Room = roomViewModel;
        Combat = combatViewModel;
        ConnectionStatus = "Disconnected";
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected);
        ToggleConnectionCommand = new AsyncRelayCommand(ToggleConnectionAsync, () => !IsBusy);
        ShowSettingsCommand = new RelayCommand(OpenSettings);
        ShowCredentialsCommand = new RelayCommand(OpenCredentials);
        ShowCharactersCommand = new RelayCommand(OpenCharacters);
        ShowCharacterSheetCommand = new RelayCommand(OpenCharacterSheet);
        RefreshStatsCommand = new RelayCommand(SendStatsRequest, () => IsConnected);
        SendInitialDataCommand = new RelayCommand(SendInitialData, () => IsConnected);
        SendUsernameCommand = new RelayCommand(SendUsername, () => IsConnected && GetPreferredUsername() != null);
        SendPasswordCommand = new RelayCommand(SendPassword, () => IsConnected && GetPreferredPassword() != null);
        QuickLoginCommand = new AsyncRelayCommand(QuickLoginAsync, () => IsConnected && GetPreferredUsername() != null && GetPreferredPassword() != null);
        ToggleAutoGongCommand = new RelayCommand(() => { _profile.Features.AutoGong = !_profile.Features.AutoGong; OnPropertyChanged(nameof(AutoGong)); OnPropertyChanged(nameof(AutoGongButtonText)); });

        _client.ConnectionFailed += msg =>
        {
            var disp = App.Current?.Dispatcher;
            if (disp != null)
            {
                disp.Invoke(() =>
                {
                    ConnectionStatus = msg;
                    IsConnected = false;
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
    public ICommand RefreshStatsCommand { get; }
    public ICommand SendInitialDataCommand { get; }
    public ICommand SendUsernameCommand { get; }
    public ICommand SendPasswordCommand { get; }
    public ICommand QuickLoginCommand { get; }
    public ICommand ToggleAutoGongCommand { get; }

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
    public string AutoGongButtonText => AutoGong ? "Auto Gong: ON" : "Auto Gong: OFF";

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
            ConnectionStatus = $"Connected to {host}:{port}";
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
        var user = _settingsService.Get().Connection.LastUsername;
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
        await Task.Delay(300); // slight delay to let server prompt for password
        _client.SendCommand(pwd);
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
        // After possible edits, update command enabled state
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

    // Global automation toggle (Auto Gong) exposed to UI
    public bool AutoGong
    {
        get => _profile.Features.AutoGong;
        set
        {
            if (_profile.Features.AutoGong != value)
            {
                _profile.Features.AutoGong = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AutoGongButtonText));
            }
        }
    }

    private void OpenCharacterSheet()
    {
        var vm = _serviceProvider.GetService<CharacterSheetViewModel>();
        if (vm == null) return;
        vm.RefreshFromProfile(); // ensure latest data each time opened
        var win = new CharacterSheetDialog(vm) { Owner = App.Current.MainWindow };
        win.ShowDialog();
    }

    protected override void OnDisposing()
    {
        if (IsConnected)
        {
            _ = DisconnectAsync();
        }
    }
}
