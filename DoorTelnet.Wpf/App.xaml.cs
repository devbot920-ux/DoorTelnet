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
                // logging provider registration
                services.AddSingleton<LogBuffer>(_ => new LogBuffer(500));
                services.AddSingleton<WpfLogProvider>();
                services.AddSingleton<LogViewModel>();

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
                services.AddSingleton<UserSelectionService>();

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
                    var userSelection = sp.GetRequiredService<UserSelectionService>();
                    var characterStore = sp.GetRequiredService<CharacterProfileStore>();
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

                    // REGEX for block parsing
                    var spellLine = new Regex(@"^(?<mana>\d+)\s+(?<diff>\d+)\s+(?<nick>[a-zA-Z][a-zA-Z0-9]*)\s+(?<sphere>[A-Z])\s+(?<long>.+?)\s*$", RegexOptions.Compiled);
                    var healLine = new Regex(@"^(?<short>[A-Za-z]+)\s*->\s*(?<spell>[A-Za-z ]+)\s*\(heals:?\s*(?<amt>\d+)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

                    // Inventory parsing helpers (two styles: compact items line in st2, and raw inventory list between prompts)
                    PlayerProfile profile = sp.GetRequiredService<PlayerProfile>();

                    void PersistCharacterIfNew()
                    {
                        try
                        {
                            var username = userSelection.SelectedUser ?? sp.GetRequiredService<ISettingsService>().Get().Connection.LastUsername;
                            if (string.IsNullOrWhiteSpace(username)) username = "default";
                            var name = profile.Player.Name;
                            if (string.IsNullOrWhiteSpace(name)) return;
                            // Only persist if not already there
                            characterStore.EnsureCharacterWithMeta(username,
                                name,
                                profile.Player.FirstName,
                                profile.Player.LastName,
                                profile.Player.Race,
                                profile.Player.Walk,
                                profile.Player.Class,
                                profile.Player.Level,
                                profile.Player.Experience,
                                profile.Player.XpLeft);
                        }
                        catch { }
                    }

                    void ParseScreenSnapshot()
                    {
                        var screen = screenField?.GetValue(script) as ScreenBuffer;
                        if (screen == null) return;
                        var text = screen.ToText();
                        if (string.IsNullOrWhiteSpace(text)) return;

                        var lines = text.Split('\n')
                            .Select(l => l.TrimEnd('\r'))
                            .ToList();

                        // Identity block parsing (supports lines provided by user sample)
                        string? nameLine = lines.FirstOrDefault(l => l.StartsWith("Name", StringComparison.OrdinalIgnoreCase));
                        string? raceLine = lines.FirstOrDefault(l => l.StartsWith("Race", StringComparison.OrdinalIgnoreCase));
                        string? walkLine = lines.FirstOrDefault(l => l.StartsWith("Walk", StringComparison.OrdinalIgnoreCase));
                        string? classLine = lines.FirstOrDefault(l => l.StartsWith("Class", StringComparison.OrdinalIgnoreCase));
                        string? levelLine = lines.FirstOrDefault(l => l.Contains("Level", StringComparison.OrdinalIgnoreCase));

                        string ExtractAfterColon(string? l)
                            => string.IsNullOrWhiteSpace(l) ? string.Empty : Regex.Replace(l, @"^[^:]*:\s*", "").Trim();

                        int level = 0;
                        if (!string.IsNullOrWhiteSpace(levelLine))
                        {
                            var mLvl = Regex.Match(levelLine, @"Level\s*:\s*(?<lvl>\d+)");
                            if (mLvl.Success) int.TryParse(mLvl.Groups["lvl"].Value, out level);
                        }

                        var nameVal = ExtractAfterColon(nameLine);
                        if (!string.IsNullOrEmpty(nameVal))
                        {
                            // Name line may contain trailing comma segments (e.g., clan info) - keep first token
                            var firstComma = nameVal.IndexOf(',');
                            if (firstComma > 0) nameVal = nameVal.Substring(0, firstComma).Trim();
                        }
                        var raceVal = ExtractAfterColon(raceLine);
                        var walkVal = ExtractAfterColon(walkLine);
                        var classVal = ExtractAfterColon(classLine);
                        if (!string.IsNullOrWhiteSpace(nameVal) || !string.IsNullOrWhiteSpace(raceVal) || !string.IsNullOrWhiteSpace(walkVal) || !string.IsNullOrWhiteSpace(classVal) || level > 0)
                        {
                            profile.SetIdentity(nameVal, raceVal, walkVal, classVal, level > 0 ? level : null);
                        }

                        PersistCharacterIfNew();

                        // Spells table
                        int headerIdx = lines.FindIndex(l => l.Contains("Nickname") && l.Contains("Mana"));
                        if (headerIdx >= 0)
                        {
                            var newSpells = new List<SpellInfo>();
                            for (int i = headerIdx + 1; i < lines.Count; i++)
                            {
                                var raw = lines[i].Trim();
                                if (string.IsNullOrWhiteSpace(raw)) break;
                                if (raw.StartsWith("-=-")) continue;
                                var m = spellLine.Match(raw);
                                if (!m.Success) continue;
                                if (!int.TryParse(m.Groups["mana"].Value, out var mana)) continue;
                                if (!int.TryParse(m.Groups["diff"].Value, out var diff)) diff = 0;
                                var nick = m.Groups["nick"].Value;
                                var sphereCode = m.Groups["sphere"].Value.FirstOrDefault();
                                var longName = m.Groups["long"].Value.Trim();
                                newSpells.Add(new SpellInfo { Nick = nick, LongName = longName, Mana = mana, Diff = diff, SphereCode = sphereCode, Sphere = string.Empty });
                            }
                            if (newSpells.Count > 0)
                            {
                                profile.ReplaceSpells(newSpells);
                            }
                        }

                        // Heals list (explicit only: minheal, superheal, tolife) - derive if spells present but no explicit list
                        var healCandidates = new[] { "minheal", "superheal", "tolife" };
                        var heals = profile.Spells
                            .Where(s => healCandidates.Contains(s.Nick, StringComparer.OrdinalIgnoreCase))
                            .Select(s => new HealSpell { Short = s.Nick.Substring(0, Math.Min(3, s.Nick.Length)), Spell = s.Nick, Heals = s.Nick.Equals("minheal", StringComparison.OrdinalIgnoreCase) ? 250 : s.Nick.Equals("superheal", StringComparison.OrdinalIgnoreCase) ? 500 : 1000 })
                            .ToList();
                        if (heals.Count > 0)
                        {
                            profile.ReplaceHeals(heals);
                        }

                        // Inventory parsing from Encumbrance to Armed line: merge into CSV
                        int encIdx = lines.FindIndex(l => l.StartsWith("Encumbrance:", StringComparison.OrdinalIgnoreCase));
                        int armedIdx = lines.FindIndex(l => l.StartsWith("You are armed with", StringComparison.OrdinalIgnoreCase));
                        if (encIdx >= 0 && armedIdx > encIdx)
                        {
                            var rawLines = new List<string>();
                            for (int i = encIdx + 1; i < armedIdx; i++)
                            {
                                var raw = lines[i].Trim();
                                if (string.IsNullOrWhiteSpace(raw)) continue;
                                if (raw.Contains("[Hp=")) continue;
                                if (Regex.IsMatch(raw, @"^[A-Z][a-z]+:")) continue;
                                rawLines.Add(raw);
                            }
                            if (rawLines.Count > 0)
                            {
                                var blob = string.Join(", ", rawLines);
                                var items = blob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                    .Where(s => s.Length > 1)
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                                if (items.Count > 0) profile.ReplaceInventory(items);
                            }
                        }

                        // Armed with parsing
                        var armedLine = lines.FirstOrDefault(l => l.StartsWith("You are armed with", StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrWhiteSpace(armedLine))
                        {
                            var weapon = Regex.Replace(armedLine, @"^You are armed with\s*", "", RegexOptions.IgnoreCase).Trim();
                            if (!string.IsNullOrWhiteSpace(weapon) && weapon != profile.Player.ArmedWith)
                            {
                                profile.Player.ArmedWith = weapon; profile.SetIdentity(profile.Player.Name, profile.Player.Race, profile.Player.Walk, profile.Player.Class, profile.Player.Level);
                            }
                        }
                        // Incarnations parsing integration
                        if (text.Contains("Incarnations", StringComparison.OrdinalIgnoreCase))
                        {
                            var store = sp.GetRequiredService<CharacterProfileStore>();
                            foreach (var entry in store.ParseIncarnations(text))
                            {
                                store.EnsureCharacter(userSelection.SelectedUser ?? "default", entry.name, entry.house);
                            }
                        }

                        // XP parsing (enhanced for [Cur:.. Nxt: .. Left: ####])
                        foreach (var ln in lines)
                        {
                            var mPanel = Regex.Match(ln, @"\[Cur:\s*(?<cur>\d+)\s+Nxt:\s*(?<nxt>\d+)\s+Left:\s*(?<left>\d+)\]", RegexOptions.IgnoreCase);
                            if (mPanel.Success)
                            {
                                if (long.TryParse(mPanel.Groups["cur"].Value, out var curXp)) profile.SetExperience(curXp);
                                if (long.TryParse(mPanel.Groups["left"].Value, out var leftXp)) profile.SetXpLeft(leftXp);
                                continue;
                            }
                        }
                        PersistCharacterIfNew();
                    }

                    client.LineReceived += line =>
                    {
                        try
                        {
                            roomTracker.AddLine(line);
                            combatTracker.ProcessLine(line);

                            // Minimal single-line fallback for name/class if not yet set
                            if (profile.Player.Name.Length == 0 || profile.Player.Class.Length == 0)
                            {
                                var mc = Regex.Match(line, @"Name\s*:?\s*(?<nm>[A-Za-z][A-Za-z]+).{0,40}Class\s*:?\s*(?<cls>[A-Za-z]+)", RegexOptions.IgnoreCase);
                                if (mc.Success)
                                {
                                    profile.SetNameClass(mc.Groups["nm"].Value.Trim(), mc.Groups["cls"].Value.Trim());
                                    PersistCharacterIfNew();
                                }
                            }

                            // After any line we can snapshot parse (cheap enough; throttle with simple modulus)
                            if (Environment.TickCount % 7 == 0)
                            {
                                ParseScreenSnapshot();
                            }

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

                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        await System.Threading.Tasks.Task.Delay(4000);
                        TrySendInitial();
                    });
                    return client;
                });

                services.AddSingleton<PlayerProfile>();
                services.AddSingleton<AutomationFeatureService>(sp =>
                {
                    var stats = sp.GetRequiredService<StatsTracker>();
                    var profile = sp.GetRequiredService<PlayerProfile>();
                    var client = sp.GetRequiredService<TelnetClient>();
                    var room = sp.GetRequiredService<RoomTracker>();
                    var combat = sp.GetRequiredService<CombatTracker>();
                    var charStore = sp.GetRequiredService<CharacterProfileStore>();
                    var logger = sp.GetRequiredService<ILogger<AutomationFeatureService>>();
                    
                    return new AutomationFeatureService(stats, profile, client, room, combat, charStore, logger);
                });

                // ViewModels / dialogs
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<CredentialsViewModel>();
                services.AddTransient<CharacterProfilesViewModel>();
                services.AddTransient<CharacterSheetViewModel>();
                services.AddTransient<HotKeysViewModel>();

                services.AddSingleton<StatsViewModel>();
                services.AddSingleton<RoomViewModel>();
                services.AddSingleton<CombatViewModel>();
                services.AddSingleton<MainViewModel>();

                services.AddSingleton<MainWindow>(sp =>
                {
                    _ = sp.GetRequiredService<AutomationFeatureService>();
                    
                    // Add WPF log provider to logging after DI container is built
                    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                    var wpfLogProvider = sp.GetRequiredService<WpfLogProvider>();
                    loggerFactory.AddProvider(wpfLogProvider);
                    
                    var w = new MainWindow { DataContext = sp.GetRequiredService<MainViewModel>(), LogVm = sp.GetRequiredService<LogViewModel>() };
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

