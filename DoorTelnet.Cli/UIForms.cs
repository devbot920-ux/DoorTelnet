using System.Windows.Forms;
using DoorTelnet.Core.Player;

namespace DoorTelnet.Cli;

// Settings dialog form
public class SettingsForm : Form
{
    public DebugSettings Settings { get; private set; }

    private readonly TabControl _tabControl = new() { Dock = DockStyle.Fill };
    private readonly Button _okButton = new() { Text = "OK", DialogResult = DialogResult.OK, Size = new Size(75, 25) };
    private readonly Button _cancelButton = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(75, 25) };
    private readonly Button _applyButton = new() { Text = "Apply", Size = new Size(75, 25) };

    // Debug settings controls
    private readonly CheckBox _chkTelnetDiag = new() { Text = "Enable Telnet Diagnostics", AutoSize = true };
    private readonly CheckBox _chkRawEcho = new() { Text = "Enable Raw Echo Logging", AutoSize = true };
    private readonly CheckBox _chkStatsClean = new() { Text = "Enhanced Stats Line Cleaning", AutoSize = true };
    private readonly CheckBox _chkDumbMode = new() { Text = "Dumb Mode (BasicWrite only)", AutoSize = true };

    // Terminal settings controls  
    private readonly CheckBox _chkAsciiCompat = new() { Text = "ASCII Compatible Mode", AutoSize = true };
    private readonly ComboBox _cboCursorStyle = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly Label _lblCursorStyle = new() { Text = "Cursor Style:", AutoSize = true };

    // Event for Apply button
    public event Action<DebugSettings>? SettingsApplied;

    public SettingsForm(DebugSettings currentSettings)
    {
        Settings = new DebugSettings
        {
            TelnetDiagnostics = currentSettings.TelnetDiagnostics,
            RawEcho = currentSettings.RawEcho,
            EnhancedStatsLineCleaning = currentSettings.EnhancedStatsLineCleaning,
            DumbMode = currentSettings.DumbMode,
            CursorStyle = currentSettings.CursorStyle,
            AsciiCompatible = currentSettings.AsciiCompatible
        };

        InitializeForm();
        SetupTabs();
        LoadSettings();
        SetupEventHandlers();
    }

    private void InitializeForm()
    {
        Text = "DoorTelnet Settings";
        Size = new Size(450, 350);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
    }

    private void SetupTabs()
    {
        // Debug tab
        var debugTab = new TabPage("Debug & Diagnostics");
        var debugPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        debugPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        debugPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        debugPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        debugPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        debugPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var debugDesc = new Label
        {
            Text = "Configure debugging and diagnostic options. Changes apply immediately.",
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(0, 0, 0, 10)
        };

        debugPanel.Controls.Add(debugDesc, 0, 0);
        debugPanel.Controls.Add(_chkTelnetDiag, 0, 1);
        debugPanel.Controls.Add(_chkRawEcho, 0, 2);
        debugPanel.Controls.Add(_chkStatsClean, 0, 3);
        debugPanel.Controls.Add(_chkDumbMode, 0, 4);

        debugTab.Controls.Add(debugPanel);

        // Terminal tab
        var terminalTab = new TabPage("Terminal & Display");
        var terminalPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(10) };
        terminalPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        terminalPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        terminalPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        terminalPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        terminalPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        terminalPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var terminalDesc = new Label
        {
            Text = "Configure terminal display and character rendering options.",
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(0, 0, 0, 10)
        };

        _cboCursorStyle.Items.AddRange(new[] { "underscore", "block", "pipe", "hash", "dot", "plus" });

        terminalPanel.Controls.Add(terminalDesc, 0, 0);
        terminalPanel.SetColumnSpan(terminalDesc, 2);
        terminalPanel.Controls.Add(_chkAsciiCompat, 0, 1);
        terminalPanel.SetColumnSpan(_chkAsciiCompat, 2);
        terminalPanel.Controls.Add(_lblCursorStyle, 0, 2);
        terminalPanel.Controls.Add(_cboCursorStyle, 1, 2);

        terminalTab.Controls.Add(terminalPanel);

        _tabControl.TabPages.Add(debugTab);
        _tabControl.TabPages.Add(terminalTab);

        // Button panel
        var buttonPanel = new Panel { Height = 40, Dock = DockStyle.Bottom };
        var buttonFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Padding = new Padding(5)
        };
        buttonFlow.Controls.AddRange(new Control[] { _okButton, _cancelButton, _applyButton });
        buttonPanel.Controls.Add(buttonFlow);

        Controls.Add(_tabControl);
        Controls.Add(buttonPanel);
    }

    private void LoadSettings()
    {
        _chkTelnetDiag.Checked = Settings.TelnetDiagnostics;
        _chkRawEcho.Checked = Settings.RawEcho;
        _chkStatsClean.Checked = Settings.EnhancedStatsLineCleaning;
        _chkDumbMode.Checked = Settings.DumbMode;
        _chkAsciiCompat.Checked = Settings.AsciiCompatible;
        _cboCursorStyle.SelectedItem = Settings.CursorStyle;
    }

    private void SaveSettings()
    {
        Settings.TelnetDiagnostics = _chkTelnetDiag.Checked;
        Settings.RawEcho = _chkRawEcho.Checked;
        Settings.EnhancedStatsLineCleaning = _chkStatsClean.Checked;
        Settings.DumbMode = _chkDumbMode.Checked;
        Settings.AsciiCompatible = _chkAsciiCompat.Checked;
        Settings.CursorStyle = _cboCursorStyle.SelectedItem?.ToString() ?? "underscore";
    }

    private void SetupEventHandlers()
    {
        _okButton.Click += (s, e) => { SaveSettings(); DialogResult = DialogResult.OK; Close(); };
        _applyButton.Click += (s, e) =>
        {
            SaveSettings();
            SettingsApplied?.Invoke(Settings);
        };

        // Add tooltips
        var toolTip = new ToolTip();
        toolTip.SetToolTip(_chkTelnetDiag, "Enable detailed telnet protocol logging");
        toolTip.SetToolTip(_chkRawEcho, "Log raw data received from server");
        toolTip.SetToolTip(_chkStatsClean, "Advanced cleaning to prevent AC/AT timer artifacts");
        toolTip.SetToolTip(_chkDumbMode, "Use basic text rendering instead of ANSI parser");
        toolTip.SetToolTip(_chkAsciiCompat, "Use ASCII characters instead of Unicode for better compatibility");
        toolTip.SetToolTip(_cboCursorStyle, "Choose cursor appearance style");
    }
}

