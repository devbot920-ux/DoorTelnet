using System.Text;
using System.Windows.Forms;
using DoorTelnet.Core.Automation;
using DoorTelnet.Core.Player;
using DoorTelnet.Core.World;
using DoorTelnet.Core.Terminal;
using DoorTelnet.Core.Combat;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Cli;

public class StatsForm : Form
{
    private readonly StatsTracker _stats; 
    private readonly PlayerProfile _profile; 
    private readonly UiLogProvider _logProvider;
    private readonly ScreenBuffer _screen;
    private readonly ILogger _logger;
    private readonly CredentialStore _creds;
    private CombatTracker? _combatTracker; // Add combat tracker reference
    
    private readonly MenuStrip _menuStrip = new() { Dock = DockStyle.Top };
    private readonly StatusStrip _statusStrip = new() { Dock = DockStyle.Bottom };
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _playerStatusLabel = new();
    private readonly SplitContainer _mainSplitter = new() { Orientation = Orientation.Horizontal };
    private readonly SplitContainer _topSplitter = new() { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
    private readonly SplitContainer _rightSplitter = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal }; // Add right splitter for combat stats
    
    private readonly Panel _statsPanel = new();
    private readonly Panel _roomPanel = new();
    private readonly Panel _combatPanel = new(); // Add combat panel
    private readonly Panel _logPanel = new();
    private Panel _buttonPanel = new(); 
    
    private readonly Label _statsHeader = new() 
    { 
        Text = "Character Stats", 
        Font = new Font("Segoe UI", 10, FontStyle.Bold), 
        Dock = DockStyle.Top, 
        Height = 25, 
        TextAlign = ContentAlignment.MiddleLeft, 
        BackColor = Color.LightBlue, 
        Padding = new Padding(5, 5, 5, 0) 
    };
    
    private readonly TextBox _txtPlayerStats = new() 
    { 
        Multiline = true, 
        Dock = DockStyle.Fill, 
        ReadOnly = true, 
        ScrollBars = ScrollBars.Vertical, 
        Font = new Font("Consolas", 9), 
        BackColor = Color.White, 
        BorderStyle = BorderStyle.None, 
        WordWrap = true 
    };
    
    private readonly Label _roomHeader = new() 
    { 
        Text = "Current Room", 
        Font = new Font("Segoe UI", 10, FontStyle.Bold), 
        Dock = DockStyle.Top, 
        Height = 25, 
        TextAlign = ContentAlignment.MiddleLeft, 
        BackColor = Color.LightGreen, 
        Padding = new Padding(5, 5, 5, 0) 
    };
    
    private readonly TextBox _txtRoom = new() 
    { 
        Multiline = true, 
        Dock = DockStyle.Fill, 
        ReadOnly = true, 
        ScrollBars = ScrollBars.Vertical, 
        Font = new Font("Consolas", 9), 
        BackColor = Color.White, 
        BorderStyle = BorderStyle.None, 
        WordWrap = true 
    };
    
    // Add combat statistics UI components
    private readonly Label _combatHeader = new() 
    { 
        Text = "Combat Statistics", 
        Font = new Font("Segoe UI", 10, FontStyle.Bold), 
        Dock = DockStyle.Top, 
        Height = 25, 
        TextAlign = ContentAlignment.MiddleLeft, 
        BackColor = Color.LightSalmon, 
        Padding = new Padding(5, 5, 5, 0) 
    };
    
    private readonly TextBox _txtCombatStats = new() 
    { 
        Multiline = true, 
        Dock = DockStyle.Fill, 
        ReadOnly = true, 
        ScrollBars = ScrollBars.Vertical, 
        Font = new Font("Consolas", 9), 
        BackColor = Color.White, 
        BorderStyle = BorderStyle.None, 
        WordWrap = true 
    };
    
    private readonly Label _logHeader = new() 
    { 
        Text = "Session Log", 
        Font = new Font("Segoe UI", 10, FontStyle.Bold), 
        Dock = DockStyle.Top, 
        Height = 25, 
        TextAlign = ContentAlignment.MiddleLeft, 
        BackColor = Color.LightCoral, 
        Padding = new Padding(5, 5, 5, 0) 
    };
    
    private readonly ListBox _lstLog = new() 
    { 
        Dock = DockStyle.Fill, 
        SelectionMode = SelectionMode.MultiExtended, 
        Font = new Font("Consolas", 8), 
        BorderStyle = BorderStyle.None, 
        HorizontalScrollbar = true, 
        IntegralHeight = false 
    };
    
    private readonly Button _btnSendUser = new() 
    { 
        Text = "Send Username", 
        Size = new Size(100, 30), 
        BackColor = Color.FromArgb(173, 216, 230), 
        FlatStyle = FlatStyle.Flat, 
        Cursor = Cursors.Hand 
    };
    
    private readonly Button _btnSendPass = new() 
    { 
        Text = "Send Password", 
        Size = new Size(100, 30), 
        BackColor = Color.FromArgb(144, 238, 144), 
        FlatStyle = FlatStyle.Flat, 
        Cursor = Cursors.Hand 
    };
    
    private readonly Button _btnClearLog = new() 
    { 
        Text = "Clear Log", 
        Size = new Size(80, 30), 
        BackColor = Color.FromArgb(240, 128, 128), 
        FlatStyle = FlatStyle.Flat, 
        Cursor = Cursors.Hand 
    };
    
    private readonly Button _btnClearCombat = new() // Add clear combat stats button
    { 
        Text = "Clear Combat", 
        Size = new Size(85, 30), 
        BackColor = Color.FromArgb(255, 182, 193), 
        FlatStyle = FlatStyle.Flat, 
        Cursor = Cursors.Hand 
    };
    
    private readonly ComboBox _cmbUsers = new() 
    { 
        DropDownStyle = ComboBoxStyle.DropDownList, 
        Size = new Size(130, 25), 
        Margin = new Padding(2) 
    };
    
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _combatTimer = new() { Interval = 2000 }; // Update combat stats every 2 seconds
    
    private readonly CheckBox _chkAutoGong = new() 
    { 
        Text = "Auto Gong", 
        AutoSize = true, 
        Margin = new Padding(8, 8, 4, 4) 
    };
    
    private readonly CheckBox _chkPickupGold = new() 
    { 
        Text = "Gold", 
        AutoSize = true, 
        Margin = new Padding(4, 8, 4, 4) 
    };
    
    private readonly CheckBox _chkPickupSilver = new() 
    { 
        Text = "Silver", 
        AutoSize = true, 
        Margin = new Padding(4, 8, 12, 4) 
    };
    
    private readonly CheckBox _chkShielded = new() 
    { 
        Text = "Shielded", 
        AutoSize = true, 
        Margin = new Padding(4, 8, 8, 4) 
    };
    
