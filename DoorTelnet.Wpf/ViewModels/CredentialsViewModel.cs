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

    // Backing fields for observable properties
    private string _selectedUser = string.Empty; public string SelectedUser { get => _selectedUser; set { if (SetProperty(ref _selectedUser, value)) OnSelectedUserChanged(); } }
    private string _username = string.Empty; public string Username { get => _username; set { if (SetProperty(ref _username, value)) SaveCommand.NotifyCanExecuteChanged(); } }
    private string _password = string.Empty; public string Password { get => _password; set { if (SetProperty(ref _password, value)) SaveCommand.NotifyCanExecuteChanged(); } }
    private bool _isNew = true; public bool IsNew { get => _isNew; set { if (SetProperty(ref _isNew, value)) SaveCommand.NotifyCanExecuteChanged(); } }

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
    }

    private void OnSelectedUserChanged()
    {
        if (!string.IsNullOrEmpty(SelectedUser))
        {
            IsNew = false;
            Username = SelectedUser;
            Password = _store.GetPassword(SelectedUser) ?? string.Empty;
        }
        DeleteCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void Load()
    {
        Users.Clear();
        foreach (var u in _store.ListUsernames()) Users.Add(u);
        if (Users.Count > 0) SelectedUser = Users[0];
    }

    private bool CanSave() => !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
    private bool CanDelete() => !string.IsNullOrEmpty(SelectedUser);

    private void Save()
    {
        if (!CanSave()) return;
        _store.AddOrUpdate(Username, Password);
        Load();
        SelectedUser = Username;
        IsNew = false;
    }

    private void Delete()
    {
        if (string.IsNullOrEmpty(SelectedUser)) return;
        _store.Remove(SelectedUser);
        Load();
    }
}
