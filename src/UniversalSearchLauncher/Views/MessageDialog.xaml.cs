using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace UniversalSearchLauncher.Views;

public partial class MessageDialog : Window
{
    public MessageDialog(
        string title,
        string message,
        string primaryText = "OK",
        string? secondaryText = null,
        string? headline = null)
    {
        InitializeComponent();
        HeadlineText.Text = headline ?? title;
        Title = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;

        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            SecondaryButton.Content = secondaryText;
            SecondaryButton.Visibility = Visibility.Visible;
        }

        if (Application.Current?.MainWindow is { IsVisible: true } owner && !ReferenceEquals(owner, this))
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
        }
    }

    public static bool Show(
        Window? owner,
        string title,
        string message,
        string primaryText = "OK",
        string? secondaryText = null,
        string? headline = null)
    {
        var dialog = new MessageDialog(title, message, primaryText, secondaryText, headline);
        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.ShowInTaskbar = false;
        }

        return dialog.ShowDialog() == true;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
