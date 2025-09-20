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

    private void PwdBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is CredentialsViewModel vm && sender is System.Windows.Controls.PasswordBox pb)
        {
            vm.Password = pb.Password;
        }
    }
}
