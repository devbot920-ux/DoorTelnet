using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using DoorTelnet.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DoorTelnet.Wpf;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Current is App app && app._host != null
            ? app._host.Services.GetRequiredService<MainViewModel>()
            : null;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Close, (_, _) => Close()));
    }

    protected override void OnContentRendered(System.EventArgs e)
    {
        base.OnContentRendered(e);
    }
}