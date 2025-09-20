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

namespace DoorTelnet.Wpf.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly TelnetClient _client;
    private readonly IConfiguration _config;
    private readonly StatsTracker _statsTracker;
    private readonly CombatTracker _combatTracker;
    private readonly ISettingsService _settingsService;
    private readonly System.IServiceProvider _serviceProvider;

    public StatsViewModel Stats { get; }
    public RoomViewModel Room { get; } // Stage 4
    public CombatViewModel Combat { get; } // Stage 5

    public MainViewModel(TelnetClient client, IConfiguration config, ILogger<MainViewModel> logger, StatsTracker statsTracker, StatsViewModel statsViewModel, RoomViewModel roomViewModel, CombatViewModel combatViewModel, CombatTracker combatTracker, ISettingsService settingsService, System.IServiceProvider serviceProvider)
        : base(logger)
    {
        _client = client;
        _config = config;
        _statsTracker = statsTracker;
        _combatTracker = combatTracker;
        _settingsService = settingsService;
        _serviceProvider = serviceProvider;
        Stats = statsViewModel;
        Room = roomViewModel; // Stage 4
        Combat = combatViewModel; // Stage 5
        ConnectionStatus = "Disconnected";
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected);
        ToggleConnectionCommand = new AsyncRelayCommand(ToggleConnectionAsync, () => !IsBusy);
        ShowSettingsCommand = new RelayCommand(OpenSettings);
        ShowCredentialsCommand = new RelayCommand(OpenCredentials);
        ShowCharactersCommand = new RelayCommand(OpenCharacters);
    }

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ToggleConnectionCommand { get; }
    public ICommand ShowSettingsCommand { get; }
    public ICommand ShowCredentialsCommand { get; }
    public ICommand ShowCharactersCommand { get; }

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
                OnPropertyChanged(nameof(ConnectButtonText));
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
    }

    private void OpenCharacters()
    {
        var vm = _serviceProvider.GetRequiredService<CharacterProfilesViewModel>();
        var win = new CharacterProfilesDialog(vm) { Owner = App.Current.MainWindow };
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
