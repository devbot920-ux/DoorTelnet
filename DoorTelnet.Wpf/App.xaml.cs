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
using DoorTelnet.Core.Session; // NEW: Add session management
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
                lb.SetMinimumLevel(LogLevel.Trace);
            })
            .ConfigureServices((ctx, services) =>
            {
                var config = ctx.Configuration;
                services.AddSingleton<IConfiguration>(config);
                
                // logging provider registration
                services.AddSingleton<LogBuffer>(_ => new LogBuffer(500));
                services.AddSingleton<WpfLogProvider>();
                services.AddSingleton<LogViewModel>();

                // NEW: Game session manager - registered early
                services.AddSingleton<GameSessionManager>(sp => 
                    new GameSessionManager(sp.GetRequiredService<ILogger<GameSessionManager>>()));

                // Core singletons
                services.AddSingleton<ScreenBuffer>(_ => new ScreenBuffer(
                    int.TryParse(config["terminal:cols"], out var c) ? c : 80,
                    int.TryParse(config["terminal:rows"], out var r) ? r : 25));
                services.AddSingleton<RuleEngine>();
                services.AddSingleton<ScriptEngine>();
                services.AddSingleton<StatsTracker>();
                services.AddSingleton<RoomTracker>(sp =>
                {
                    var roomTracker = new RoomTracker();
                    var screenBuffer = sp.GetRequiredService<ScreenBuffer>();
                    roomTracker.SetScreenBuffer(screenBuffer);
                    return roomTracker;
                });
                services.AddSingleton<CombatTracker>(sp =>
                {
                    var roomTracker = sp.GetRequiredService<RoomTracker>();
                    var combatTracker = new CombatTracker(roomTracker);
                    roomTracker.CombatTracker = combatTracker;
                    return combatTracker;
                });
                services.AddSingleton<PlayerProfile>();
                services.AddSingleton<ISettingsService, SettingsService>();
                services.AddSingleton(sp => new CredentialStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoorTelnet", "credentials.json")));
                services.AddSingleton(sp => new CharacterProfileStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DoorTelnet", "characters.json")));
                services.AddSingleton<UserSelectionService>();

                // Navigation services
                services.AddSingleton<DoorTelnet.Core.Navigation.Services.GraphDataService>();
                services.AddSingleton<DoorTelnet.Core.Navigation.Services.PathfindingService>();
                services.AddSingleton<DoorTelnet.Core.Navigation.Services.RoomMatchingService>();
                services.AddSingleton<DoorTelnet.Core.Navigation.Services.MovementQueueService>();
                services.AddSingleton<DoorTelnet.Core.Navigation.Services.NavigationService>();

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
                    var combatDebug = bool.TryParse(cfg["diagnostics:combatCleaning"], out var cd) && cd;
                    var roomDebug = bool.TryParse(cfg["diagnostics:roomCleaning"], out var rd) && rd;
                    var colorLogging = bool.TryParse(cfg["diagnostics:colorLogging"], out var cl) && cl;
                    var roomColorLogging = bool.TryParse(cfg["diagnostics:roomColorLogging"], out var rcl) && rcl;
                    
                    // Configure debug logging
                    DoorTelnet.Core.Combat.CombatLineParser.SetDebugLogging(combatDebug);
                    DoorTelnet.Core.World.RoomTextProcessor.SetDebugLogging(roomDebug);
                    DoorTelnet.Core.Terminal.AnsiParser.EnableColorLogging = colorLogging;
                    DoorTelnet.Core.World.RoomParser.EnableColorLogging = roomColorLogging;

                    // NEW: Get game session manager
                    var sessionManager = sp.GetRequiredService<GameSessionManager>();
                    
                    var roomTracker = sp.GetRequiredService<RoomTracker>();
                    var combatTracker = sp.GetRequiredService<CombatTracker>();
                    var userSelection = sp.GetRequiredService<UserSelectionService>();
                    var characterStore = sp.GetRequiredService<CharacterProfileStore>();
                    
                    var screenField = typeof(ScriptEngine).GetField("_screen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    var client = new TelnetClient(cols, rows, script, rules, logger, diagnostics, raw, dumb, stats);

                    // NEW: Hook up session manager events
                    sessionManager.RequestEnterCommand += () =>
                    {
                        logger.LogInformation("Session manager requesting enter command");
                        client.SendCommand(""); // Send enter
                    };

                    DateTime lastXpSent = DateTime.MinValue;
                    combatTracker.RequestExperienceCheck += () =>
                    {
                        // NEW: Only process if in game
                        if (!sessionManager.ShouldProcessGameData()) return;
                        
                        if ((DateTime.UtcNow - lastXpSent).TotalMilliseconds < 700) return;
                        lastXpSent = DateTime.UtcNow;
                        client.SendCommand("xp");
                        logger.LogDebug("XP requested after combat event");
                    };

                    bool initialCommandsSent = false;
                    void SendInitialCore()
                    {
                        // NEW: Only send if in game
                        if (!sessionManager.ShouldProcessGameData())
                        {
                            logger.LogDebug("Skipping initial commands - not in game state");
                            return;
                        }
                        
                        client.SendCommand("inv");
                        client.SendCommand("st2");
                        client.SendCommand("stats");
                        client.SendCommand("spells");
                        client.SendCommand("inv");
                        client.SendCommand("xp");
                        logger.LogInformation("Initial data commands dispatched (inv, st2, stats, spells, inv, xp)");
                        
                        _ = System.Threading.Tasks.Task.Run(async () =>
                        {
                            await System.Threading.Tasks.Task.Delay(2000);
                            if ((DateTime.UtcNow - lastXpSent).TotalMilliseconds >= 1500)
                            {
                                lastXpSent = DateTime.UtcNow;
                                client.SendCommand("xp");
                                logger.LogDebug("Follow-up XP request after initial commands");
                            }
                        });
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

                    PlayerProfile profile = sp.GetRequiredService<PlayerProfile>();

                    void PersistCharacterIfNew()
                    {
                        try
                        {
                            var username = userSelection.SelectedUser ?? sp.GetRequiredService<ISettingsService>().Get().Connection.LastUsername;
                            if (string.IsNullOrWhiteSpace(username)) username = "default";
                            var name = profile.Player.Name;
                            if (string.IsNullOrWhiteSpace(name)) return;
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
                        // NEW: Only parse if in game
                        if (!sessionManager.ShouldProcessGameData()) return;
                        
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
                        // NEW PATTERN: "Items" (green) + ":" (white), items (no color) with "," (green), ending in "." (green)
                        // Items might wrap across lines
                        int encIdx = lines.FindIndex(l => l.StartsWith("Encumbrance:", StringComparison.OrdinalIgnoreCase));
                        int armedIdx = lines.FindIndex(l => l.StartsWith("You are armed with", StringComparison.OrdinalIgnoreCase));
                        
                        if (encIdx >= 0 && armedIdx > encIdx)
                        {
                            // Method 1: Look for "Items:" marker with color detection
                            var itemsHeaderIdx = -1;
                            for (int i = encIdx + 1; i < armedIdx; i++)
                            {
                                if (lines[i].Trim().StartsWith("Items", StringComparison.OrdinalIgnoreCase))
                                {
                                    itemsHeaderIdx = i;
                                    break;
                                }
                            }
                            
                            if (itemsHeaderIdx >= 0)
                            {
                                // Parse items from after "Items:" until we hit the armed line or another section
                                var rawLines = new List<string>();
                                for (int i = itemsHeaderIdx; i < armedIdx; i++)
                                {
                                    var raw = lines[i].Trim();
                                    if (string.IsNullOrWhiteSpace(raw)) continue;
                                    if (raw.Contains("[Hp=")) continue;
                                    if (i == itemsHeaderIdx)
                                    {
                                        // Remove "Items:" prefix
                                        var colonIdx = raw.IndexOf(':');
                                        if (colonIdx >= 0 && colonIdx < raw.Length - 1)
                                        {
                                            raw = raw.Substring(colonIdx + 1).Trim();
                                        }
                                        else
                                        {
                                            continue; // Just the header, no items on same line
                                        }
                                    }
                                    if (!string.IsNullOrWhiteSpace(raw))
                                    {
                                        rawLines.Add(raw);
                                    }
                                }
                                
                                if (rawLines.Count > 0)
                                {
                                    // Join all lines and split by commas
                                    var blob = string.Join(" ", rawLines);
                                    
                                    // Remove trailing period if present
                                    blob = blob.TrimEnd('.', ' ');
                                    
                                    // Split by commas and clean up
                                    var items = blob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                        .Where(s => s.Length > 1)
                                        .Select(s => s.Trim())
                                        .Distinct(StringComparer.OrdinalIgnoreCase)
                                        .ToList();
                                    
                                    if (items.Count > 0) 
                                    {
                                        profile.ReplaceInventory(items);
                                        System.Diagnostics.Debug.WriteLine($"📦 INVENTORY PARSED (Items: header): {items.Count} items found");
                                    }
                                }
                            }
                            else
                            {
                                // Fallback: Use old method for backwards compatibility
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
                                    if (items.Count > 0) 
                                    {
                                        profile.ReplaceInventory(items);
                                        System.Diagnostics.Debug.WriteLine($"📦 INVENTORY PARSED (fallback): {items.Count} items found");
                                    }
                                }
                            }
                        }

                        // Armed with parsing - "You are armed with a {item name}." (green)
                        var armedLine = lines.FirstOrDefault(l => l.StartsWith("You are armed with", StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrWhiteSpace(armedLine))
                        {
                            // Extract the item name: "You are armed with a {name}."
                            var match = Regex.Match(armedLine, @"^You are armed with\s+(?:a|an)\s+(.+?)\.?\s*$", RegexOptions.IgnoreCase);
                            if (match.Success)
                            {
                                var weapon = match.Groups[1].Value.Trim();
                                if (!string.IsNullOrWhiteSpace(weapon) && weapon != profile.Player.ArmedWith)
                                {
                                    profile.Player.ArmedWith = weapon; 
                                    profile.SetIdentity(profile.Player.Name, profile.Player.Race, profile.Player.Walk, profile.Player.Class, profile.Player.Level);
                                    System.Diagnostics.Debug.WriteLine($"⚔️ ARMED WITH: '{weapon}'");
                                }
                            }
                            else
                            {
                                // Fallback to old method
                                var weapon = Regex.Replace(armedLine, @"^You are armed with\s*", "", RegexOptions.IgnoreCase).Trim();
                                weapon = weapon.TrimEnd('.', ' ');
                                if (!string.IsNullOrWhiteSpace(weapon) && weapon != profile.Player.ArmedWith)
                                {
                                    profile.Player.ArmedWith = weapon; 
                                    profile.SetIdentity(profile.Player.Name, profile.Player.Race, profile.Player.Walk, profile.Player.Class, profile.Player.Level);
                                    System.Diagnostics.Debug.WriteLine($"⚔️ ARMED WITH (fallback): '{weapon}'");
                                }
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
                            // NEW: Always let session manager see lines for state detection
                            sessionManager.ProcessLine(line);
                            
                            // NEW: Always parse stats lines regardless of game state
                            // This ensures the UI updates immediately when stats appear
                            stats.ParseIfStatsLine(line);
                            
                            // NEW: Gate tracking data processing
                            if (sessionManager.ShouldProcessGameData())
                            {
                                roomTracker.AddLine(line);
                                combatTracker.ProcessLine(line);

                                // Minimal single-line fallback for name/class
                                if (profile.Player.Name.Length == 0 || profile.Player.Class.Length == 0)
                                {
                                    var mc = Regex.Match(line, @"Name\s*:?\s*(?<nm>[A-Za-z][A-ZaZ]+).{0,40}Class\s*:?\s*(?<cls>[A-Za-z]+)", RegexOptions.IgnoreCase);
                                    if (mc.Success)
                                    {
                                        profile.SetNameClass(mc.Groups["nm"].Value.Trim(), mc.Groups["cls"].Value.Trim());
                                        PersistCharacterIfNew();
                                    }
                                }

                                // Snapshot parse
                                if (Environment.TickCount % 7 == 0)
                                {
                                    ParseScreenSnapshot();
                                }

                                // XP processing
                                var xpMatch = Regex.Match(line, @"\[Cur:\s*(?<cur>\d+)\s+Nxt:\s*(?<nxt>\d+)\s+Left:\s*(?<left>\d+)\]", RegexOptions.IgnoreCase);
                                if (xpMatch.Success)
                                {
                                    if (long.TryParse(xpMatch.Groups["cur"].Value, out var curXp)) 
                                    {
                                        profile.SetExperience(curXp);
                                        logger.LogDebug("XP updated from line: Current={currentXp}", curXp);
                                    }
                                    if (long.TryParse(xpMatch.Groups["left"].Value, out var leftXp)) 
                                    {
                                        profile.SetXpLeft(leftXp);
                                        logger.LogDebug("XP Left updated from line: Left={leftXp}", leftXp);
                                    }
                                    PersistCharacterIfNew();
                                }

                                var screen = screenField?.GetValue(script) as ScreenBuffer;
                                if (screen != null)
                                {
                                    roomTracker.TryUpdateRoom("defaultUser", "defaultChar", screen.ToText());
                                }
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
                services.AddSingleton<NavigationFeatureService>();
                
                services.AddSingleton<AutomationFeatureService>(sp =>
                {
                    var stats = sp.GetRequiredService<StatsTracker>();
                    var profile = sp.GetRequiredService<PlayerProfile>();
                    var client = sp.GetRequiredService<TelnetClient>();
                    var room = sp.GetRequiredService<RoomTracker>();
                    var combat = sp.GetRequiredService<CombatTracker>();
                    var charStore = sp.GetRequiredService<CharacterProfileStore>();
                    var navigationService = sp.GetRequiredService<NavigationFeatureService>();
                    var logger = sp.GetRequiredService<ILogger<AutomationFeatureService>>();
                    
                    return new AutomationFeatureService(stats, profile, client, room, combat, charStore, logger, navigationService);
                });

                // ViewModels / dialogs
                services.AddTransient<SettingsViewModel>();
                services.AddTransient<CredentialsViewModel>();
                services.AddTransient<CharacterProfilesViewModel>();
                services.AddTransient<CharacterSheetViewModel>();
                services.AddTransient<HotKeysViewModel>();

                services.AddSingleton<StatsViewModel>();
                services.AddSingleton<RoomViewModel>(sp =>
                {
                    var roomTracker = sp.GetRequiredService<RoomTracker>();
                    var logger = sp.GetRequiredService<ILogger<RoomViewModel>>();
                    var navigationService = sp.GetRequiredService<NavigationFeatureService>();
                    var roomMatchingService = sp.GetRequiredService<DoorTelnet.Core.Navigation.Services.RoomMatchingService>();
                    return new RoomViewModel(roomTracker, logger, navigationService, roomMatchingService);
                });
                services.AddSingleton<CombatViewModel>();
                services.AddSingleton<MainViewModel>();

                services.AddSingleton<MainWindow>(sp =>
                {
                    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
                    var wpfLogProvider = sp.GetRequiredService<WpfLogProvider>();
                    loggerFactory.AddProvider(wpfLogProvider);
                    
                    _ = sp.GetRequiredService<AutomationFeatureService>();
                    
                    var roomTracker = sp.GetRequiredService<RoomTracker>();
                    var roomVm = sp.GetRequiredService<RoomViewModel>();
                    roomTracker.RoomChanged += _ => Current?.Dispatcher.BeginInvoke(roomVm.Refresh);
                    
                    // NEW: Hook up session manager to MainViewModel for character data clearing
                    var sessionManager = sp.GetRequiredService<GameSessionManager>();
                    var mainViewModel = sp.GetRequiredService<MainViewModel>();
                    sessionManager.RequestClearCharacterData += () =>
                    {
                        Current?.Dispatcher.Invoke(() =>
                        {
                            mainViewModel.ClearAllCharacterData();
                        });
                    };
                    
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var graphService = sp.GetRequiredService<DoorTelnet.Core.Navigation.Services.GraphDataService>();
                            var graphPath = Path.Combine(AppContext.BaseDirectory, "graph.json");
                            
                            if (File.Exists(graphPath))
                            {
                                var logger = sp.GetRequiredService<ILogger<App>>();
                                logger.LogInformation("Loading navigation graph data...");
                                
                                var success = await graphService.LoadGraphDataAsync(graphPath);
                                if (success)
                                {
                                    logger.LogInformation("Navigation graph loaded successfully: {NodeCount} nodes, {EdgeCount} edges", 
                                        graphService.NodeCount, graphService.EdgeCount);
                                }
                                else
                                {
                                    logger.LogWarning("Failed to load navigation graph data");
                                }
                            }
                            else
                            {
                                var logger = sp.GetRequiredService<ILogger<App>>();
                                logger.LogWarning("Graph data file not found: {GraphPath}", graphPath);
                            }
                        }
                        catch (Exception ex)
                        {
                            var logger = sp.GetRequiredService<ILogger<App>>();
                            logger.LogError(ex, "Error loading navigation graph data");
                        }
                    });
                    
                    var w = new MainWindow { DataContext = mainViewModel, LogVm = sp.GetRequiredService<LogViewModel>() };
                    var settings = sp.GetRequiredService<ISettingsService>();
                    var ui = settings.Get().UI;
                    if (ui.Width > 400 && ui.Height > 300)
                    {
                        w.Width = ui.Width; w.Height = ui.Height;
                    }
                    
                    // Test logging immediately to verify it works
                    var testLogger = sp.GetRequiredService<ILogger<MainWindow>>();
                    testLogger.LogInformation("DoorTelnet application started successfully");
                    testLogger.LogDebug("Debug logging is working");
                    testLogger.LogTrace("Trace logging is working");
                    
                    return w;
                });

                // Game API Service
                if (bool.TryParse(config["api:enabled"], out var apiEnabled) && apiEnabled)
                {
                    var apiPort = int.TryParse(config["api:port"], out var ap) ? ap : 5000;
                    services.AddSingleton(sp => new GameApiService(
                        sp.GetRequiredService<TelnetClient>(),
                        sp.GetRequiredService<StatsTracker>(),
                        sp.GetRequiredService<RoomTracker>(),
                        sp.GetRequiredService<CombatTracker>(),
                        sp.GetRequiredService<PlayerProfile>(),
                        sp.GetRequiredService<NavigationFeatureService>(),
                        sp.GetRequiredService<ILogger<GameApiService>>(),
                        apiPort
                    ));
                }
            })
            .Build();

        _host.Start();
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        
        if (bool.TryParse(_host.Services.GetRequiredService<IConfiguration>()["api:enabled"], out var apiEnabledStart) && apiEnabledStart)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var apiService = _host.Services.GetRequiredService<GameApiService>();
                    await apiService.StartAsync();
                }
                catch (Exception ex)
                {
                    var logger = _host.Services.GetRequiredService<ILogger<App>>();
                    logger.LogError(ex, "Game API Service failed to start");
                }
            });
        }
        
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

