namespace DoorTelnet.Core.Navigation.Models;

/// <summary>
/// Represents a navigation suggestion for autocomplete functionality
/// </summary>
public class NavigationSuggestion
{
    public string RoomId { get; set; } = string.Empty;
    public string RoomName { get; set; } = string.Empty;
    public string Sector { get; set; } = string.Empty;
    public int Distance { get; set; }
    public NavigationSuggestionType SuggestionType { get; set; }

    /// <summary>
    /// Display text for the suggestion
    /// </summary>
    public string DisplayText => SuggestionType switch
    {
        NavigationSuggestionType.RoomId => $"#{RoomId} - {RoomName}",
        NavigationSuggestionType.Store => $"{RoomName} ({Distance} moves) - {Sector}",
        NavigationSuggestionType.RoomName => Distance < int.MaxValue 
            ? $"{RoomName} ({Distance} moves) - {Sector}"
            : $"{RoomName} - {Sector}",
        _ => RoomName
    };

    /// <summary>
    /// Short display text for selected item
    /// </summary>
    public string ShortText => SuggestionType switch
    {
        NavigationSuggestionType.RoomId => RoomId,
        _ => RoomName
    };

    /// <summary>
    /// Override ToString for ComboBox display when no template is used
    /// </summary>
    public override string ToString() => ShortText;
}

/// <summary>
/// Types of navigation suggestions
/// </summary>
public enum NavigationSuggestionType
{
    RoomName,
    RoomId,
    Store
}