public class CredentialsForm : Form
{
    private readonly ComboBox _cmbUsers = new() { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _txtUser = new() { Dock = DockStyle.Top, PlaceholderText = "Username" };
    private readonly TextBox _txtPass = new() { Dock = DockStyle.Top, UseSystemPasswordChar = true, PlaceholderText = "Password" };
    private readonly CheckBox _chkNew = new() { Dock = DockStyle.Top, Text = "New / Update" };
    private readonly Button _ok = new() { Dock = DockStyle.Bottom, Text = "OK" };
    private readonly Button _cancel = new() { Dock = DockStyle.Bottom, Text = "Cancel" };
    public bool NewEntry => _chkNew.Checked;
    public string? Username => _chkNew.Checked ? _txtUser.Text : (_cmbUsers.SelectedItem as string);
    public string? Password => _chkNew.Checked ? _txtPass.Text : null;

    public CredentialsForm(IEnumerable<string> existing)
    {
        Text = "Credentials"; Width = 320; Height = 240;
        _cmbUsers.Items.AddRange(existing.Cast<object>().ToArray());
        if (_cmbUsers.Items.Count > 0) _cmbUsers.SelectedIndex = 0;
        Controls.AddRange(new Control[] { _ok, _cancel, _chkNew, _txtPass, _txtUser, _cmbUsers });
        _txtUser.Enabled = _txtPass.Enabled = false;
        _chkNew.CheckedChanged += (_, _) =>
        {
            var en = _chkNew.Checked;
            _txtUser.Enabled = en; _txtPass.Enabled = en; _cmbUsers.Enabled = !en;
        };
        _ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        _cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
    }
}

public class AddUserForm : Form
{
    private readonly TextBox _txtUser = new() { Dock = DockStyle.Top, PlaceholderText = "Username", Margin = new Padding(5) };
    private readonly TextBox _txtPass = new() { Dock = DockStyle.Top, UseSystemPasswordChar = true, PlaceholderText = "Password", Margin = new Padding(5) };
    private readonly Panel _buttonPanel = new() { Height = 40, Dock = DockStyle.Bottom };
    private readonly Button _ok = new() { Text = "Add User", DialogResult = DialogResult.OK, Size = new Size(80, 25) };
    private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(80, 25) };

    public string Username => _txtUser.Text.Trim();
    public string Password => _txtPass.Text;

    public AddUserForm()
    {
        Text = "Add New User";
        Size = new Size(300, 150);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var buttonFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Padding = new Padding(5)
        };

