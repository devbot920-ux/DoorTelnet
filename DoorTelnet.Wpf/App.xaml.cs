using System;
using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using DoorTelnet.Wpf.ViewModels;
using DoorTelnet.Core.Scripting;
using DoorTelnet.Core.Automation;
using DoorTelnet.Core.Terminal;
using DoorTelnet.Core.Telnet;
using DoorTelnet.Core.World;
using DoorTelnet.Core.Combat;
using DoorTelnet.Core.Player;
using DoorTelnet.Wpf.Services;

namespace DoorTelnet.Wpf;

public partial class App : Application
{
    internal IHost? _host; // expose for dialog resolution

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(cfg =>
            {
                cfg.SetBasePath(AppContext.BaseDirectory);
                if (File.Exists(Path.Combine(AppContext.BaseDirectory, "appsettings.json")))
                {
                    cfg.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                }
            })
            .ConfigureLogging(lb =>
            {
                lb.ClearProviders();
                lb.AddDebug();
                lb.AddConsole();
            })
            .ConfigureServices((ctx, services) =>
            {
                var config = ctx.Configuration;
                services.AddSingleton<IConfiguration>(config);

                // Core singletons
                services.AddSingleton<ScreenBuffer>(_ => new ScreenBuffer(
                    int.TryParse(config["terminal:cols"], out var c) ? c : 80,
                    int.TryParse(config["terminal:rows"], out var r) ? r : 25));
                services.AddSingleton<RuleEngine>();
                services.AddSingleton<ScriptEngine>();
                services.AddSingleton<StatsTracker>();
                services.AddSingleton<RoomTracker>();
                services.AddSingleton<CombatTracker>();
                services.AddSingleton<PlayerProfile>();
                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton(sp => new CredentialStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoorTelnet", "credentials.json")));
                services.AddSingleton(sp => new CharacterProfileStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoorTelnet", "characters.json")));

                services.AddSingleton<TelnetClient>(sp =>
                {
                    var cfg = sp.GetRequiredService<IConfiguration>();
                    var cols = int.TryParse(cfg["terminal:cols"], out var cVal) ? cVal : 80;
                    var rows = int.TryParse(cfg["terminal:rows"], out var rVal) ? rVal : 25;
                    var script = sp.GetRequiredService<ScriptEngine>();
                    var rules = sp.GetRequiredService<RuleEngine>();
                    var stats = sp.GetRequiredService<StatsTracker>();
                    var logger = sp.GetRequiredService<ILogger<TelnetClient>>();
                    var diagnostics = bool.TryParse(cfg["diagnostics:telnet"], out var d) && d;
                    var raw = bool.TryParse(cfg["diagnostics:rawEcho"], out var re) && re;
                    var dumb = bool.TryParse(cfg["diagnostics:dumbMode"], out var dm) && dm;
                    var roomTracker = sp.GetRequiredService<RoomTracker>();
                    var combatTracker = sp.GetRequiredService<CombatTracker>();
                    var roomVm = sp.GetRequiredService<RoomViewModel>();
                    roomTracker.RoomChanged += _ => Current?.Dispatcher.BeginInvoke(roomVm.Refresh);
                    var screenField = typeof(ScriptEngine).GetField("_screen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    var client = new TelnetClient(cols, rows, script, rules, logger, diagnostics, raw, dumb, stats);

                    DateTime lastXpSent = DateTime.MinValue;
                    combatTracker.RequestExperienceCheck += () =>
                    {
                        if ((DateTime.UtcNow - lastXpSent).TotalMilliseconds < 700) return;
                        lastXpSent = DateTime.UtcNow;
                        client.SendCommand("xp");
                    };

                    bool initialCommandsSent = false;
                    void SendInitialCore()
                    {
                        client.SendCommand("inv");
                        client.SendCommand("st2");
                        client.SendCommand("stats");
                        client.SendCommand("spells");
                        client.SendCommand("inv");
                        client.SendCommand("xp");
                        logger.LogInformation("Initial data commands dispatched (inv, st2, stats, spells, inv, xp)");
                    }
                    void TrySendInitial()
                    {
                        if (initialCommandsSent) return;
                        initialCommandsSent = true;
                        SendInitialCore();
                    }

                    combatTracker.RequestInitialCommands += TrySendInitial;

                    client.LineReceived += line =>
                    {
                        try
                        {
                            roomTracker.AddLine(line);
                            combatTracker.ProcessLine(line);
                            if (!initialCommandsSent)
                            {
                                var chk = line.Trim();
                                if (chk.Contains("Obvious Exits:", StringComparison.OrdinalIgnoreCase) ||
                                    chk.StartsWith("Exits:", StringComparison.OrdinalIgnoreCase) ||
                                    chk.IndexOf("You rejoin the world", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    chk.StartsWith("Welcome", StringComparison.OrdinalIgnoreCase))
                                {
                                    TrySendInitial();
                                }
                            }

                            var screen = screenField?.GetValue(script) as ScreenBuffer;
                            if (screen != null)
                            {
                                roomTracker.TryUpdateRoom("defaultUser", "defaultChar", screen.ToText());
                            }
                        }
                        catch { }
                    };

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(4000);
                        TrySendInitial();
                    });
                    return client;
                });

                // ViewModels / dialogs
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<CredentialsViewModel>();
                services.AddTransient<CharacterProfilesViewModel>();

                services.AddSingleton<StatsViewModel>();
                services.AddSingleton<RoomViewModel>();
                services.AddSingleton<CombatViewModel>();
                services.AddSingleton<MainViewModel>();

                services.AddSingleton<MainWindow>(sp =>
                {
                    var w = new MainWindow { DataContext = sp.GetRequiredService<MainViewModel>() };
                    var settings = sp.GetRequiredService<ISettingsService>();
                    var ui = settings.Get().UI;
                    if (ui.Width > 400 && ui.Height > 300)
                    {
                        w.Width = ui.Width; w.Height = ui.Height;
                    }
                    return w;
                });
            })
            .Build();

        _host.Start();
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Closed += (s, _) =>
        {
            try
            {
                var settings = _host.Services.GetRequiredService<ISettingsService>();
                var model = settings.Get();
                model.UI.Width = (int)mainWindow.Width;
                model.UI.Height = (int)mainWindow.Height;
                settings.Save();
            }
            catch { }
        };
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}

