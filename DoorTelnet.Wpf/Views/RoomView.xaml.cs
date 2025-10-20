using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;

namespace DoorTelnet.Wpf.Views;

public partial class RoomView : UserControl
{
    public RoomView()
    {
        InitializeComponent();
    }

    private void ComboBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox)
        {
            // Subscribe to events that might cause text selection
            comboBox.GotFocus += ComboBox_GotFocus;
            comboBox.PreviewTextInput += ComboBox_PreviewTextInput;
            comboBox.DropDownOpened += ComboBox_DropDownOpened;
        }
    }

    private void ComboBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.IsEditable)
        {
            // Get the internal TextBox
            var textBox = comboBox.Template?.FindName("PART_EditableTextBox", comboBox) as TextBox;
            if (textBox != null)
            {
                // Clear any existing selection and move cursor to end
                Dispatcher.BeginInvoke(() =>
                {
                    textBox.SelectionStart = textBox.Text?.Length ?? 0;
                    textBox.SelectionLength = 0;
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
        }
    }

    private void ComboBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.IsEditable)
        {
            // Get the internal TextBox
            var textBox = comboBox.Template?.FindName("PART_EditableTextBox", comboBox) as TextBox;
            if (textBox != null && textBox.SelectionLength > 0)
            {
                // If text is selected, clear selection and position cursor at end
                // This prevents overwriting when user types
                textBox.SelectionStart = textBox.Text?.Length ?? 0;
                textBox.SelectionLength = 0;
            }
        }
    }

    private void ComboBox_DropDownOpened(object? sender, EventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.IsEditable)
        {
            // Get the internal TextBox
            var textBox = comboBox.Template?.FindName("PART_EditableTextBox", comboBox) as TextBox;
            if (textBox != null)
            {
                // Ensure no text is selected when dropdown opens
                Dispatcher.BeginInvoke(() =>
                {
                    textBox.SelectionStart = textBox.Text?.Length ?? 0;
                    textBox.SelectionLength = 0;
                }, System.Windows.Threading.DispatcherPriority.Input);
            }
        }
    }
}
