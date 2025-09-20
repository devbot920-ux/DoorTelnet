using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoorTelnet.Wpf.Services;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Wpf.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;

    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port;
    [ObservableProperty] private bool _autoReconnect;
    [ObservableProperty] private int _autoReconnectDelaySec;

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand ReloadCommand { get; }

    public SettingsViewModel(ISettingsService settingsService, ILogger<SettingsViewModel> logger) : base(logger)
    {
        _settingsService = settingsService;
        SaveCommand = new RelayCommand(Save);
        ReloadCommand = new RelayCommand(Load);
        Load();
    }

    private void Load()
    {
        var s = _settingsService.Get();
        Host = s.Connection.Host;
        Port = s.Connection.Port;
        AutoReconnect = s.Automation.AutoReconnect;
        AutoReconnectDelaySec = s.Automation.AutoReconnectDelaySec;
    }

    private void Save()
    {
        var s = _settingsService.Get();
        s.Connection.Host = Host;
        s.Connection.Port = Port;
        s.Automation.AutoReconnect = AutoReconnect;
        s.Automation.AutoReconnectDelaySec = AutoReconnectDelaySec;
        _settingsService.Save();
    }
}
