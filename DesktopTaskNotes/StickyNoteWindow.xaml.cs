using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTaskNotes.Data;
using DesktopTaskNotes.Dialogs;
using DesktopTaskNotes.Interop;
using DesktopTaskNotes.Models;
using DesktopTaskNotes.Services;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace DesktopTaskNotes;

public partial class StickyNoteWindow : Window
{
    private readonly DatabaseService _database;
    private readonly WindowManager _windowManager;
    private readonly ObservableCollection<ChecklistItem> _items = [];
    private readonly DispatcherTimer _layoutSaveTimer;
    private readonly DispatcherTimer _desktopGuardTimer;
    private readonly bool _exposeForUiTest;
    private Point _dragStart;
    private Guid? _dragItemId;
    private List<Guid> _lastArchived = [];
    private bool _allowClose;
    private bool _loading;
    private bool _applyingCollapsedState;
    private bool _collapsedVisualApplied;
    private bool _manualMinimize;
    private double _expandedWidth;
    private double _expandedHeight;

    public StickyNoteWindow(DatabaseService database, WindowManager windowManager, StickyNote note)
    {
        InitializeComponent();
        _database = database;
        _windowManager = windowManager;
        Note = note;
        _exposeForUiTest = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DESKTOP_TASK_NOTES_TEST_ROOT"));
        ItemsList.ItemsSource = _items;
        _expandedWidth = Math.Max(note.Width, 280);
        _expandedHeight = Math.Max(note.Height, 220);

        _layoutSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _layoutSaveTimer.Tick += async (_, _) =>
        {
            _layoutSaveTimer.Stop();
            await SaveLayoutAsync();
        };
        _desktopGuardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _desktopGuardTimer.Tick += (_, _) => GuardDesktopVisibility();

        if (_exposeForUiTest) ShowInTaskbar = true;
        else SourceInitialized += (_, _) => WindowNative.ConfigureToolWindow(this);
        LocationChanged += (_, _) => ScheduleLayoutSave();
        SizeChanged += Window_SizeChanged;
        StateChanged += Window_StateChanged;
        Loaded += async (_, _) =>
        {
            _desktopGuardTimer.Start();
            await ReloadAsync();
        };
        Closing += StickyNoteWindow_Closing;
        ApplyNoteState();
    }

    public StickyNote Note { get; }
    public Guid NoteId => Note.Id;

    public async Task ReloadAsync()
    {
        _loading = true;
        try
        {
            var refreshed = await _database.GetNoteAsync(Note.Id);
            if (refreshed is not null)
            {
                Note.Title = refreshed.Title;
                Note.Kind = refreshed.Kind;
                Note.Color = refreshed.Color;
                Note.WindowMode = refreshed.WindowMode;
                Note.ProjectDueAt = refreshed.ProjectDueAt;
                Note.IsHidden = refreshed.IsHidden;
            }

            var items = await _database.GetItemsForNoteAsync(Note.Id);
            _items.Clear();
            foreach (var item in items) _items.Add(item);
            ApplyNoteState();
            UpdateProgress();
            StatusText.Text = $"{_items.Count(i => !i.IsCompleted)} 项未完成";
            CollapsedStatusText.Text = StatusText.Text;
        }
        finally
        {
            _loading = false;
        }
    }