    private readonly Label _lblGongHp = new() 
    { 
        Text = "Min HP %:", 
        AutoSize = true, 
        Margin = new Padding(4, 10, 2, 4) 
    };
    
    private readonly NumericUpDown _numGongHp = new() 
    { 
        Minimum = 10, 
        Maximum = 100, 
        Value = 60, 
        Width = 50, 
        Margin = new Padding(2, 6, 8, 4) 
    };
    
    private readonly CheckBox _chkAutoHeal = new() 
    { 
        Text = "Auto Heal", 
        AutoSize = true, 
        Margin = new Padding(8, 8, 4, 4) 
    };
    
    private readonly Label _lblCriticalHp = new() 
    { 
        Text = "Critical %:", 
        AutoSize = true, 
        Margin = new Padding(4, 10, 2, 4) 
    };
    
    private readonly NumericUpDown _numCriticalHp = new() 
    { 
        Minimum = 5, 
        Maximum = 50, 
        Value = 25, 
        Width = 50, 
        Margin = new Padding(2, 6, 8, 4) 
    };
    
    // Add auto-heal threshold control
    private readonly Label _lblAutoHealHp = new() 
    { 
        Text = "Heal %:", 
        AutoSize = true, 
        Margin = new Padding(4, 10, 2, 4) 
    };
    
    private readonly NumericUpDown _numAutoHealHp = new() 
    { 
        Minimum = 30, 
        Maximum = 90, 
        Value = 70, 
        Width = 50, 
        Margin = new Padding(2, 6, 8, 4) 
    };
    
    // Events public
    public event Action? SendUsernameRequested;
    public event Action? SendPasswordRequested;
    public event Action<DebugSettings>? DebugSettingsChanged;
    public event Action<string>? UserSelected;
    public event Action? DisconnectRequested;
    public event Action? ReconnectRequested;
    
    private DebugSettings _currentSettings = new();
    private string? _selectedUser;
    private PlayerStatsStore.StatSnapshot? _lastSnap;
    private bool _userAdjustedMainSplitter;
    private bool _userAdjustedTopSplitter;
    
    public StatsForm(StatsTracker stats, PlayerProfile profile, UiLogProvider logProvider, ScreenBuffer screen, ILogger logger, CredentialStore creds)
    { 
        _logProvider = logProvider; 
        _stats = stats; 
        _profile = profile; 
        _screen = screen; 
        _logger = logger; 
        _creds = creds;
        
        InitializeForm();
        SetupMenus();
        SetupLayout();
        SetupEventHandlers();
        SetupContextMenus();
        ApplyModernStyling();
        
        _timer.Start();
        _combatTimer.Start(); // Start combat stats timer
        _logProvider.Message += OnLog;
        LoadCredentialUsers();
        
        // Initialize combat stats display
        RefreshCombatStats();
    }
    
