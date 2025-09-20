using System.Windows;
using DoorTelnet.Wpf.ViewModels;

namespace DoorTelnet.Wpf.Views.Dialogs;

public partial class SettingsDialog : Window
{
    public SettingsDialog(SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
