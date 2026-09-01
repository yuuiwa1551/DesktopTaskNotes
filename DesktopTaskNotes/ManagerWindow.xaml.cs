using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTaskNotes.Data;
using DesktopTaskNotes.Dialogs;
using DesktopTaskNotes.Models;
using DesktopTaskNotes.Services;
using Microsoft.Win32;

namespace DesktopTaskNotes;

public partial class ManagerWindow : Window
{
    private readonly DatabaseService _database;
    private readonly BackupService _backupService;
    private readonly WindowManager _windowManager;
    private readonly AppPaths _paths;
    private readonly DispatcherTimer _searchTimer;
    private bool _allowClose;
    private bool _loadingSettings;
    private int _refreshGeneration;

    public ManagerWindow(DatabaseService database, BackupService backupService, WindowManager windowManager, AppPaths paths)
    {
        InitializeComponent();
        _database = database;
        _backupService = backupService;
        _windowManager = windowManager;
        _paths = paths;
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await RefreshAsync();
        };
        Loaded += async (_, _) => await RefreshAsync();
        Closing += ManagerWindow_Closing;
        BackupPathText.Text = $"自动备份位置：{_paths.BackupDirectory}\n保留最近 7 个日备份和 4 个周备份。";
    }

    public async Task RefreshAsync(Guid? preferredNoteId = null)
    {
        var generation = ++_refreshGeneration;
        var search = SearchBox.Text?.Trim() ?? string.Empty;
        preferredNoteId ??= (NotesGrid.SelectedItem as NoteSummary)?.Id;
        var preferredItemId = (ItemsGrid.SelectedItem as ChecklistItem)?.Id;
        var preferredArchiveId = (ArchiveGrid.SelectedItem as ArchiveEntry)?.ItemId;
        var preferredTrashId = (TrashGrid.SelectedItem as NoteSummary)?.Id;

        var notesTask = _database.GetNoteSummariesAsync(search: search);
        var itemsTask = _database.SearchActiveItemsAsync(search);
        var archiveTask = _database.GetArchiveAsync(search);
        var trashTask = _database.GetNoteSummariesAsync(true, search);
        await Task.WhenAll(notesTask, itemsTask, archiveTask, trashTask);
        if (generation != _refreshGeneration) return;

        var notes = notesTask.Result;
        foreach (var note in notes) note.IsMinimized = _windowManager.IsNoteMinimized(note.Id);
        var items = itemsTask.Result;
        var archive = archiveTask.Result;
        var trash = trashTask.Result;

        NotesGrid.ItemsSource = notes;
        ItemsGrid.ItemsSource = items;
        ArchiveGrid.ItemsSource = archive;
        TrashGrid.ItemsSource = trash;
        NotesGrid.SelectedItem = notes.FirstOrDefault(n => n.Id == preferredNoteId) ?? notes.FirstOrDefault();
        ItemsGrid.SelectedItem = items.FirstOrDefault(i => i.Id == preferredItemId) ?? items.FirstOrDefault();
        ArchiveGrid.SelectedItem = archive.FirstOrDefault(i => i.ItemId == preferredArchiveId) ?? archive.FirstOrDefault();
        TrashGrid.SelectedItem = trash.FirstOrDefault(i => i.Id == preferredTrashId) ?? trash.FirstOrDefault();

        NotesEmptyState.Visibility = notes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ItemsEmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ArchiveEmptyState.Visibility = archive.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TrashEmptyState.Visibility = trash.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotesEmptyState.Text = search.Length == 0
            ? "还没有便利贴，先从右上角新建一张"
            : "没有匹配的便利贴";
        ItemsEmptyState.Text = search.Length == 0 ? "当前没有待办" : "没有匹配的待办";
        ArchiveEmptyState.Text = search.Length == 0 ? "还没有归档事项" : "没有匹配的归档事项";
        TrashEmptyState.Text = search.Length == 0 ? "回收站是空的" : "回收站中没有匹配项";
        UpdateActionStates();

        _loadingSettings = true;
        StartupCheckBox.IsChecked = StartupService.IsEnabled();
        _loadingSettings = false;
    }

    public Task RefreshAndSelectNoteAsync(Guid noteId) => RefreshAsync(noteId);

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    private async void NewChecklist_Click(object sender, RoutedEventArgs e) => await _windowManager.CreateNoteAsync(NoteKind.Checklist, this);
    private async void NewProject_Click(object sender, RoutedEventArgs e) => await _windowManager.CreateNoteAsync(NoteKind.Project, this);
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void ShowNote_Click(object sender, RoutedEventArgs e)
    {
        if (NotesGrid.SelectedItem is NoteSummary note) await _windowManager.ShowNoteAsync(note.Id, true);
    }

    private async void HideNote_Click(object sender, RoutedEventArgs e)
    {
        if (NotesGrid.SelectedItem is NoteSummary note) await _windowManager.HideNoteAsync(note.Id);
    }

    private async void EditNote_Click(object sender, RoutedEventArgs e)
    {
        if (NotesGrid.SelectedItem is not NoteSummary summary) return;
        var note = await _database.GetNoteAsync(summary.Id);
        if (note is null) return;
        var dialog = new NoteEditorDialog(_database, note) { Owner = this };
        await dialog.RestoreWindowSizeAsync();
        if (dialog.ShowDialog() != true) return;
        note.Title = dialog.NoteTitle;
        note.Kind = dialog.NoteKind;
        note.Color = dialog.NoteColor;
        note.ProjectDueAt = dialog.ProjectDueAt;
        await _database.UpdateNoteMetadataAsync(note);
        await _windowManager.RefreshNoteAsync(note.Id);
        await RefreshAsync();
    }

    private async void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (NotesGrid.SelectedItem is NoteSummary note) await _windowManager.DeleteNoteAsync(note.Id, this);
    }

    private async void NotesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (NotesGrid.SelectedItem is NoteSummary note) await _windowManager.ShowNoteAsync(note.Id, true);
    }

    private async void ItemsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => await OpenSelectedItemAsync();
    private async void OpenItem_Click(object sender, RoutedEventArgs e) => await OpenSelectedItemAsync();

    private async Task OpenSelectedItemAsync()
    {
        if (ItemsGrid.SelectedItem is ChecklistItem item) await _windowManager.OpenItemAsync(item.NoteId, item.Id);
    }

    private async void RestoreArchive_Click(object sender, RoutedEventArgs e)
    {
        if (ArchiveGrid.SelectedItem is not ArchiveEntry item) return;
        var noteId = await _database.RestoreArchivedItemAsync(item.ItemId);
        await _windowManager.ShowNoteAsync(noteId, true);
        await _windowManager.RefreshNoteAsync(noteId);
        await RefreshAsync();
    }

    private async void RestoreNote_Click(object sender, RoutedEventArgs e)
    {
        if (TrashGrid.SelectedItem is not NoteSummary note) return;
        await _database.RestoreNoteAsync(note.Id);
        await _windowManager.ShowNoteAsync(note.Id, true);
        await RefreshAsync();
    }

    private async void PermanentDeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (TrashGrid.SelectedItem is not NoteSummary note) return;
        if (!AppDialog.ShowConfirmation(this, "永久删除",
                $"永久删除“{note.Title}”及其中全部事项？\n此操作无法撤销。", "永久删除", true)) return;
        await _database.PermanentlyDeleteNoteAsync(note.Id);
        await RefreshAsync();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchPlaceholder is not null)
        {
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (!IsLoaded) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void GridSelection_Changed(object sender, SelectionChangedEventArgs e) => UpdateActionStates();

    private void UpdateActionStates()
    {
        if (!IsInitialized) return;
        var hasNote = NotesGrid.SelectedItem is NoteSummary;
        ShowNoteButton.IsEnabled = hasNote;
        HideNoteButton.IsEnabled = NotesGrid.SelectedItem is NoteSummary { IsHidden: false };
        EditNoteButton.IsEnabled = hasNote;
        DeleteNoteButton.IsEnabled = hasNote;
        if (NotesGrid.SelectedItem is NoteSummary selectedNote)
        {
            ShowNoteButton.Content = selectedNote.IsHidden
                ? "显示到桌面"
                : selectedNote.IsMinimized ? "恢复到桌面" : "打开便利贴";
        }
        else
        {
            ShowNoteButton.Content = "打开便利贴";
        }
        OpenItemButton.IsEnabled = ItemsGrid.SelectedItem is ChecklistItem;
        RestoreArchiveButton.IsEnabled = ArchiveGrid.SelectedItem is ArchiveEntry;
        var hasTrash = TrashGrid.SelectedItem is NoteSummary;
        RestoreNoteButton.IsEnabled = hasTrash;
        PermanentDeleteNoteButton.IsEnabled = hasTrash;
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.Source != MainTabs) return;
        SearchContainer.Visibility = MainTabs.SelectedIndex == 4 ? Visibility.Collapsed : Visibility.Visible;
        await RefreshAsync();
    }

    private async void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || !IsLoaded) return;
        try
        {
            var enabled = StartupCheckBox.IsChecked == true;
            StartupService.SetEnabled(enabled);
            await _database.SetSettingAsync("StartWithWindows", enabled ? "true" : "false");
            SetSettingsStatus(enabled ? "已启用开机启动。" : "已关闭开机启动。", true);
        }
        catch (Exception ex)
        {
            _loadingSettings = true;
            StartupCheckBox.IsChecked = StartupService.IsEnabled();
            _loadingSettings = false;
            SetSettingsStatus($"设置失败：{ex.Message}", false);
        }
    }

    private async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        SetBackupBusy(true);
        SetSettingsStatus("正在创建备份…", null);
        try
        {
            var path = Path.Combine(_paths.BackupDirectory, $"manual-{DateTime.Now:yyyy-MM-dd-HHmmss}.dtnbackup");
            await _backupService.CreateBackupAsync(path);
            SetSettingsStatus($"备份已保存：{path}", true);
        }
        catch (Exception ex)
        {
            SetSettingsStatus($"备份失败：{ex.Message}", false);
        }
        finally { SetBackupBusy(false); }
    }

    private async void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "桌面事项贴备份 (*.dtnbackup)|*.dtnbackup",
            FileName = $"桌面事项贴-{DateTime.Now:yyyy-MM-dd-HHmm}.dtnbackup",
            AddExtension = true,
            DefaultExt = ".dtnbackup"
        };
        if (dialog.ShowDialog(this) != true) return;
        SetBackupBusy(true);
        SetSettingsStatus("正在导出备份…", null);
        try
        {
            await _backupService.CreateBackupAsync(dialog.FileName);
            SetSettingsStatus("备份导出成功。", true);
        }
        catch (Exception ex)
        {
            SetSettingsStatus($"导出失败：{ex.Message}", false);
        }
        finally { SetBackupBusy(false); }
    }

    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "桌面事项贴备份 (*.dtnbackup)|*.dtnbackup" };
        if (dialog.ShowDialog(this) != true) return;
        if (!AppDialog.ShowConfirmation(this, "恢复备份",
                "恢复会用备份替换当前数据，并在恢复前自动保存一份快照。", "开始恢复", true)) return;
        SetBackupBusy(true);
        SetSettingsStatus("正在校验并恢复备份…", null);
        try
        {
            await _backupService.RestoreBackupAsync(dialog.FileName);
            AppDialog.ShowMessage(this, "恢复成功", "数据已经恢复，桌面事项贴将重新启动。");
            ((App)Application.Current).ExitApplication(false, true);
        }
        catch (Exception ex)
        {
            AppDialog.ShowMessage(this, "恢复失败", ex.Message, true);
            SetSettingsStatus($"恢复失败：{ex.Message}", false);
        }
        finally { SetBackupBusy(false); }
    }

    private void SetBackupBusy(bool busy)
    {
        BackupNowButton.IsEnabled = !busy;
        ExportBackupButton.IsEnabled = !busy;
        RestoreBackupButton.IsEnabled = !busy;
    }

    private void SetSettingsStatus(string message, bool? success)
    {
        SettingsStatusText.Text = message;
        SettingsStatusText.Foreground = success switch
        {
            true => (Brush)FindResource("SuccessBrush"),
            false => (Brush)FindResource("DangerBrush"),
            _ => (Brush)FindResource("TextSecondaryBrush")
        };
    }

    private void ManagerWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
    }
}
