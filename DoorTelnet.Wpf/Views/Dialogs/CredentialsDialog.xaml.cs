using System.Windows;
using DoorTelnet.Wpf.ViewModels;

namespace DoorTelnet.Wpf.Views.Dialogs;

public partial class CredentialsDialog : Window
{
    public CredentialsDialog(CredentialsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += () => Dispatcher.Invoke(Close);
    }
}
