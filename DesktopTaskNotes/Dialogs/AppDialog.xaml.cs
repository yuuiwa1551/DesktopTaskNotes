using System.Windows;
using System.Windows.Media;

namespace DesktopTaskNotes.Dialogs;

public partial class AppDialog : Window
{
    private AppDialog(Window? owner, string title, string message)
    {
        InitializeComponent();
        Title = title;
        HeadingText.Text = title;
        MessageText.Text = message;
        if (owner is { IsLoaded: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    public static bool ShowConfirmation(Window? owner, string title, string message,
        string primaryText = "确定", bool danger = false)
    {
        var dialog = new AppDialog(owner, title, message);
        dialog.PrimaryButton.Content = primaryText;
        if (danger)
        {
            dialog.PrimaryButton.Style = (Style)dialog.FindResource("DangerPrimaryButtonStyle");
            dialog.IconSurface.Background = new SolidColorBrush(Color.FromRgb(255, 246, 245));
            dialog.IconText.Foreground = new SolidColorBrush(Color.FromRgb(217, 45, 32));
            dialog.IconText.Text = "\uE7BA";
        }
        return dialog.ShowDialog() == true;
    }

    public static void ShowMessage(Window? owner, string title, string message, bool isError = false)
    {
        var dialog = new AppDialog(owner, title, message);
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        dialog.PrimaryButton.Content = "知道了";
        if (isError)
        {
            dialog.IconSurface.Background = new SolidColorBrush(Color.FromRgb(255, 246, 245));
            dialog.IconText.Foreground = new SolidColorBrush(Color.FromRgb(217, 45, 32));
            dialog.IconText.Text = "\uEA39";
        }
        dialog.ShowDialog();
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