        buttonFlow.Controls.Add(_ok);
        buttonFlow.Controls.Add(_cancel);
        _buttonPanel.Controls.Add(buttonFlow);

        Controls.Add(_buttonPanel);
        Controls.Add(_txtPass);
        Controls.Add(_txtUser);

        _ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                MessageBox.Show("Please enter both username and password.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
    }
}

public class UserManagementForm : Form
{
    private readonly CredentialStore _creds;
    private readonly DataGridView _gridUsers = new() { Dock = DockStyle.Fill, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private readonly Panel _buttonPanel = new() { Height = 50, Dock = DockStyle.Bottom };
    private readonly Button _btnAdd = new() { Text = "&Add User", Size = new Size(90, 30) };
    private readonly Button _btnEdit = new() { Text = "&Edit User", Size = new Size(90, 30) };
    private readonly Button _btnDelete = new() { Text = "&Delete User", Size = new Size(90, 30) };
    private readonly Button _btnClose = new() { Text = "&Close", Size = new Size(90, 30), DialogResult = DialogResult.OK };
    
    public UserManagementForm(CredentialStore credentialStore)
    {
        _creds = credentialStore;
        InitializeForm();
        SetupGrid();
        SetupButtons();
        LoadUsers();
        SetupEventHandlers();
    }
    
    private void InitializeForm()
    {
        Text = "Manage Users";
        Size = new Size(500, 400);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(400, 300);
        ShowInTaskbar = false;
    }
    
    private void SetupGrid()
    {
        _gridUsers.AllowUserToAddRows = false;
        _gridUsers.AllowUserToDeleteRows = false;
        _gridUsers.ReadOnly = true;
        _gridUsers.MultiSelect = false;
        _gridUsers.AutoGenerateColumns = false;
        _gridUsers.BackgroundColor = Color.White;
        _gridUsers.BorderStyle = BorderStyle.Fixed3D;
        _gridUsers.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        
        // Add columns
        var colUsername = new DataGridViewTextBoxColumn
        {
            Name = "Username",
            HeaderText = "Username",
            DataPropertyName = "Username",
            Width = 200,
            ReadOnly = true
        };
        
        var colLastUsed = new DataGridViewTextBoxColumn
        {
            Name = "LastUsed",
            HeaderText = "Last Used",
            DataPropertyName = "LastUsed",
            Width = 150,
            ReadOnly = true
        };
        
        var colHasPassword = new DataGridViewCheckBoxColumn
        {
            Name = "HasPassword",
            HeaderText = "Has Password",
            DataPropertyName = "HasPassword",
            Width = 100,
            ReadOnly = true
        };
        
        _gridUsers.Columns.AddRange(new DataGridViewColumn[] { colUsername, colLastUsed, colHasPassword });
        
        // Enable double-click to edit
        _gridUsers.DoubleClick += (s, e) => EditSelectedUser();
    }
    
    private void SetupButtons()
    {
        var buttonFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Padding = new Padding(10)
        };
        
        buttonFlow.Controls.Add(_btnAdd);
        buttonFlow.Controls.Add(_btnEdit);
        buttonFlow.Controls.Add(_btnDelete);
        buttonFlow.Controls.Add(_btnClose);
        
        _buttonPanel.Controls.Add(buttonFlow);
        Controls.Add(_buttonPanel);
        Controls.Add(_gridUsers);
    }
    
    private void LoadUsers()
    {
        var users = _creds.ListUsernames().OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList();
        var userData = users.Select(u => new
        {
            Username = u,
            LastUsed = "N/A", // Could be enhanced to track last usage
            HasPassword = !string.IsNullOrEmpty(_creds.GetPassword(u))
        }).ToList();
        
        _gridUsers.DataSource = userData;
        
        // Update button states
        _btnEdit.Enabled = _gridUsers.SelectedRows.Count > 0;
        _btnDelete.Enabled = _gridUsers.SelectedRows.Count > 0;
    }
    
