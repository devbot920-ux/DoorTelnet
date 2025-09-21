using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DoorTelnet.Wpf.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DoorTelnet.Wpf;

public partial class MainWindow : Window
{
    public LogViewModel? LogVm { get; set; }
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Current is App app && app._host != null
            ? app._host.Services.GetRequiredService<MainViewModel>()
            : null;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Close, (_, _) => Close()));
        Activated += (_, _) => FocusTerminal();
        Loaded += (_, _) => FocusTerminal();
    }

    private void FocusTerminal()
    {
        if (Terminal != null)
        {
            Terminal.Focus();
            Keyboard.Focus(Terminal);
        }
    }

    private void AutoGongToggle_Click(object sender, RoutedEventArgs e)
    {
        FocusTerminal();
    }

    private void AutoAttackToggle_Click(object sender, RoutedEventArgs e)
    {
        FocusTerminal();
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        // Connection command already bound; just restore focus
        // Delay slight to allow any command event loop
        Dispatcher.BeginInvoke(FocusTerminal, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void UserButton_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button button && button.ContextMenu != null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void UserButton_Click(object sender, RoutedEventArgs e)
    {
        // Also show context menu on left-click for better UX
        if (sender is Button button && button.ContextMenu != null)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }
        FocusTerminal();
    }
}