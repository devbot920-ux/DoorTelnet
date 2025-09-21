using System.Windows;
using DoorTelnet.Wpf.ViewModels;

namespace DoorTelnet.Wpf.Views.Dialogs;

public partial class HotKeysDialog : Window
{
    public HotKeysDialog(HotKeysViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += () => this.Close();
    }
}