    public async Task FocusItemAsync(Guid itemId)
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        await ReloadAsync();
        Activate();
        foreach (var element in FindVisualChildren<Border>(ItemsList))
        {
            if (element.Tag is Guid id && id == itemId)
            {
                element.Background = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
                element.BringIntoView();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) => { timer.Stop(); element.Background = Brushes.Transparent; };
                timer.Start();
                break;
            }
        }
    }

    public void ForceClose()
    {
        _allowClose = true;
        _desktopGuardTimer.Stop();
        Close();
    }

    private void ApplyNoteState()
    {
        TitleText.Text = Note.Title;
        NoteBorder.Background = BrushFrom(Note.Color);
        Topmost = Note.WindowMode == NoteWindowMode.Topmost;
        PinButton.Opacity = Topmost ? 1 : 0.55;
        PinButton.ToolTip = Topmost ? "取消始终置顶" : "始终置顶";
        ProjectPanel.Visibility = Note.Kind == NoteKind.Project ? Visibility.Visible : Visibility.Collapsed;

        if (!IsLoaded)
        {
            Left = Clamp(Note.Left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80);
            Top = Clamp(Note.Top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60);
            Width = Math.Max(MinWidth, Note.Width);
            Height = Math.Max(220, Note.Height);
        }
        ApplyCollapsedState();
    }

    private void UpdateProgress()
    {
        var total = _items.Count;
        var completed = _items.Count(i => i.IsCompleted);
        var progress = total == 0 ? 0 : completed * 100d / total;
        ProjectProgress.Value = progress;
        var due = Note.ProjectDueAt is { } dueAt ? $" · {dueAt.LocalDateTime:MM-dd}" : string.Empty;
        ProjectProgressText.Text = $"{completed}/{total}{due}";
    }

    private void ApplyCollapsedState()
    {
        _applyingCollapsedState = true;
        try
        {
            var visibility = Note.IsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ProjectPanel.Visibility = Note.IsCollapsed ? Visibility.Collapsed : Note.Kind == NoteKind.Project ? Visibility.Visible : Visibility.Collapsed;
            CollapsedSummary.Visibility = Note.IsCollapsed ? Visibility.Visible : Visibility.Collapsed;
            ItemsScroller.Visibility = visibility;
            AddPanel.Visibility = visibility;
            Footer.Visibility = visibility;
            ResizeMode = Note.IsCollapsed ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
            CollapseButton.Content = Note.IsCollapsed ? "\uE70E" : "\uE70D";
            if (Note.IsCollapsed)
            {
                if (!_collapsedVisualApplied)
                {
                    _expandedWidth = Math.Max(ActualWidth > 0 ? ActualWidth : Width, MinWidth);
                    _expandedHeight = Math.Max(ActualHeight > 0 ? ActualHeight : Height, 220);
                }
                MinHeight = 96;
                Height = 96;
                _collapsedVisualApplied = true;
            }
            else
            {
                var restoreExpandedSize = _collapsedVisualApplied;
                MinHeight = 220;
                if (restoreExpandedSize)
                {
                    Width = Math.Max(_expandedWidth, MinWidth);
                    Height = Math.Max(_expandedHeight, MinHeight);
                }
                _collapsedVisualApplied = false;
            }
        }
        finally
        {
            _applyingCollapsedState = false;
        }
    }

    private async void PinButton_Click(object sender, RoutedEventArgs e)
    {
        Note.WindowMode = Note.WindowMode == NoteWindowMode.Topmost ? NoteWindowMode.Desktop : NoteWindowMode.Topmost;
        Topmost = Note.WindowMode == NoteWindowMode.Topmost;
        PinButton.Opacity = Topmost ? 1 : 0.55;
        PinButton.ToolTip = Topmost ? "取消始终置顶" : "始终置顶";
        await _database.UpdateNoteMetadataAsync(Note);
    }

    private async void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        MorePanel.Visibility = Visibility.Collapsed;
        Note.IsCollapsed = !Note.IsCollapsed;
        ApplyCollapsedState();
        await SaveLayoutAsync();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        MorePanel.Visibility = Visibility.Collapsed;
        _manualMinimize = true;
        ShowInTaskbar = true;
        WindowNative.ConfigureTaskbarWindow(this);
        WindowState = WindowState.Minimized;
    }

    private async void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (Note.IsCollapsed)
        {
            Note.IsCollapsed = false;
            ApplyCollapsedState();
            await SaveLayoutAsync();
        }
        MorePanel.Visibility = MorePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (MorePanel.Visibility == Visibility.Visible) UpdateColorSelection();
    }

    private async void ColorSwatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string color }) return;
        Note.Color = color;
        NoteBorder.Background = BrushFrom(color);
        UpdateColorSelection();
        await _database.UpdateNoteMetadataAsync(Note);
        _windowManager.RefreshManager();
    }

    private async void EditNoteMenu_Click(object sender, RoutedEventArgs e)
    {
        MorePanel.Visibility = Visibility.Collapsed;
        await EditNoteAsync();
    }

    private async void HideNoteMenu_Click(object sender, RoutedEventArgs e)
    {
        MorePanel.Visibility = Visibility.Collapsed;
        await _windowManager.HideNoteAsync(Note.Id);
    }

    private async void DeleteNoteMenu_Click(object sender, RoutedEventArgs e)
    {
        MorePanel.Visibility = Visibility.Collapsed;
        await _windowManager.DeleteNoteAsync(Note.Id, this);
    }

    private void MoreBackdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
        MorePanel.Visibility = Visibility.Collapsed;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || MorePanel.Visibility != Visibility.Visible) return;
        MorePanel.Visibility = Visibility.Collapsed;
        e.Handled = true;
    }

    private void UpdateColorSelection()
    {
        foreach (var (button, check) in new[]
                 {
                     (YellowSwatch, YellowCheck), (MintSwatch, MintCheck), (PinkSwatch, PinkCheck),
                     (PurpleSwatch, PurpleCheck), (BlueSwatch, BlueCheck), (GraySwatch, GrayCheck)
                 })
        {
            var selected = string.Equals(button.Tag as string, Note.Color, StringComparison.OrdinalIgnoreCase);
            button.BorderThickness = selected ? new Thickness(2) : new Thickness(0);
            check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async Task EditNoteAsync()
    {
        var dialog = new NoteEditorDialog(Note) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        Note.Title = dialog.NoteTitle;
        Note.Kind = dialog.NoteKind;
        Note.Color = dialog.NoteColor;
        Note.ProjectDueAt = dialog.ProjectDueAt;
        await _database.UpdateNoteMetadataAsync(Note);
        await ReloadAsync();
        _windowManager.RefreshManager();
    }

    private async void NewItemBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        await AddItemAsync();
    }

    private async void AddItem_Click(object sender, RoutedEventArgs e) => await AddItemAsync();

    private void NewItemBox_TextChanged(object sender, TextChangedEventArgs e) =>
        NewItemPlaceholder.Visibility = string.IsNullOrEmpty(NewItemBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    private async Task AddItemAsync()
    {
        if (string.IsNullOrWhiteSpace(NewItemBox.Text)) return;
        await _database.AddItemAsync(Note.Id, NewItemBox.Text);
        NewItemBox.Clear();
        await ReloadAsync();
        ItemsScroller.ScrollToEnd();
        NewItemBox.Focus();
        _windowManager.RefreshManager();
    }

    private async void TaskCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox { DataContext: ChecklistItem item } checkBox) return;
        await _database.SetItemCompletedAsync(item.Id, checkBox.IsChecked == true);
        await ReloadAsync();
        _windowManager.RefreshManager();
    }

    private async void TaskText_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loading || sender is not TextBox { DataContext: ChecklistItem item } box) return;
        var value = box.Text.Trim();
        if (value.Length == 0)
        {
            box.Text = item.Text;
            return;
        }
        if (value == item.Text) return;
        item.Text = value;
        await _database.UpdateItemAsync(item);
        _windowManager.RefreshManager();
    }

    private async void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        _lastArchived = await _database.ArchiveCompletedAsync(Note.Id);
        if (_lastArchived.Count == 0)
        {
            StatusText.Text = "没有已完成事项";
            return;
        }
        StatusText.Text = $"已清理 {_lastArchived.Count} 项";
        UndoButton.Visibility = Visibility.Visible;
        await ReloadAsync();
        StatusText.Text = $"已清理 {_lastArchived.Count} 项";
        UndoButton.Visibility = Visibility.Visible;
        _windowManager.RefreshManager();
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        await _database.UndoArchiveAsync(_lastArchived);
        _lastArchived.Clear();
        UndoButton.Visibility = Visibility.Collapsed;
        await ReloadAsync();
        _windowManager.RefreshManager();
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        if (FindVisualParent<Button>(source) is not null || FindVisualParent<TextBox>(source) is not null) return;
        DragMove();
    }

    private void Header_MouseEnter(object sender, MouseEventArgs e) => HeaderButtons.Opacity = 1;
    private void Header_MouseLeave(object sender, MouseEventArgs e) => HeaderButtons.Opacity = 0.35;

    private void DragHandle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItemId = sender is FrameworkElement { DataContext: ChecklistItem item } ? item.Id : null;
    }

    private void DragHandle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItemId is null) return;
        var point = e.GetPosition(null);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop((DependencyObject)sender, _dragItemId.Value, DragDropEffects.Move);
    }

    private async void TaskRow_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(Guid)) || sender is not Border { DataContext: ChecklistItem target }) return;
        var sourceId = (Guid)e.Data.GetData(typeof(Guid));
        await _database.ReorderItemAsync(sourceId, target.Id);
        await ReloadAsync();
    }

    private void ScheduleLayoutSave()
    {
        if (_loading || !IsLoaded || WindowState != WindowState.Normal) return;
        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_loading && !_applyingCollapsedState && IsLoaded && !Note.IsCollapsed && WindowState == WindowState.Normal)
        {
            _expandedWidth = Math.Max(ActualWidth, MinWidth);
            _expandedHeight = Math.Max(ActualHeight, MinHeight);
        }
        ScheduleLayoutSave();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal && _manualMinimize)
        {
            _manualMinimize = false;
            if (!_exposeForUiTest)
            {
                ShowInTaskbar = false;
                WindowNative.ConfigureToolWindow(this);
            }
        }
        GuardDesktopVisibility();
    }

    private async Task SaveLayoutAsync()
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        Note.Left = Left;
        Note.Top = Top;
        Note.Width = Width;
        if (!Note.IsCollapsed) Note.Height = Height;
        await _database.UpdateNoteLayoutAsync(Note.Id, Note.Left, Note.Top, Note.Width, Note.Height, Note.IsCollapsed);
    }

    private void GuardDesktopVisibility()
    {
        if (_allowClose || Note.IsHidden || Note.WindowMode != NoteWindowMode.Desktop) return;
        if (WindowState == WindowState.Minimized)
        {
            if (_manualMinimize) return;
            WindowState = WindowState.Normal;
            WindowNative.RestoreWithoutActivation(this);
        }
    }

    private async void StickyNoteWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        await _windowManager.HideNoteAsync(Note.Id);
    }

    private static SolidColorBrush BrushFrom(string value)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); }
        catch { return new SolidColorBrush(Color.FromRgb(255, 242, 168)); }
    }

    private static double Clamp(double value, double min, double max) => Math.Min(Math.Max(value, min), Math.Max(min, max));

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed) yield return typed;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