    private void SetupEventHandlers()
    {
        _btnAdd.Click += (s, e) => AddUser();
        _btnEdit.Click += (s, e) => EditSelectedUser();
        _btnDelete.Click += (s, e) => DeleteSelectedUser();
        _gridUsers.SelectionChanged += (s, e) =>
        {
            _btnEdit.Enabled = _gridUsers.SelectedRows.Count > 0;
            _btnDelete.Enabled = _gridUsers.SelectedRows.Count > 0;
        };
        
        // Handle key events for accessibility
        _gridUsers.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Delete && _btnDelete.Enabled)
            {
                DeleteSelectedUser();
            }
            else if (e.KeyCode == Keys.Enter && _btnEdit.Enabled)
            {
                EditSelectedUser();
            }
        };
    }
    
    private void AddUser()
    {
        var editForm = new EditUserForm();
        if (editForm.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                _creds.AddOrUpdate(editForm.Username, editForm.Password);
                LoadUsers();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to add user: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
    
    private void EditSelectedUser()
    {
        if (_gridUsers.SelectedRows.Count == 0) return;
        
        var username = _gridUsers.SelectedRows[0].Cells["Username"].Value?.ToString();
        if (string.IsNullOrEmpty(username)) return;
        
        var currentPassword = _creds.GetPassword(username) ?? "";
        var editForm = new EditUserForm(username, currentPassword);
        
        if (editForm.ShowDialog(this) == DialogResult.OK)
        {
            try
            {
                if (editForm.Username != username)
                {
                    // Username changed - delete old and add new
                    _creds.Remove(username);
                }
                _creds.AddOrUpdate(editForm.Username, editForm.Password);
                LoadUsers();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to update user: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
    
    private void DeleteSelectedUser()
    {
        if (_gridUsers.SelectedRows.Count == 0) return;
        
        var username = _gridUsers.SelectedRows[0].Cells["Username"].Value?.ToString();
        if (string.IsNullOrEmpty(username)) return;
        
        var result = MessageBox.Show(
            $"Are you sure you want to delete user '{username}'?\n\nThis action cannot be undone.",
            "Confirm Delete",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
            
        if (result == DialogResult.Yes)
        {
            try
            {
                _creds.Remove(username);
                LoadUsers();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to delete user: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}

public class EditUserForm : Form
{
    private readonly Label _lblUsername = new() { Text = "&Username:", AutoSize = true };
    private readonly TextBox _txtUsername = new() { Width = 200 };
    private readonly Label _lblPassword = new() { Text = "&Password:", AutoSize = true };
    private readonly TextBox _txtPassword = new() { Width = 200, UseSystemPasswordChar = true };
    private readonly Button _btnOK = new() { Text = "OK", DialogResult = DialogResult.OK, Width = 75 };
    private readonly Button _btnCancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 75 };
    
    public string Username => _txtUsername.Text.Trim();
    public string Password => _txtPassword.Text;
    
    public EditUserForm() : this("", "")
    {
        Text = "Add New User";
    }
    
    public EditUserForm(string username, string password)
    {
        Text = string.IsNullOrEmpty(username) ? "Add New User" : "Edit User";
        _txtUsername.Text = username;
        _txtPassword.Text = password;
        
        InitializeForm();
        SetupLayout();
        SetupEventHandlers();
        SetTabOrder();
    }
    
    private void InitializeForm()
    {
        Size = new Size(320, 180);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        AcceptButton = _btnOK;
        CancelButton = _btnCancel;
    }
    
    private void SetupLayout()
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(15),
            ColumnCount = 2,
            RowCount = 3
        };
        
        // Configure column styles
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        
        // Configure row styles
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        
        // Add controls to table
        table.Controls.Add(_lblUsername, 0, 0);
        table.Controls.Add(_txtUsername, 1, 0);
        table.Controls.Add(_lblPassword, 0, 1);
        table.Controls.Add(_txtPassword, 1, 1);
        
        // Button panel
        var buttonPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(10)
        };
        
        buttonPanel.Controls.Add(_btnOK);
        buttonPanel.Controls.Add(_btnCancel);
        
        table.Controls.Add(buttonPanel, 1, 2);
        table.SetColumnSpan(buttonPanel, 1);
        
        Controls.Add(table);
    }
    
    private void SetTabOrder()
    {
        _txtUsername.TabIndex = 0;
        _txtPassword.TabIndex = 1;
        _btnOK.TabIndex = 2;
        _btnCancel.TabIndex = 3;
    }
    
    private void SetupEventHandlers()
    {
        _btnOK.Click += (s, e) => ValidateAndClose();
        
        // Allow Enter key to move between fields and submit
        _txtUsername.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                _txtPassword.Focus();
                e.SuppressKeyPress = true;
            }
        };
        
        _txtPassword.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                ValidateAndClose();
                e.SuppressKeyPress = true;
            }
        };
    }
    
    private void ValidateAndClose()
    {
        if (string.IsNullOrWhiteSpace(Username))
        {
            MessageBox.Show("Please enter a username.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtUsername.Focus();
            return;
        }
        
        if (string.IsNullOrWhiteSpace(Password))
        {
            MessageBox.Show("Please enter a password.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _txtPassword.Focus();
            return;
        }
        
        DialogResult = DialogResult.OK;
        Close();
    }
}