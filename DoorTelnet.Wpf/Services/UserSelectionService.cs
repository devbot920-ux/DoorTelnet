using System;
namespace DoorTelnet.Wpf.Services;
public class UserSelectionService
{
    private readonly object _sync = new();
    private string? _selectedUser;
    public string? SelectedUser { get { lock (_sync) return _selectedUser; } set { lock (_sync) _selectedUser = value; } }
}
