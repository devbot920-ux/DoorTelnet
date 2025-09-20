using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DoorTelnet.Core.Player;
using Microsoft.Extensions.Logging;
using DoorTelnet.Wpf.Services;

namespace DoorTelnet.Wpf.ViewModels;

public class CharacterProfileDisplay : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string House { get; set; } = string.Empty;
    public int Level { get; set; }
    public long Experience { get; set; }
    public string Class { get; set; } = string.Empty;
}

public partial class CharacterProfilesViewModel : ViewModelBase
{
    private readonly CharacterProfileStore _store;
    private readonly ISettingsService _settings;

    public ObservableCollection<CharacterProfileDisplay> Characters { get; } = new();

    private string _username = string.Empty; public string CurrentUsername { get => _username; set { if (SetProperty(ref _username, value)) Load(); } }
    private string _selectedCharacter = string.Empty; public string SelectedCharacter { get => _selectedCharacter; set => SetProperty(ref _selectedCharacter, value); }

    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand CloseCommand { get; }

    public event Action? RequestClose;

    public CharacterProfilesViewModel(CharacterProfileStore store, ISettingsService settings, ILogger<CharacterProfilesViewModel> logger) : base(logger)
    {
        _store = store;
        _settings = settings;
        RefreshCommand = new RelayCommand(Load);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());
        CurrentUsername = settings.Get().Connection.LastUsername; // placeholder until login workflow sets current user
        Load();
    }

    private void Load()
    {
        Characters.Clear();
        if (string.IsNullOrWhiteSpace(CurrentUsername)) return;
        foreach (var c in _store.GetCharacters(CurrentUsername))
        {
            Characters.Add(new CharacterProfileDisplay
            {
                Name = c.Name,
                House = c.House,
                Level = c.Level,
                Experience = c.Experience,
                Class = c.Class
            });
        }
    }
}
