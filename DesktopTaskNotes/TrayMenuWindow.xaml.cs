using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DesktopTaskNotes.Models;
using DesktopTaskNotes.Services;
using Forms = System.Windows.Forms;

namespace DesktopTaskNotes;

public partial class TrayMenuWindow : Window
{
    private readonly WindowManager _windowManager;
    private readonly App _app;
    private bool _allowClose;

    public TrayMenuWindow(WindowManager windowManager, App app)
    {
        InitializeComponent();
        _windowManager = windowManager;
        _app = app;
        Deactivated += (_, _) => Hide();
        Closing += (_, e) =>
        {
            if (_allowClose) return;
            e.Cancel = true;
            Hide();
        };
    }

    public void ShowAtCursor()
    {
        if (!IsVisible) Show();
        var dpi = VisualTreeHelper.GetDpi(this);
        var cursor = Forms.Cursor.Position;
        var workArea = Forms.Screen.FromPoint(cursor).WorkingArea;
        var left = cursor.X / dpi.DpiScaleX - Width + 8;
        var top = cursor.Y / dpi.DpiScaleY - Height + 8;
        Left = Math.Clamp(left, workArea.Left / dpi.DpiScaleX + 8,
            workArea.Right / dpi.DpiScaleX - Width - 8);
        Top = Math.Clamp(top, workArea.Top / dpi.DpiScaleY + 8,
            workArea.Bottom / dpi.DpiScaleY - Height - 8);
        Activate();
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private void OpenManager_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _windowManager.ShowManager();
    }

    private async void NewChecklist_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        await _windowManager.CreateNoteAsync(NoteKind.Checklist);
    }

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        await _windowManager.CreateNoteAsync(NoteKind.Project);
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _app.ExitApplication(true);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Hide();
        e.Handled = true;
    }
}
