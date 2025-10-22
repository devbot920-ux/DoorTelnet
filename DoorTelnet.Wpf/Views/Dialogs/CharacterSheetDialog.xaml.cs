using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DoorTelnet.Wpf.ViewModels;

namespace DoorTelnet.Wpf.Views.Dialogs;

public partial class CharacterSheetDialog : Window
{
    private static readonly Regex _digitsOnly = new("^\\d+$");
    public CharacterSheetDialog(CharacterSheetViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += () => this.Close();
        this.AddHandler(TextBox.PreviewTextInputEvent, new TextCompositionEventHandler(NumericOnlyHandler), true);
        DataObject.AddPastingHandler(this, OnPasteNumericOnly);
    }

    private void TextBox_SelectAll(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.Dispatcher.BeginInvoke(() => tb.SelectAll());
        }
    }

    private void TextBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox tb && !tb.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            tb.Focus();
            tb.SelectAll();
        }
    }

    private void NumericOnly(object sender, TextCompositionEventArgs e) => NumericOnlyHandler(sender, e);

    private void NumericOnlyHandler(object? sender, TextCompositionEventArgs e)
    {
        if (!_digitsOnly.IsMatch(e.Text)) e.Handled = true;
    }

    private void OnPasteNumericOnly(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Text))
        {
            var text = e.DataObject.GetData(DataFormats.Text) as string ?? string.Empty;
            if (!_digitsOnly.IsMatch(text)) e.CancelCommand();
        }
        else e.CancelCommand();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}