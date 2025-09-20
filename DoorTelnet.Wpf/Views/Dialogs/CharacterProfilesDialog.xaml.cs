using System.Windows;
using DoorTelnet.Wpf.ViewModels;

namespace DoorTelnet.Wpf.Views.Dialogs;

public partial class CharacterProfilesDialog : Window
{
    public CharacterProfilesDialog(CharacterProfilesViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += () => Dispatcher.Invoke(Close);
    }
}
