using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using DoorTelnet.Core.Telnet;
using DoorTelnet.Core.Scripting;
using DoorTelnet.Core.Automation;
using DoorTelnet.Core.Combat;

namespace DoorTelnet.Wpf.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly TelnetClient _client;
    private readonly IConfiguration _config;
    private readonly StatsTracker _statsTracker;
    private readonly CombatTracker _combatTracker;

    public StatsViewModel Stats { get; }
    public RoomViewModel Room { get; } // Stage 4
    public CombatViewModel Combat { get; } // Stage 5

    public MainViewModel(TelnetClient client, IConfiguration config, ILogger<MainViewModel> logger, StatsTracker statsTracker, StatsViewModel statsViewModel, RoomViewModel roomViewModel, CombatViewModel combatViewModel, CombatTracker combatTracker)
        : base(logger)
    {
        _client = client;
        _config = config;
        _statsTracker = statsTracker;
        _combatTracker = combatTracker;
        Stats = statsViewModel;
        Room = roomViewModel; // Stage 4
        Combat = combatViewModel; // Stage 5
        ConnectionStatus = "Disconnected";
        ConnectCommand = new AsyncRelayCommand(ConnectAsync, CanConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected);
        ToggleConnectionCommand = new AsyncRelayCommand(ToggleConnectionAsync, () => !IsBusy);
    }

    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand ToggleConnectionCommand { get; }

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
        if (IsBusy) return;
        if (!IsConnected)
        {
            await ConnectAsync();
        }
        else
        {
            await DisconnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        if (IsConnected) return;
        IsBusy = true;
        var host = _config["connection:host"] ?? "localhost";
        var port = int.TryParse(_config["connection:port"], out var p) ? p : 23;
        ConnectionStatus = $"Connecting to {host}:{port}...";
        try
        {
            await _client.ConnectAsync(host, port);
            await _client.StartAsync();
            IsConnected = true;
            ConnectionStatus = "Connected";
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to connect");
            ConnectionStatus = "Failed to connect";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisconnectAsync()
    {
        if (!IsConnected) return;
        IsBusy = true;
        ConnectionStatus = "Disconnecting...";
        try
        {
            await _client.StopAsync();
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Error during disconnect");
        }
        finally
        {
            IsConnected = false;
            ConnectionStatus = "Disconnected";
            IsBusy = false;
        }
    }

    protected override void OnDisposing()
    {
        if (IsConnected)
        {
            _ = DisconnectAsync();
        }
    }
}
