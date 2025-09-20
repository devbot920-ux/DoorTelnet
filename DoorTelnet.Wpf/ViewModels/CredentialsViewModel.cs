using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoorTelnet.Core.Player;
using Microsoft.Extensions.Logging;

namespace DoorTelnet.Wpf.ViewModels;

public partial class CredentialsViewModel : ViewModelBase
{
    private readonly CredentialStore _store;

    public ObservableCollection<string> Users { get; } = new();

    private string _selectedUser = string.Empty; public string SelectedUser { get => _selectedUser; set { if (SetProperty(ref _selectedUser, value)) OnSelectedUserChanged(); } }
    private string _username = string.Empty; public string Username { get => _username; set { if (SetProperty(ref _username, value)) { SaveCommand.NotifyCanExecuteChanged(); } } }
    private string _password = string.Empty; public string Password { get => _password; set { if (SetProperty(ref _password, value)) { SaveCommand.NotifyCanExecuteChanged(); } } }
    private bool _isNew = true; public bool IsNew { get => _isNew; set { if (SetProperty(ref _isNew, value)) { SaveCommand.NotifyCanExecuteChanged(); DeleteCommand.NotifyCanExecuteChanged(); if (value) { Password = string.Empty; } } } }

    public IRelayCommand SaveCommand { get; }
    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public event Action? RequestClose;

    public CredentialsViewModel(CredentialStore store, ILogger<CredentialsViewModel> logger) : base(logger)
    {
        _store = store;
        SaveCommand = new RelayCommand(Save, CanSave);
        DeleteCommand = new RelayCommand(Delete, CanDelete);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());
        Load();
        // Start in new mode if no users
        if (Users.Count == 0)
        {
            IsNew = true;
        }
    }

    private void OnSelectedUserChanged()
    {
        if (!string.IsNullOrEmpty(SelectedUser))
        {
            IsNew = false;
            Username = SelectedUser;
            Password = string.Empty; // do not expose existing password; require re-entry to change
        }
        DeleteCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void Load()
    {
        Users.Clear();
        foreach (var u in _store.ListUsernames()) Users.Add(u);
        if (Users.Count > 0)
        {
            SelectedUser = Users[0];
        }
    }

    // Allow saving:
    // New credential: username + password required
    // Existing credential update: username required; password optional (only update if provided)
    private bool CanSave()
    {
        if (IsNew)
            return !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
        return !string.IsNullOrWhiteSpace(Username); // editing existing
    }

    private bool CanDelete() => !IsNew && !string.IsNullOrEmpty(SelectedUser);

    private void Save()
    {
        if (!CanSave()) return;
        // If editing existing and password left blank, keep old stored password
        string passwordToStore = Password;
        if (!IsNew && string.IsNullOrWhiteSpace(passwordToStore))
        {
            var existing = _store.GetPassword(SelectedUser);
            passwordToStore = existing ?? string.Empty;
        }
        _store.AddOrUpdate(Username, passwordToStore);
        Load();
        SelectedUser = Username;
        IsNew = false;
    }

    private void Delete()
    {
        if (IsNew || string.IsNullOrEmpty(SelectedUser)) return;
        _store.Remove(SelectedUser);
        Load();
        IsNew = Users.Count == 0; // switch to new mode if nothing left
        if (IsNew)
        {
            Username = string.Empty;
            Password = string.Empty;
        }
    }
}
