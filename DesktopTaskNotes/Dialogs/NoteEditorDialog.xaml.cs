using System.Windows;
using System.Windows.Controls;
using DesktopTaskNotes.Models;

namespace DesktopTaskNotes.Dialogs;

public partial class NoteEditorDialog : Window
{
    public NoteEditorDialog(StickyNote? note = null)
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
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
