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

namespace DoorTelnet.Wpf;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private IHost? _host;

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

                // Core singletons (minimal needed for now)
                services.AddSingleton<ScreenBuffer>(_ => new ScreenBuffer(
                    int.TryParse(config["terminal:cols"], out var c) ? c : 80,
                    int.TryParse(config["terminal:rows"], out var r) ? r : 25));
                services.AddSingleton<RuleEngine>();
                services.AddSingleton<ScriptEngine>();
                services.AddSingleton<StatsTracker>();

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
                    return new TelnetClient(cols, rows, script, rules, logger, diagnostics, raw, dumb, stats);
                });

                // ViewModels
                services.AddSingleton<MainViewModel>();
                services.AddSingleton<StatsViewModel>();

                // Windows
                services.AddTransient<MainWindow>();
            })
            .Build();

        _host.Start();
        var window = _host.Services.GetRequiredService<MainWindow>();
        window.DataContext = _host.Services.GetRequiredService<MainViewModel>();
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(2));
            _host.Dispose();
        }
        base.OnExit(e);
    }
}

