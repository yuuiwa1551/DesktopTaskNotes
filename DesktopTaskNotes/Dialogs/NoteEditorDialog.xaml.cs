using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using DesktopTaskNotes.Data;
using DesktopTaskNotes.Models;
using Forms = System.Windows.Forms;

namespace DesktopTaskNotes.Dialogs;

public partial class NoteEditorDialog : Window
{
    private const string WidthSettingKey = "NoteEditorWidth";
    private const string HeightSettingKey = "NoteEditorHeight";
    private const double DefaultWidth = 480;
    private const double DefaultHeight = 520;
    private const double WorkAreaMargin = 32;

    private readonly DatabaseService _database;
    private bool _sizeRestored;

    public NoteEditorDialog(DatabaseService database, StickyNote? note = null)
    {
        InitializeComponent();
        _database = database;
        Loaded += (_, _) =>
        {
            ClampSizeToOwnerScreen();
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
        if (note is not null)
        {
            TitleBox.Text = note.Title;
            ChecklistRadio.IsChecked = note.Kind == NoteKind.Checklist;
            ProjectRadio.IsChecked = note.Kind == NoteKind.Project;
            DueDatePicker.SelectedDate = note.ProjectDueAt?.LocalDateTime.Date;
            foreach (var radio in ColorRadios())
            {
                if (string.Equals(radio.Tag?.ToString(), note.Color, StringComparison.OrdinalIgnoreCase))
                {
                    radio.IsChecked = true;
                    break;
                }
            }
        }
        UpdateDueVisibility();
    }

    public async Task RestoreWindowSizeAsync()
    {
        var width = ParseSize(await _database.GetSettingAsync(WidthSettingKey), DefaultWidth);
        var height = ParseSize(await _database.GetSettingAsync(HeightSettingKey), DefaultHeight);
        Width = width;
        Height = height;
        ClampSizeToOwnerScreen();
        _sizeRestored = true;
    }

    public string NoteTitle { get; private set; } = string.Empty;
    public NoteKind NoteKind { get; private set; }
    public string NoteColor { get; private set; } = "#FFF2A8";
    public DateTimeOffset? ProjectDueAt { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            MessageBox.Show(this, "请输入便利贴标题。", "桌面事项贴", MessageBoxButton.OK, MessageBoxImage.Information);
            TitleBox.Focus();
            return;
        }

        NoteTitle = TitleBox.Text.Trim();
        NoteKind = ProjectRadio.IsChecked == true ? NoteKind.Project : NoteKind.Checklist;
        NoteColor = ColorRadios().FirstOrDefault(radio => radio.IsChecked == true)?.Tag?.ToString() ?? "#FFF2A8";
        ProjectDueAt = NoteKind == NoteKind.Project && DueDatePicker.SelectedDate is DateTime date
            ? new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date))
            : null;
        DialogResult = true;
    }

    private void Type_Checked(object sender, RoutedEventArgs e) => UpdateDueVisibility();

    private void UpdateDueVisibility()
    {
        if (DuePanel is null || ProjectRadio is null) return;
        DuePanel.Visibility = ProjectRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_sizeRestored) return;

        var width = WindowState == WindowState.Normal ? ActualWidth : RestoreBounds.Width;
        var height = WindowState == WindowState.Normal ? ActualHeight : RestoreBounds.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height)) return;

        try
        {
            await _database.SetSettingAsync(WidthSettingKey, width.ToString(CultureInfo.InvariantCulture));
            await _database.SetSettingAsync(HeightSettingKey, height.ToString(CultureInfo.InvariantCulture));
        }
        catch
        {
            // Window preferences must never prevent the dialog from closing.
        }
    }

    private void ClampSizeToOwnerScreen()
    {
        double availableWidth;
        double availableHeight;
        if (Owner is { IsLoaded: true })
        {
            var dpi = VisualTreeHelper.GetDpi(Owner);
            var workArea = Forms.Screen.FromHandle(new WindowInteropHelper(Owner).Handle).WorkingArea;
            availableWidth = workArea.Width / dpi.DpiScaleX;
            availableHeight = workArea.Height / dpi.DpiScaleY;
        }
        else
        {
            // SystemParameters.WorkArea is already expressed in WPF device-independent units.
            availableWidth = SystemParameters.WorkArea.Width;
            availableHeight = SystemParameters.WorkArea.Height;
        }

        var maximumWidth = Math.Max(MinWidth, availableWidth - WorkAreaMargin);
        var maximumHeight = Math.Max(MinHeight, availableHeight - WorkAreaMargin);
        Width = Math.Clamp(Width, MinWidth, maximumWidth);
        Height = Math.Clamp(Height, MinHeight, maximumHeight);
    }

    private static double ParseSize(string? value, double fallback)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
               && double.IsFinite(parsed)
            ? parsed
            : fallback;
    }

    private IEnumerable<RadioButton> ColorRadios()
    {
        yield return YellowRadio;
        yield return MintRadio;
        yield return PinkRadio;
        yield return PurpleRadio;
        yield return BlueRadio;
        yield return GrayRadio;
    }
}