    /// <summary>
    /// Initialize the form with current settings (called from main application after loading saved settings)
    /// </summary>
    public void InitializeWithSettings(DebugSettings settings)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action<DebugSettings>(InitializeWithSettings), settings);
            return;
        }
        
        try
        {
            _currentSettings = settings;
            UpdateMenuItemStates();
            Log($"[Settings] Initialized with saved settings: CursorStyle={settings.CursorStyle}, Enhanced={settings.EnhancedStatsLineCleaning}");
        }
        catch (Exception ex)
        {
            Log($"[Settings] Error initializing with settings: {ex.Message}");
        }
    }

    // Combat Statistics Methods
    private void RefreshCombatStats()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(RefreshCombatStats));
            return;
        }
        
        if (_combatTracker == null)
        {
            _txtCombatStats.Text = "⚔️ Combat Statistics\n\nCombat tracker not available...";
            return;
        }
        
        var stats = _combatTracker.GetStatistics();
        var activeCombats = _combatTracker.ActiveCombats;
        var completedCombats = _combatTracker.CompletedCombats;
        
        var sb = new StringBuilder();
        sb.AppendLine("⚔️ Combat Statistics");
        sb.AppendLine();
        
        // Active combats section
        if (activeCombats.Count > 0)
        {
            sb.AppendLine($"🔥 Active Combats ({activeCombats.Count}):");
            foreach (var combat in activeCombats.OrderBy(c => c.StartTime))
            {
                sb.AppendLine($"   • {combat.MonsterName}");
                sb.AppendLine($"     Dealt: {combat.DamageDealt}, Taken: {combat.DamageTaken}");
                sb.AppendLine($"     Duration: {combat.DurationSeconds:F1}s");
                sb.AppendLine();
            }
        }
        else
        {
            sb.AppendLine("🔥 Active Combats: None");
            sb.AppendLine();
        }
        
        // Overall statistics
        sb.AppendLine("📊 Session Summary:");
        sb.AppendLine($"   Total Combats: {stats.TotalCombats}");
        sb.AppendLine($"   Victories: {stats.Victories} ({stats.WinRate:P1})");
        sb.AppendLine($"   Deaths: {stats.Deaths} ({stats.DeathRate:P1})");
        sb.AppendLine($"   Fled: {stats.Flees} ({stats.FleeRate:P1})");
        sb.AppendLine();
        
        // Damage statistics
        sb.AppendLine("🗡️ Damage Statistics:");
        sb.AppendLine($"   Total Dealt: {stats.TotalDamageDealt:N0}");
        sb.AppendLine($"   Total Taken: {stats.TotalDamageTaken:N0}");
        sb.AppendLine($"   Avg Dealt/Fight: {stats.AverageDamageDealt:F1}");
        sb.AppendLine($"   Avg Taken/Fight: {stats.AverageDamageTaken:F1}");
        sb.AppendLine();
        
        // Experience statistics
        sb.AppendLine("✨ Experience Statistics:");
        sb.AppendLine($"   Total XP Gained: {stats.TotalExperience:N0}");
        sb.AppendLine($"   Average XP/Fight: {stats.AverageExperience:F1}");
        sb.AppendLine($"   Avg Fight Duration: {stats.AverageDuration:F1}s");
        sb.AppendLine();
        
        // Recent combats (last 5)
        if (completedCombats.Count > 0)
        {
            sb.AppendLine("🕒 Recent Combats:");
            var recent = completedCombats.TakeLast(5).Reverse();
            foreach (var combat in recent)
            {
                var statusIcon = combat.Status switch
                {
                    "Victory" => "🏆",
                    "Death" => "💀",
                    "Fled" => "🏃",
                    _ => "❓"
                };
                
                sb.AppendLine($"   {statusIcon} {combat.MonsterName} ({combat.Status})");
                sb.AppendLine($"     D: {combat.DamageDealt}, T: {combat.DamageTaken}, XP: {combat.ExperienceGained}");
                sb.AppendLine($"     Duration: {combat.DurationSeconds:F1}s");
            }
        }
        
        _txtCombatStats.Text = sb.ToString();
        _combatHeader.Text = $"Combat Statistics - {stats.TotalCombats} Fights";
    }
    
    private void ClearCombatHistory()
    {
        if (_combatTracker != null)
        {
            _combatTracker.ClearHistory();
            RefreshCombatStats();
            Log("[Combat] Combat history cleared");
        }
    }
    
    private void CopyCombatStats()
    {
        try
        {
            if (!string.IsNullOrEmpty(_txtCombatStats.Text))
            {
                Clipboard.SetText(_txtCombatStats.Text);
                Log("[Combat] Statistics copied to clipboard");
            }
        }
        catch (Exception ex)
        {
            Log($"[Combat] Failed to copy statistics: {ex.Message}");
        }
    }
    
    private void ExportCombatStats()
    {
        try
        {
            using var dialog = new SaveFileDialog
            {
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                FileName = $"combat_stats_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
                Title = "Export Combat Statistics"
            };
            
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                File.WriteAllText(dialog.FileName, _txtCombatStats.Text);
                Log($"[Combat] Statistics exported to {dialog.FileName}");
            }
        }
        catch (Exception ex)
        {
            Log($"[Combat] Export failed: {ex.Message}");
            MessageBox.Show($"Failed to export combat statistics: {ex.Message}", "Export Error", 
                           MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    
    private void ShowActiveCombats()
    {
        if (_combatTracker == null)
        {
            Log("[Combat] Combat tracker not available");
            return;
        }
        
        var activeCombats = _combatTracker.ActiveCombats;
        if (activeCombats.Count == 0)
        {
            MessageBox.Show("No active combats.", "Active Combats", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        
        var sb = new StringBuilder();
        sb.AppendLine($"Active Combats ({activeCombats.Count}):");
        sb.AppendLine();
        
        foreach (var combat in activeCombats.OrderBy(c => c.StartTime))
        {
            sb.AppendLine($"Monster: {combat.MonsterName}");
            sb.AppendLine($"Started: {combat.StartTime:HH:mm:ss}");
            sb.AppendLine($"Duration: {combat.DurationSeconds:F1} seconds");
            sb.AppendLine($"Damage Dealt: {combat.DamageDealt}");
            sb.AppendLine($"Damage Taken: {combat.DamageTaken}");
            sb.AppendLine($"Last Activity: {combat.LastDamageTime:HH:mm:ss}");
            sb.AppendLine($"Awaiting XP: {(combat.AwaitingExperience ? "Yes" : "No")}");
            sb.AppendLine();
        }
        
        MessageBox.Show(sb.ToString(), "Active Combats", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void InitializeForm()
    {
        Text = "DoorTelnet Stats";
        Size = new Size(1200, 800);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;
        
        _statusStrip.Items.Add(_statusLabel);
        _statusStrip.Items.Add(new ToolStripStatusLabel { Spring = true });
        _statusStrip.Items.Add(_playerStatusLabel);
        _statusLabel.Text = "Ready";
        _playerStatusLabel.Text = "No player data";
    }

    private void SetupMenus()
    {
        var fileMenu = new ToolStripMenuItem("&File");
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("&Disconnect", null, (s, e) => RequestDisconnect()) 
        { 
            ShortcutKeys = Keys.Control | Keys.D 
        });
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("&Reconnect", null, (s, e) => RequestReconnect()) 
        { 
            ShortcutKeys = Keys.Control | Keys.R 
        });
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("&Manage Users...", null, (s, e) => ShowUserManagementDialog()) 
        { 
            ShortcutKeys = Keys.Control | Keys.U 
        });
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("&Settings...", null, ShowSettingsDialog) 
        { 
            ShortcutKeys = Keys.Control | Keys.S 
        });
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (s, e) => Close()) 
        { 
            ShortcutKeys = Keys.Alt | Keys.F4 
        });

        var combatMenu = new ToolStripMenuItem("&Combat");
        combatMenu.DropDownItems.Add(new ToolStripMenuItem("&Clear Combat History", null, (s, e) => ClearCombatHistory()) 
        { 
            ShortcutKeys = Keys.Control | Keys.Shift | Keys.C 
        });
        combatMenu.DropDownItems.Add(new ToolStripMenuItem("&Export Combat Stats", null, (s, e) => ExportCombatStats()) 
        { 
            ShortcutKeys = Keys.Control | Keys.E 
        });
        combatMenu.DropDownItems.Add(new ToolStripSeparator());
        combatMenu.DropDownItems.Add(new ToolStripMenuItem("Show &Active Combats", null, (s, e) => ShowActiveCombats()) 
        { 
            ShortcutKeys = Keys.F11 
        });

        var debugMenu = new ToolStripMenuItem("&Debug");
        debugMenu.DropDownItems.Add(new ToolStripMenuItem("&Room Parser Debug", null, (s, e) => TriggerRoomDebug()) 
        { 
            ShortcutKeys = Keys.F10 
        });
        debugMenu.DropDownItems.Add(new ToolStripMenuItem("&Screen Dump", null, (s, e) => TriggerScreenDump()) 
        { 
            ShortcutKeys = Keys.F9 
        });
        debugMenu.DropDownItems.Add(new ToolStripSeparator());
        debugMenu.DropDownItems.Add(new ToolStripMenuItem("&Telnet Diagnostics", null, ToggleTelnetDiagnostics) 
        { 
            CheckOnClick = true 
        });
        debugMenu.DropDownItems.Add(new ToolStripMenuItem("&Raw Echo", null, ToggleRawEcho) 
        { 
            CheckOnClick = true 
        });
        debugMenu.DropDownItems.Add(new ToolStripMenuItem("&Enhanced Stats Cleaning", null, ToggleStatscleanimg) 
        { 
            CheckOnClick = true, 
            Checked = true 
        });

        var viewMenu = new ToolStripMenuItem("&View");
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("&Always on Top", null, ToggleAlwaysOnTop) 
        { 
            CheckOnClick = true 
        });
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Show &Control Buttons", null, ToggleControlButtons) 
        { 
            CheckOnClick = true, 
            Checked = true 
        });
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("&Reset Layout", null, ResetLayout));
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("&Force Layout Refresh", null, ForceLayoutRefresh));
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("&Force Layout Fix", null, ForceLayoutFix));
        viewMenu.DropDownItems.Add(new ToolStripSeparator());
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("Show Layout &Info", null, ShowLayoutInfo));
        viewMenu.DropDownItems.Add(new ToolStripMenuItem("&Fix Docking Issues", null, FixDockingIssues));

        _menuStrip.Items.AddRange(new ToolStripItem[] { fileMenu, combatMenu, debugMenu, viewMenu });
    }
    
    private void SetupLayout()
    {
        SuspendLayout();
        try
        {
            Controls.Clear();
            Controls.Add(_menuStrip);

            _statsPanel.Dock = DockStyle.Fill;
            _statsPanel.Padding = new Padding(1);
            _statsPanel.Controls.Clear();
            _statsPanel.Controls.Add(_txtPlayerStats);
            _statsPanel.Controls.Add(_statsHeader);

            _roomPanel.Dock = DockStyle.Fill;
            _roomPanel.Padding = new Padding(1);
            _roomPanel.Controls.Clear();
            _roomPanel.Controls.Add(_txtRoom);
            _roomPanel.Controls.Add(_roomHeader);

            // Setup combat panel
            _combatPanel.Dock = DockStyle.Fill;
            _combatPanel.Padding = new Padding(1);
            _combatPanel.Controls.Clear();
            _combatPanel.Controls.Add(_txtCombatStats);
            _combatPanel.Controls.Add(_combatHeader);

            _logPanel.Dock = DockStyle.Fill;
            _logPanel.Padding = new Padding(1);
            _logPanel.Controls.Clear();
            _logPanel.Controls.Add(_lstLog);
            _logPanel.Controls.Add(_logHeader);

            // Clear all splitter panels
            _topSplitter.Panel1.Controls.Clear();
            _topSplitter.Panel2.Controls.Clear();
            _rightSplitter.Panel1.Controls.Clear();
            _rightSplitter.Panel2.Controls.Clear();
            _mainSplitter.Panel1.Controls.Clear();
            _mainSplitter.Panel2.Controls.Clear();
            
            // Setup three-panel layout: Stats | Room/Combat | Log
            _topSplitter.Panel1.Controls.Add(_statsPanel);
            _rightSplitter.Panel1.Controls.Add(_roomPanel);
            _rightSplitter.Panel2.Controls.Add(_combatPanel);
            _topSplitter.Panel2.Controls.Add(_rightSplitter);
            _mainSplitter.Panel1.Controls.Add(_topSplitter);
            _mainSplitter.Panel2.Controls.Add(_logPanel);

            _buttonPanel = new Panel 
            { 
                Height = 42, 
                Dock = DockStyle.Bottom, 
                Padding = new Padding(8, 5, 8, 5), 
                BackColor = Color.FromArgb(250, 250, 250) 
            };

            // Login controls (left)
            var loginFlow = new FlowLayoutPanel 
            { 
                Dock = DockStyle.Left, 
                FlowDirection = FlowDirection.LeftToRight, 
                AutoSize = true, 
                WrapContents = false, 
                Margin = new Padding(0), 
                Padding = new Padding(6, 4, 6, 4), 
                BackColor = Color.FromArgb(245, 245, 245) 
            };
            
            loginFlow.Controls.Add(new Label
            { 
                Text = "User:", 
                AutoSize = true, 
                Margin = new Padding(0, 8, 5, 0)
            });
            
            loginFlow.Controls.Add(_cmbUsers);
            loginFlow.Controls.Add(_btnSendUser);
            loginFlow.Controls.Add(_btnSendPass);
            loginFlow.Controls.Add(_btnClearLog);
            loginFlow.Controls.Add(_btnClearCombat); // Add clear combat button

            // Automation controls (right)
            var autoFlow = new FlowLayoutPanel 
            { 
                Dock = DockStyle.Right, 
                FlowDirection = FlowDirection.LeftToRight, 
                AutoSize = true, 
                WrapContents = false, 
                Margin = new Padding(0), 
                Padding = new Padding(6, 4, 6, 4), 
                BackColor = Color.FromArgb(235, 235, 235),
                MaximumSize = new Size(800, 50) // Ensure it doesn't get too wide
            };
            
            autoFlow.Controls.Add(_chkAutoGong);
            autoFlow.Controls.Add(_chkPickupGold);
            autoFlow.Controls.Add(_chkPickupSilver);
            autoFlow.Controls.Add(_chkShielded);
            autoFlow.Controls.Add(_chkAutoHeal);
            autoFlow.Controls.Add(_lblGongHp);
            autoFlow.Controls.Add(_numGongHp);
            autoFlow.Controls.Add(_lblCriticalHp);
            autoFlow.Controls.Add(_numCriticalHp);
            autoFlow.Controls.Add(_lblAutoHealHp);  // Add auto-heal threshold label
            autoFlow.Controls.Add(_numAutoHealHp);  // Add auto-heal threshold control

            _buttonPanel.Controls.Add(autoFlow);
            _buttonPanel.Controls.Add(loginFlow);

            // Add controls in correct docking order: bottom panels first, then positioned main splitter
            Controls.Add(_buttonPanel);
            Controls.Add(_statusStrip);
            Controls.Add(_mainSplitter);
            
            // Position the main splitter manually to fill remaining space
            _mainSplitter.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _mainSplitter.Location = new Point(0, _menuStrip.Height);
            _mainSplitter.Size = new Size(ClientSize.Width, ClientSize.Height - _menuStrip.Height - _statusStrip.Height - _buttonPanel.Height);
        }
        finally 
        { 
            ResumeLayout(true); 
        }

        Shown += SetInitialSplitterPositions;
        Log("Layout initialized");
    }

    private void SetInitialSplitterPositions(object? sender, EventArgs e)
    {
        Shown -= SetInitialSplitterPositions;
        var initTimer = new System.Windows.Forms.Timer { Interval = 300 };
        initTimer.Tick += (s, args) => 
        {
            initTimer.Stop();
            initTimer.Dispose();
            
            // Update the main splitter size to account for any layout changes
            var buttonHeight = _buttonPanel.Visible ? _buttonPanel.Height : 0;
            var availableHeight = ClientSize.Height - _menuStrip.Height - _statusStrip.Height - buttonHeight;
            
            _mainSplitter.Size = new Size(ClientSize.Width, availableHeight);
            SetSplitterPositions(availableHeight);
        };
        initTimer.Start();
    }

    private void SetSplitterPositions(int availableHeight)
    {
        if (availableHeight > 200)
        {
            var target = (int)(availableHeight * 0.75);
            _mainSplitter.SplitterDistance = Math.Max(Math.Min(target, availableHeight - _mainSplitter.Panel2MinSize), _mainSplitter.Panel1MinSize);
        }
        else
        {
            _mainSplitter.SplitterDistance = Math.Max(availableHeight - 100, _mainSplitter.Panel1MinSize);
        }
        
        // Set top splitter to 40% stats, 60% room/combat
        if (_topSplitter.Width > 200) 
        {
            SetSplitterDistanceSafely(_topSplitter, (int)(_topSplitter.Width * 0.4));
        }
        
        // Set right splitter to 50/50 room and combat
        if (_rightSplitter.Height > 100)
        {
            SetSplitterDistanceSafely(_rightSplitter, _rightSplitter.Height / 2);
        }
    }

    private bool SetSplitterDistanceSafely(SplitContainer splitter, int desired)
    { 
        if (splitter.Width <= 0 || splitter.Height <= 0) 
            return false;
            
        int max = (splitter.Orientation == Orientation.Vertical ? splitter.Width : splitter.Height) - splitter.Panel2MinSize;
        splitter.SplitterDistance = Math.Min(Math.Max(desired, splitter.Panel1MinSize), max);
        return true;
    }

    private void SetupEventHandlers()
    {
        _timer.Tick += (_, _) => RefreshSummary();
        _combatTimer.Tick += (_, _) => RefreshCombatStats(); // Add combat stats refresh
        _stats.Updated += () => BeginInvoke(new Action(RefreshSummary));
        
        _btnSendUser.Click += (_, _) => 
        { 
            Log("Send Username"); 
            SendUsernameRequested?.Invoke(); 
        };
        
        _btnSendPass.Click += (_, _) => 
        { 
            Log("Send Password"); 
            SendPasswordRequested?.Invoke(); 
        };
        
        _btnClearLog.Click += (_, _) => ClearLog();
        _btnClearCombat.Click += (_, _) => ClearCombatHistory(); // Add clear combat handler
        _cmbUsers.SelectedIndexChanged += OnUserSelectionChanged;
        
        _chkAutoGong.CheckedChanged += (_, _) => _profile.Features.AutoGong = _chkAutoGong.Checked;
        _chkPickupGold.CheckedChanged += (_, _) => _profile.Features.PickupGold = _chkPickupGold.Checked;
        _chkPickupSilver.CheckedChanged += (_, _) => _profile.Features.PickupSilver = _chkPickupSilver.Checked;
        _chkShielded.CheckedChanged += (_, _) => _profile.Features.AutoShield = _chkShielded.Checked;
        _chkAutoHeal.CheckedChanged += (_, _) => _profile.Features.AutoHeal = _chkAutoHeal.Checked;
        _numGongHp.ValueChanged += (_, _) => _profile.Thresholds.GongMinHpPercent = (int)_numGongHp.Value;
        _numCriticalHp.ValueChanged += (_, _) => _profile.Thresholds.CriticalHpPercent = (int)_numCriticalHp.Value;
        _numAutoHealHp.ValueChanged += (_, _) => _profile.Thresholds.AutoHealHpPercent = (int)_numAutoHealHp.Value; // Add auto-heal threshold handler
        
        // Initialize control values from profile
        _chkAutoGong.Checked = _profile.Features.AutoGong;
        _chkPickupGold.Checked = _profile.Features.PickupGold;
        _chkPickupSilver.Checked = _profile.Features.PickupSilver;
        _chkShielded.Checked = _profile.Features.AutoShield;
        _chkAutoHeal.Checked = _profile.Features.AutoHeal;
        _numGongHp.Value = _profile.Thresholds.GongMinHpPercent;
        _numCriticalHp.Value = _profile.Thresholds.CriticalHpPercent;
        _numAutoHealHp.Value = _profile.Thresholds.AutoHealHpPercent; // Initialize auto-heal threshold value
    }

    private void SetupContextMenus()
    {
        var logMenu = new ContextMenuStrip();
        logMenu.Items.Add(new ToolStripMenuItem("Copy Selected", null, (s, e) => CopySelectedLogEntries()));
        logMenu.Items.Add(new ToolStripMenuItem("Select All", null, (s, e) => SelectAllLogEntries()));
        logMenu.Items.Add(new ToolStripSeparator());
        logMenu.Items.Add(new ToolStripMenuItem("Clear Log", null, (s, e) => ClearLog()));
        _lstLog.ContextMenuStrip = logMenu;
        
        // Add combat stats context menu
        var combatMenu = new ContextMenuStrip();
        combatMenu.Items.Add(new ToolStripMenuItem("Copy Statistics", null, (s, e) => CopyCombatStats()));
        combatMenu.Items.Add(new ToolStripMenuItem("Export to File", null, (s, e) => ExportCombatStats()));
        combatMenu.Items.Add(new ToolStripSeparator());
        combatMenu.Items.Add(new ToolStripMenuItem("Clear History", null, (s, e) => ClearCombatHistory()));
        _txtCombatStats.ContextMenuStrip = combatMenu;
    }

    private void ApplyModernStyling()
    {
        _statsPanel.BorderStyle = BorderStyle.FixedSingle;
        _roomPanel.BorderStyle = BorderStyle.FixedSingle;
        _combatPanel.BorderStyle = BorderStyle.FixedSingle; // Add combat panel styling
        _logPanel.BorderStyle = BorderStyle.FixedSingle;
        _mainSplitter.SplitterWidth = 4;
        _topSplitter.SplitterWidth = 4;
        _rightSplitter.SplitterWidth = 4; // Add right splitter styling
    }

    // Player Stats / Display
    public void UpdatePlayerStats(PlayerStatsStore.StatSnapshot snap)
    {
        if (InvokeRequired) 
        { 
            BeginInvoke(new Action<PlayerStatsStore.StatSnapshot>(UpdatePlayerStats), snap); 
            return; 
        }
        
        _lastSnap = snap;
        BuildStatsDisplay();
    }

    private void BuildStatsDisplay()
    {
        var sb = new StringBuilder();
        
        if (_lastSnap != null)
        {
            var s = _lastSnap;
            sb.AppendLine($"📊 Player: {s.Character} ({s.Username})");
            sb.AppendLine($"❤️  HP: {s.HP}/{s.MaxHP} ({(double)s.HP / s.MaxHP:P1})");
            sb.AppendLine($"🔮 MP: {s.Mana}/{s.MaxMana}  🏃 MV: {s.Move}/{s.MaxMove}");
            sb.AppendLine($"⭐ Level {s.Level} {s.LevelTitle}");
            sb.AppendLine($"🛡️ AC:{s.AC} Absorb:{s.Absorb}%  ✨ XP:{s.Experience}");
            sb.AppendLine();
            
            sb.AppendLine("📋 Attributes:");
            foreach (var kv in s.Attributes.OrderBy(k => k.Key))
            {
                sb.AppendLine($"   {kv.Key,-12} {kv.Value.current,4} (base {kv.Value.baseVal})");
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("📊 Player: (no snapshot yet)");
            sb.AppendLine();
        }
        
        var eff = _profile.Effects;
        sb.AppendLine("🩹 Status Effects:");
        sb.AppendLine($"   Shielded : {(eff.Shielded ? "Yes" : "No")}");
        sb.AppendLine($"   Poisoned : {(eff.Poisoned ? "Yes" : "No")}");
        
        if (!string.IsNullOrEmpty(eff.HungerState)) 
        {
            sb.AppendLine($"   Hunger   : {eff.HungerState}");
        }
        
        if (!string.IsNullOrEmpty(eff.ThirstState)) 
        {
            sb.AppendLine($"   Thirst   : {eff.ThirstState}");
        }
        
        sb.AppendLine($"   Boosts   : {(eff.Boosts.Count == 0 ? "none" : string.Join(", ", eff.Boosts))}");
        sb.AppendLine($"   Drains   : {(eff.Drains.Count == 0 ? "none" : string.Join(", ", eff.Drains))}");
        sb.AppendLine();
        
        if (_profile.Spells.Count > 0)
        {
            sb.AppendLine($"📘 Spells ({_profile.Spells.Count}):");
            sb.AppendLine("   Mana Diff Nick  S  Name");
            sb.AppendLine("   ---- ---- ----- -- ------------------------------");
            
            foreach (var sp in _profile.Spells.OrderBy(x => x.Nick))
            {
                sb.AppendLine($"   {sp.Mana,4} {sp.Diff,4} {sp.Nick,-5} {sp.SphereCode,1}  {sp.LongName}");
            }
            sb.AppendLine();
        }
        else 
        {
            sb.AppendLine("📘 Spells: (none parsed yet)");
            sb.AppendLine();
        }
        
        // Add inventory section
        if (_profile.Player.Inventory.Count > 0)
        {
            sb.AppendLine($"🎒 Inventory ({_profile.Player.Inventory.Count} items):");
            
            // Show encumbrance first
            if (!string.IsNullOrEmpty(_profile.Player.Encumbrance))
            {
                sb.AppendLine($"   💼 {_profile.Player.Encumbrance}");
            }
            
            // Extract and show gold/silver at the top
            var goldItems = new List<string>();
            var armorItems = new List<string>();
            var standardItems = new List<string>();
            
            foreach (var item in _profile.Player.Inventory)
            {
                var itemLower = item.ToLowerInvariant().Trim();
                
                // Check for gold/silver (with or without amounts)
                if (itemLower.Contains("gold") || itemLower.Contains("silver"))
                {
                    goldItems.Add(item);
                }
                // Check for armor/equipment keywords
                else if (IsArmorOrEquipment(itemLower))
                {
                    armorItems.Add(item);
                }
                else
                {
                    standardItems.Add(item);
                }
            }
            
            // Display gold/silver first
            if (goldItems.Count > 0)
            {
                sb.AppendLine("   💰 Currency:");
                foreach (var gold in goldItems.OrderBy(g => g))
                {
                    sb.AppendLine($"      {gold}");
                }
                sb.AppendLine();
            }
            
            // Display armor/equipment
            if (armorItems.Count > 0)
            {
                sb.AppendLine("   🛡️ Armor & Equipment:");
                foreach (var armor in armorItems.OrderBy(a => a))
                {
                    sb.AppendLine($"      {armor}");
                }
                sb.AppendLine();
            }
            
            // Display standard items alphabetically
            if (standardItems.Count > 0)
            {
                sb.AppendLine("   📦 Items:");
                foreach (var item in standardItems.OrderBy(i => i))
                {
                    sb.AppendLine($"      {item}");
                }
                sb.AppendLine();
            }
            
            // Show what's armed/wielded
            if (!string.IsNullOrEmpty(_profile.Player.ArmedWith))
            {
                sb.AppendLine($"   ⚔️ Wielding: {_profile.Player.ArmedWith}");
            }
            sb.AppendLine();
        }
        else 
        {
            sb.AppendLine("🎒 Inventory: (not captured yet)");
            sb.AppendLine();
        }
        
        _txtPlayerStats.Text = sb.ToString();
        _statsHeader.Text = _lastSnap != null ? $"Character Stats - {_lastSnap.Character}" : "Character Stats";
    }

    public void RefreshProfileExtras() 
    { 
        if (InvokeRequired) 
        { 
            BeginInvoke(new Action(RefreshProfileExtras)); 
            return; 
        } 
        
        BuildStatsDisplay(); 
    }

    /// <summary>
    /// Clear the player stats display when returning to main menu
    /// </summary>
    public void ClearPlayerStats()
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(ClearPlayerStats));
            return;
        }

        _lastSnap = null;
        _txtPlayerStats.Text = "📊 Player Stats\n\nWaiting for data...";
        _statsHeader.Text = "Character Stats";
        
        // Update player status to clear inventory info
        _playerStatusLabel.Text = "No player data";
    }
    
    public void UpdateRoomWithGrid(RoomState room, RoomGridData gridData)
    {
        if (InvokeRequired) 
        { 
            BeginInvoke(new Action<RoomState, RoomGridData>(UpdateRoomWithGrid), room, gridData); 
            return; 
        }
        
        var sb = new StringBuilder();
        sb.AppendLine($"🏠 {room.Name}");
        sb.AppendLine();
        sb.AppendLine($"🚪 Exits: {(room.Exits.Count == 0 ? "none" : string.Join(", ", room.Exits))}");
        sb.AppendLine();
        
        if (room.Monsters.Count > 0)
        {
            sb.AppendLine($"👥 Creatures Here ({room.Monsters.Count}):");
            foreach (var m in room.Monsters)
            {
                var icon = m.Disposition == "aggressive" ? "⚔️" : 
                          m.Disposition == "fleeing" ? "🏃" : "👤";
                sb.AppendLine($"   {icon} {m.Name}");
            }
            sb.AppendLine();
        }
        
        if (room.Items.Count > 0)
        {
            sb.AppendLine($"📦 Items Here ({room.Items.Count}):");
            foreach (var it in room.Items) 
            {
                sb.AppendLine($"   💎 {it}");
            }
            sb.AppendLine();
        }
        
        // Add the room grid display
        sb.AppendLine("🗺️  Available Directions:");
        sb.AppendLine();
        
        // Format the grid
        var g = gridData;
        sb.AppendLine($"        ┌───┐        ");
        sb.AppendLine($"        │{FormatGridCell(g.Up, "U")}│        ");
        sb.AppendLine($"        └───┘        ");
        sb.AppendLine();
        sb.AppendLine($"  ┌───┬───┬───┐  ");
        sb.AppendLine($"  │{FormatGridCell(g.Northwest, "NW")}│{FormatGridCell(g.North, "N")}│{FormatGridCell(g.Northeast, "NE")}│  ");
        sb.AppendLine($"  ├───┼───┼───┤  ");
        sb.AppendLine($"  │{FormatGridCell(g.West, "W")}│ ⭐ │{FormatGridCell(g.East, "E")}│  ");
        sb.AppendLine($"  ├───┼───┼───┤  ");
        sb.AppendLine($"  │{FormatGridCell(g.Southwest, "SW")}│{FormatGridCell(g.South, "S")}│{FormatGridCell(g.Southeast, "SE")}│  ");
        sb.AppendLine($"  └───┴───┴───┘  ");
        sb.AppendLine();
        sb.AppendLine($"        ┌───┐        ");
        sb.AppendLine($"        │{FormatGridCell(g.Down, "D")}│        ");
        sb.AppendLine($"        └───┘        ");
        sb.AppendLine();
        
        sb.AppendLine("📝 Direction Legend:");
        sb.AppendLine("   🟢 = Exit available, room explored");
        sb.AppendLine("   ⚔️  = Exit leads to room with hostile creatures");
        sb.AppendLine("   👥 = Exit leads to room with NPCs/creatures");
        sb.AppendLine("   📦 = Exit leads to room with items");
        sb.AppendLine("   🔍 = Exit available, room not yet visited");
        sb.AppendLine("   ❌ = No exit in this direction");
        
        _txtRoom.Text = sb.ToString();
        _roomHeader.Text = $"Current Room - {room.Name}";
    }
    
    private string FormatGridCell(GridRoomInfo? room, string directionLabel)
    {
        if (room == null)
        {
            return "❌";
        }
        
        if (!room.IsKnown)
        {
            return "🔍";
        }
        
        if (room.HasAggressiveMonsters)
        {
            return "⚔️";
        }
        else if (room.HasMonsters)
        {
            return "👥";
        }
        else if (room.HasItems)
        {
            return "📦";
        }
        else
        {
            return "🟢";
        }
    }

    // Credential Management
    private void LoadCredentialUsers()
    {
        try
        {
            var users = _creds.ListUsernames().OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
            _cmbUsers.Items.Clear();
            
            if (users.Count > 0)
            {
                foreach (var u in users) 
                {
                    _cmbUsers.Items.Add(u);
                }
                
                _cmbUsers.Items.Add("Add New User...");
                _cmbUsers.SelectedIndex = 0;
                _selectedUser = users[0];
            }
            else 
            { 
                _cmbUsers.Items.Add("Add New User..."); 
                _cmbUsers.SelectedIndex = 0; 
            }
        }
        catch (Exception ex) 
        { 
            Log($"Error loading credential users: {ex.Message}"); 
        }
    }

    private void OnUserSelectionChanged(object? sender, EventArgs e)
    {
        if (_cmbUsers.SelectedItem is string s)
        {
            if (s == "Add New User...") 
            { 
                ShowAddUserDialog();
            }
            else 
            { 
                _selectedUser = s; 
                UserSelected?.Invoke(s); 
                Log($"User selected: {s}"); 
            }
        }
    }
    
    private void ShowAddUserDialog()
    {
        var addUserForm = new AddUserForm();
        if (addUserForm.ShowDialog(this) == DialogResult.OK)
        {
            var username = addUserForm.Username;
            var password = addUserForm.Password;
            
            if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
            {
                try
                {
                    _creds.AddOrUpdate(username, password);
                    Log($"Added/updated user: {username}");
                    
                    // Reload the user list and select the new user
                    LoadCredentialUsers();
                    SetSelectedUser(username);
                }
                catch (Exception ex)
                {
                    Log($"Error adding user: {ex.Message}");
                    MessageBox.Show($"Failed to add user: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }

    public void SetSelectedUser(string? user)
    { 
        if (InvokeRequired) 
        { 
            BeginInvoke(new Action<string?>(SetSelectedUser), user); 
            return; 
        } 
        
        if (string.IsNullOrEmpty(user)) 
            return;
            
        for (int i = 0; i < _cmbUsers.Items.Count; i++)
        {
            if (_cmbUsers.Items[i]?.ToString() == user) 
            { 
                _cmbUsers.SelectedIndex = i; 
                break; 
            }
        }
    }

    // Logging
    private void OnLog(UiLogEntry e) 
    { 
        Log($"[{e.Timestamp:HH:mm:ss}] {e.Level}: {e.Message}"); 
    }
    
    public void Log(string line)
    { 
        if (InvokeRequired)
        { 
            BeginInvoke(new Action<string>(Log), line); 
            return;
        } 
        
        if (_lstLog.Items.Count > 1000) 
        {
            _lstLog.Items.RemoveAt(0);
        }
        
        _lstLog.Items.Add(line); 
        _lstLog.TopIndex = _lstLog.Items.Count - 1; 
        _statusLabel.Text = $"Log entries: {_lstLog.Items.Count}"; 
    }
    
    private void ClearLog()
    { 
        var c = _lstLog.Items.Count; 
        _lstLog.Items.Clear(); 
        Log($"Cleared {c} log entries"); 
    }
    
    private void CopySelectedLogEntries()
    { 
        try 
        { 
            var sel = _lstLog.SelectedItems.Cast<string>(); 
            var txt = string.Join(Environment.NewLine, sel); 
            
            if (!string.IsNullOrEmpty(txt)) 
            {
                Clipboard.SetText(txt); 
            }
        } 
        catch (Exception ex)
        { 
            Log($"Copy failed: {ex.Message}"); 
        } 
    }
    
    private void SelectAllLogEntries()
    { 
        for (int i = 0; i < _lstLog.Items.Count; i++) 
        {
            _lstLog.SetSelected(i, true); 
        }
    }

    // Menu / Actions - Implement the settings dialog
    private void ShowSettingsDialog(object? sender, EventArgs e) 
    {
        try
        {
            var settingsForm = new SettingsForm(_currentSettings);
            
            // Hook up the apply event to update settings in real-time
            settingsForm.SettingsApplied += (settings) =>
            {
                _currentSettings = settings;
                ApplySettingsChanges(settings);
                Log($"[Settings] Settings applied: TelnetDiag={settings.TelnetDiagnostics}, RawEcho={settings.RawEcho}, Enhanced={settings.EnhancedStatsLineCleaning}");
            };
            
            if (settingsForm.ShowDialog(this) == DialogResult.OK)
            {
                _currentSettings = settingsForm.Settings;
                ApplySettingsChanges(_currentSettings);
                
                // Raise the settings changed event for the main application
                DebugSettingsChanged?.Invoke(_currentSettings);
                
                Log($"[Settings] Settings saved: CursorStyle={_currentSettings.CursorStyle}, DumbMode={_currentSettings.DumbMode}, AsciiCompat={_currentSettings.AsciiCompatible}");
            }
        }
        catch (Exception ex)
        {
            Log($"[Settings] Error opening settings dialog: {ex.Message}");
            MessageBox.Show($"Failed to open settings dialog: {ex.Message}", "Settings Error", 
                           MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
    
    private void ApplySettingsChanges(DebugSettings settings)
    {
        try
        {
            // Update internal settings
            _currentSettings = settings;
            
            // Update menu item states to reflect current settings
            UpdateMenuItemStates();
            
            Log($"[Settings] Applied changes - Telnet diagnostics: {settings.TelnetDiagnostics}, Raw echo: {settings.RawEcho}");
        }
        catch (Exception ex)
        {
            Log($"[Settings] Error applying settings: {ex.Message}");
        }
    }
    
    private void UpdateMenuItemStates()
    {
        try
        {
            // Find and update debug menu items
            foreach (ToolStripItem item in _menuStrip.Items)
            {
                if (item is ToolStripMenuItem debugMenu && debugMenu.Text == "&Debug")
                {
                    foreach (ToolStripItem debugItem in debugMenu.DropDownItems)
                    {
                        if (debugItem is ToolStripMenuItem menuItem)
                        {
                            switch (menuItem.Text)
                            {
                                case "&Telnet Diagnostics":
                                    menuItem.Checked = _currentSettings.TelnetDiagnostics;
                                    break;
                                case "&Raw Echo":
                                    menuItem.Checked = _currentSettings.RawEcho;
                                    break;
                                case "&Enhanced Stats Cleaning":
                                    menuItem.Checked = _currentSettings.EnhancedStatsLineCleaning;
                                    break;
                            }
                        }
                    }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log($"[Settings] Error updating menu states: {ex.Message}");
        }
    }
    
    private void ToggleTelnetDiagnostics(object? s, EventArgs e) 
    {
        if (s is ToolStripMenuItem menuItem)
        {
            _currentSettings.TelnetDiagnostics = menuItem.Checked;
            DebugSettingsChanged?.Invoke(_currentSettings);
            Log($"[Settings] Telnet diagnostics: {(menuItem.Checked ? "enabled" : "disabled")}");
        }
    }
    
    private void ToggleRawEcho(object? s, EventArgs e) 
    {
        if (s is ToolStripMenuItem menuItem)
        {
            _currentSettings.RawEcho = menuItem.Checked;
            DebugSettingsChanged?.Invoke(_currentSettings);
            Log($"[Settings] Raw echo: {(menuItem.Checked ? "enabled" : "disabled")}");
        }
    }
    
    private void ToggleStatscleanimg(object? s, EventArgs e) 
    {
        if (s is ToolStripMenuItem menuItem)
        {
            _currentSettings.EnhancedStatsLineCleaning = menuItem.Checked;
            DebugSettingsChanged?.Invoke(_currentSettings);
            Log($"[Settings] Enhanced stats cleaning: {(menuItem.Checked ? "enabled" : "disabled")}");
        }
    }
    
    /// <summary>
    /// Set the combat tracker reference for statistics display
    /// </summary>
    public void SetCombatTracker(CombatTracker combatTracker)
    {
        _combatTracker = combatTracker;
        RefreshCombatStats();
    }

    private void ToggleAlwaysOnTop(object? s, EventArgs e)
    { 
        if (s is ToolStripMenuItem m) 
        {
            TopMost = m.Checked; 
        }
    }
    
    private void ToggleControlButtons(object? s, EventArgs e)
    { 
        if (s is ToolStripMenuItem m) 
        {
            _buttonPanel.Visible = m.Checked; 
        }
    }
    
    private void ResetLayout(object? s, EventArgs e)
    { 
        _userAdjustedMainSplitter = false; 
        _userAdjustedTopSplitter = false; 
        SetInitialSplitterPositions(null, EventArgs.Empty);
    }    
    
    private void ForceLayoutRefresh(object? s, EventArgs e)
    { 
        SetInitialSplitterPositions(null, EventArgs.Empty);
    }    
    
    private void ForceLayoutFix(object? s, EventArgs e)
    { 
        // Auto force layout fix
    }
    
    private void ShowLayoutInfo(object? s, EventArgs e)
    { 
        MessageBox.Show($"Main:{_mainSplitter.Size} Top:{_topSplitter.Size}", "Layout Info"); 
    }
    
    private void FixDockingIssues(object? s, EventArgs e)
    { 
        // Manually position the main splitter to fill remaining space
        var buttonHeight = _buttonPanel.Visible ? _buttonPanel.Height : 0;
        var availableHeight = ClientSize.Height - _menuStrip.Height - _statusStrip.Height - buttonHeight;
        _mainSplitter.Location = new Point(0, _menuStrip.Height);
        _mainSplitter.Size = new Size(ClientSize.Width, availableHeight);
        _topSplitter.Dock = DockStyle.Fill; 
    }
    
    private void TriggerRoomDebug()
    { 
        var dbg = RoomParser.DebugParse(_screen.ToText()); 
        Log("[Room Debug]" + dbg.Split('\n').FirstOrDefault()); 
    }
    
    private void TriggerScreenDump()
    { 
        var snap = _screen.ToText(); 
        Log("[Screen Dump]" + string.Join(' ', snap.Split('\n').Take(5))); 
    }

    private void RefreshSummary()
    { 
        var summary = _stats.ToStatusString(); 
        _statusLabel.Text = $"{summary} | Log: {_lstLog.Items.Count}";
        
        // Update player status with inventory info
        var playerInfo = "";
        if (_profile.Player.Inventory.Count > 0)
        {
            playerInfo = $"Items: {_profile.Player.Inventory.Count}";
            if (!string.IsNullOrEmpty(_profile.Player.ArmedWith))
            {
                playerInfo += $" | Armed: {_profile.Player.ArmedWith}";
            }
        }
        else if (!string.IsNullOrEmpty(_profile.Player.ArmedWith))
        {
            playerInfo = $"Armed: {_profile.Player.ArmedWith}";
        }
        else
        {
            playerInfo = "No player data";
        }
        
        _playerStatusLabel.Text = playerInfo;
    }
    
    private void RequestDisconnect()
    {
        Log("Disconnect requested");
        DisconnectRequested?.Invoke();
    }
    
    private void RequestReconnect()
    {
        Log("Reconnect requested");
        ReconnectRequested?.Invoke();
    }
    
    private void ShowUserManagementDialog()
    {
        var userMgmtForm = new UserManagementForm(_creds);
        if (userMgmtForm.ShowDialog(this) == DialogResult.OK)
        {
            Log("User management completed, reloading user list");
            LoadCredentialUsers();
        }
    }
    
    private bool IsArmorOrEquipment(string itemName)
    {
        // Define keywords that indicate armor or equipment
        var armorKeywords = new[]
        {
            "armor", "armour", "mail", "plate", "vest", "robe", "cloak", "cape",
            "helmet", "helm", "crown", "hat", "cap",
            "gauntlets", "gloves", "bracers", "fists",
            "boots", "shoes", "sandals",
            "shield", "buckler",
            "ring", "amulet", "necklace", "pendant",
            "sword", "blade", "dagger", "staff", "wand", "bow", "axe", "mace", "hammer",
            "kimono", "vest", "breeches", "leggings", "pants",
            "magicked", "dragonscale", "dragonbone", "magishield"
        };
        
        return armorKeywords.Any(keyword => itemName.Contains(keyword));
    }
}