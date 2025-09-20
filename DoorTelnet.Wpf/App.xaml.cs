using System;
using System.IO;
using System.Text.RegularExpressions;
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
                            // --- Begin PlayerProfile population logic ---
                            var profile = sp.GetRequiredService<PlayerProfile>();
                            // Detect spells listing: assume lines like "nick - Long Name (Sphere) mana:## diff:##"
                            var spellMatch = Regex.Match(line, @"^(?<nick>[a-zA-Z]+)\s+[-:]\s+(?<long>[^()]+)\((?<sphere>[^)]+)\)\s+mana:?\s*(?<mana>\d+)\s+diff:?\s*(?<diff>\d+)", RegexOptions.IgnoreCase);
                            if (spellMatch.Success)
                            {
                                var nick = spellMatch.Groups["nick"].Value.Trim();
                                profile.AddOrUpdateSpell(new SpellInfo
                                {
                                    Nick = nick,
                                    LongName = spellMatch.Groups["long"].Value.Trim(),
                                    Sphere = spellMatch.Groups["sphere"].Value.Trim(),
                                    Mana = int.Parse(spellMatch.Groups["mana"].Value),
                                    Diff = int.Parse(spellMatch.Groups["diff"].Value)
                                });
                            }
                            // Inventory lines: simple heuristic for lines starting with item names (very loose)
                            var invMatch = Regex.Match(line, @"^\s*(?:\d+\)|-\s+|\*\s+)?(?:(?:an?|the)\s+)?(?<name>[A-Za-z][A-Za-z' \-]{2,})$" );
                            if (invMatch.Success && line.Length < 60 && !line.Contains(":"))
                            {
                                var itemName = invMatch.Groups["name"].Value.Trim();
                                if (!string.IsNullOrWhiteSpace(itemName)) profile.AddInventoryItem(itemName);
                            }
                            // Heals list: pattern short -> spell (heals:###)
                            var healMatch = Regex.Match(line, @"^(?<short>[A-Za-z]+)\s*->\s*(?<spell>[A-Za-z ]+)\s*\(heals:?\s*(?<amt>\d+)\)", RegexOptions.IgnoreCase);
                            if (healMatch.Success)
                            {
                                var sh = healMatch.Groups["short"].Value.Trim();
                                profile.AddHeal(new HealSpell
                                {
                                    Short = sh,
                                    Spell = healMatch.Groups["spell"].Value.Trim(),
                                    Heals = int.Parse(healMatch.Groups["amt"].Value)
                                });
                            }
                            // Shields list: assume lines like "shield: <spellname>" or standalone shield spell names
                            var shieldMatch = Regex.Match(line, @"(shield|aegis|barrier)\b", RegexOptions.IgnoreCase);
                            if (shieldMatch.Success)
                            {
                                var shName = shieldMatch.Value.Trim();
                                profile.AddShield(shName);
                            }
                            // Character basic stats block detection; simple capture of name/class on line like "Name : Bob    Class : Cleric"
                            var nameClassMatch = Regex.Match(line, @"Name\s*:\s*(?<nm>[A-Za-z][A-Za-z]+).*Class\s*:\s*(?<cls>[A-Za-z]+)", RegexOptions.IgnoreCase);
                            if (nameClassMatch.Success)
                            {
                                profile.SetNameClass(nameClassMatch.Groups["nm"].Value.Trim(), nameClassMatch.Groups["cls"].Value.Trim());
                            }
                            // Detect shield fade/cast lines
                            if (Regex.IsMatch(line, @"shield fades", RegexOptions.IgnoreCase)) profile.SetShielded(false);
                            if (Regex.IsMatch(line, @"You are surrounded by a magical shield", RegexOptions.IgnoreCase)) profile.SetShielded(true);
                            // AutoAttack: on seeing a monster engaged message if flag set
                            if (profile.Features.AutoAttack && Regex.IsMatch(line, @"^You see (?:an?|the) ([A-Za-z' -]+) here\.", RegexOptions.IgnoreCase))
                            {
                                var mob = Regex.Match(line, @"^You see (?:an?|the) (?<m>[A-Za-z' -]+) here", RegexOptions.IgnoreCase).Groups["m"].Value.Trim();
                                if (!string.IsNullOrWhiteSpace(mob)) client.SendCommand($"kill {mob.Split(' ')[0]}");
                            }
                            // AutoRing placeholder (if AutoRing just ring bell/gong variant)
                            if (profile.Features.AutoRing && Regex.IsMatch(line, @"^The gong reverberates", RegexOptions.IgnoreCase))
                            {
                                client.SendCommand("ring gong");
                            }
                            // --- End PlayerProfile population logic ---
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

                services.AddSingleton<PlayerProfile>();
                services.AddSingleton<AutomationFeatureService>();

                // ViewModels / dialogs
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<CredentialsViewModel>();
                services.AddTransient<CharacterProfilesViewModel>();
                services.AddTransient<CharacterSheetViewModel>();

                services.AddSingleton<StatsViewModel>();
                services.AddSingleton<RoomViewModel>();
                services.AddSingleton<CombatViewModel>();
                services.AddSingleton<MainViewModel>();

                services.AddSingleton<MainWindow>(sp =>
                {
                    // Force automation service creation (hooks Telnet events)
                    _ = sp.GetRequiredService<AutomationFeatureService>();
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

