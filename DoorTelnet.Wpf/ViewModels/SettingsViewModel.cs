using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoorTelnet.Wpf.Services;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Text.Json;
using System;

namespace DoorTelnet.Wpf.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly ISettingsService _settingsService;

    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port;
    [ObservableProperty] private bool _autoReconnect;
    [ObservableProperty] private int _autoReconnectDelaySec;

    // Replace source-gen attributes with manual properties for reliability
    private string _theme = string.Empty; public string Theme { get => _theme; set => SetProperty(ref _theme, value); }
    private string _loginScript = string.Empty; public string LoginScript { get => _loginScript; set => SetProperty(ref _loginScript, value); }
    private bool _sendLoginScript; public bool SendLoginScript { get => _sendLoginScript; set => SetProperty(ref _sendLoginScript, value); }
    private string _lastUser = string.Empty; public string LastUser { get => _lastUser; set => SetProperty(ref _lastUser, value); }

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand ReloadCommand { get; }

    public SettingsViewModel(ISettingsService settingsService, ILogger<SettingsViewModel> logger) : base(logger)
    {
        _settingsService = settingsService;
        SaveCommand = new RelayCommand(Save);
        ReloadCommand = new RelayCommand(Load);
        Load();
    }

    private string AppSettingsPath => Path.Combine(AppContext.BaseDirectory, "appsettings.json");

    private void Load()
    {
        var s = _settingsService.Get();
        Host = s.Connection.Host;
        Port = s.Connection.Port;
        AutoReconnect = s.Automation.AutoReconnect;
        AutoReconnectDelaySec = s.Automation.AutoReconnectDelaySec;
        LastUser = s.Connection.LastUsername;
        if (File.Exists(AppSettingsPath))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(AppSettingsPath));
                if (doc.RootElement.TryGetProperty("ui", out var ui) && ui.TryGetProperty("theme", out var th) && th.ValueKind == JsonValueKind.String) Theme = th.GetString() ?? string.Empty;
                if (doc.RootElement.TryGetProperty("script", out var scriptObj))
                {
                    if (scriptObj.TryGetProperty("loginScript", out var ls) && ls.ValueKind == JsonValueKind.String) LoginScript = ls.GetString() ?? string.Empty;
                    if (scriptObj.TryGetProperty("sendLoginScript", out var send))
                    {
                        SendLoginScript = send.ValueKind == JsonValueKind.True || (send.ValueKind == JsonValueKind.String && bool.TryParse(send.GetString(), out var b) && b);
                    }
                }
            }
            catch { }
        }
    }

    private void Save()
    {
        var s = _settingsService.Get();
        s.Connection.Host = Host;
        s.Connection.Port = Port;
        s.Automation.AutoReconnect = AutoReconnect;
        s.Automation.AutoReconnectDelaySec = AutoReconnectDelaySec;
        s.Connection.LastUsername = LastUser;
        _settingsService.Save();

        // Persist theme/loginScript/send flag back to appsettings.json
        try
        {
            JsonDocument? doc = null;
            if (File.Exists(AppSettingsPath))
            {
                doc = JsonDocument.Parse(File.ReadAllText(AppSettingsPath));
            }
            using var ms = new MemoryStream();
            using var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();

            bool wroteUi = false; bool wroteScript = false;
            if (doc != null)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    switch (prop.Name)
                    {
                        case "ui":
                            writer.WritePropertyName("ui");
                            writer.WriteStartObject();
                            writer.WriteString("theme", Theme);
                            if (prop.Value.TryGetProperty("window", out var win))
                            {
                                writer.WritePropertyName("window");
                                win.WriteTo(writer); // keep window size
                            }
                            writer.WriteEndObject();
                            wroteUi = true;
                            break;
                        case "script":
                            writer.WritePropertyName("script");
                            writer.WriteStartObject();
                            writer.WriteString("file", prop.Value.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : "");
                            writer.WriteString("loginScript", LoginScript);
                            writer.WriteBoolean("sendLoginScript", SendLoginScript);
                            writer.WriteEndObject();
                            wroteScript = true;
                            break;
                        default:
                            prop.WriteTo(writer);
                            break;
                    }
                }
            }
            if (!wroteUi)
            {
                writer.WritePropertyName("ui");
                writer.WriteStartObject();
                writer.WriteString("theme", Theme);
                writer.WriteEndObject();
            }
            if (!wroteScript)
            {
                writer.WritePropertyName("script");
                writer.WriteStartObject();
                writer.WriteString("loginScript", LoginScript);
                writer.WriteBoolean("sendLoginScript", SendLoginScript);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
            writer.Flush();
            File.WriteAllBytes(AppSettingsPath, ms.ToArray());
        }
        catch { }
    }
}
