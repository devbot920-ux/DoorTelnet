using System.Windows;
using DoorTelnet.Wpf.ViewModels;

namespace DoorTelnet.Wpf.Views.Dialogs;

public partial class CharacterSheetDialog : Window
{
    public CharacterSheetDialog(CharacterSheetViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += () => this.Close();
    }
}