using System.Windows;
using DesktopTaskNotes.Data;
using DesktopTaskNotes.Dialogs;
using DesktopTaskNotes.Models;

namespace DesktopTaskNotes.Services;

public sealed class WindowManager
{
    private readonly DatabaseService _database;
    private readonly BackupService _backupService;
    private readonly AppPaths _paths;
    private readonly Dictionary<Guid, StickyNoteWindow> _noteWindows = [];
    private ManagerWindow? _managerWindow;

    public WindowManager(DatabaseService database, BackupService backupService, AppPaths paths)
    {
        _database = database;
        _backupService = backupService;
        _paths = paths;
    }

    public async Task LoadNotesAsync()
    {
        foreach (var note in await _database.GetActiveNotesAsync())
        {
            if (!note.IsHidden) await ShowNoteAsync(note.Id, false);
        }
    }

    public void ShowManager()
    {
        if (_managerWindow is null)
            _managerWindow = new ManagerWindow(_database, _backupService, this, _paths);
        if (!_managerWindow.IsVisible) _managerWindow.Show();
        if (_managerWindow.WindowState == WindowState.Minimized) _managerWindow.WindowState = WindowState.Normal;
        _managerWindow.Activate();
        _ = _managerWindow.RefreshAsync();
    }

    public async Task CreateNoteAsync(NoteKind kind, Window? owner = null)
    {
        var temporary = new StickyNote { Kind = kind, Color = kind == NoteKind.Project ? "#DDEEFF" : "#FFF2A8" };
        var dialog = new NoteEditorDialog(_database, temporary) { Owner = owner };
        await dialog.RestoreWindowSizeAsync();
        if (dialog.ShowDialog() != true) return;
        var note = await _database.CreateNoteAsync(dialog.NoteKind, dialog.NoteTitle, dialog.NoteColor);
        note.ProjectDueAt = dialog.ProjectDueAt;
        await _database.UpdateNoteMetadataAsync(note);
        await ShowNoteAsync(note.Id, true);
        if (_managerWindow?.IsVisible == true) _ = _managerWindow.RefreshAndSelectNoteAsync(note.Id);
    }

    public async Task ShowNoteAsync(Guid noteId, bool activate)
    {
        var note = await _database.GetNoteAsync(noteId);
        if (note is null || note.DeletedAt is not null) return;
        note.IsHidden = false;
        await _database.SetNoteHiddenAsync(noteId, false);

        if (!_noteWindows.TryGetValue(noteId, out var window))
        {
            window = new StickyNoteWindow(_database, this, note);
            _noteWindows[noteId] = window;
            window.Show();
        }
        else if (!window.IsVisible)
        {
            window.Show();
        }

        await window.ReloadAsync();
        if (activate) window.Activate();
        RefreshManager();
    }

    public async Task HideNoteAsync(Guid noteId)
    {
        await _database.SetNoteHiddenAsync(noteId, true);
        if (_noteWindows.TryGetValue(noteId, out var window))
        {
            window.Note.IsHidden = true;
            window.Hide();
        }
        RefreshManager();
    }

    public async Task DeleteNoteAsync(Guid noteId, Window owner)
    {
        var note = await _database.GetNoteAsync(noteId);
        if (note is null) return;
        if (MessageBox.Show(owner, $"把“{note.Title}”移到回收站？30 天内可以恢复。", "移到回收站",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _database.SoftDeleteNoteAsync(noteId);
        if (_noteWindows.Remove(noteId, out var window)) window.ForceClose();
        RefreshManager();
    }

    public async Task RefreshNoteAsync(Guid noteId)
    {
        if (_noteWindows.TryGetValue(noteId, out var window)) await window.ReloadAsync();
    }

    public async Task OpenItemAsync(Guid noteId, Guid itemId)
    {
        await ShowNoteAsync(noteId, false);
        if (_noteWindows.TryGetValue(noteId, out var window)) await window.FocusItemAsync(itemId);
    }

    public void RefreshManager()
    {
        if (_managerWindow?.IsVisible == true) _ = _managerWindow.RefreshAsync();
    }

    public void CloseAll()
    {
        foreach (var window in _noteWindows.Values.ToList()) window.ForceClose();
        _noteWindows.Clear();
        _managerWindow?.ForceClose();
        _managerWindow = null;
    }
}
