using System.Collections.ObjectModel;
using System.Windows;
using DoorTelnet.Wpf.ViewModels;

namespace DoorTelnet.Wpf.Views.Dialogs;

public partial class CombatHistoryDialog : Window
{
    public CombatHistoryDialog(ObservableCollection<CombatViewModel.CombatEntryDisplay> completedCombats)
    {
        InitializeComponent();
        
        // Set the DataGrid's ItemsSource to the completed combats
        // Sort by start time descending (most recent first)
        var sortedCombats = new ObservableCollection<CombatViewModel.CombatEntryDisplay>(
            completedCombats.OrderByDescending(c => c.StartTime));
        
        HistoryDataGrid.ItemsSource = sortedCombats;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
