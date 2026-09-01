using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTaskNotes.Data;
using DesktopTaskNotes.Dialogs;
using DesktopTaskNotes.Interop;
using DesktopTaskNotes.Models;
using DesktopTaskNotes.Services;
using Microsoft.Win32;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Forms = System.Windows.Forms;

namespace DesktopTaskNotes;

public partial class StickyNoteWindow : Window
{
    private readonly DatabaseService _database;
    private readonly WindowManager _windowManager;
    private readonly ObservableCollection<ChecklistItem> _items = [];
    private readonly DispatcherTimer _layoutSaveTimer;
    private readonly DispatcherTimer _desktopGuardTimer;
    private readonly DispatcherTimer _undoTimer;
    private readonly bool _exposeForUiTest;
    private Point _dragStart;
    private Guid? _dragItemId;
    private List<Guid> _undoItemIds = [];
    private UndoOperation _undoOperation;
    private string _undoStatus = string.Empty;
    private Guid? _reminderItemId;
    private bool _allowClose;
    private bool _loading;
    private bool _applyingCollapsedState;
    private bool _collapsedVisualApplied;
    private bool _manualMinimize;
    private bool _addingItem;
    private bool _displayEventsSubscribed;
    private bool _windowDragInProgress;
    private bool _restoringUnexpectedMaximize;
    private HwndSource? _windowSource;
    private Rect _preDragBounds;
    private Rect _lastNormalBounds;
    private double _expandedWidth;
    private double _expandedHeight;

    private enum UndoOperation
    {
        None,
        Archive,
        Delete
    }

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
        _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _undoTimer.Tick += (_, _) => DismissUndo();

        if (_exposeForUiTest) ShowInTaskbar = true;
        SourceInitialized += StickyNoteWindow_SourceInitialized;
        LocationChanged += (_, _) =>
        {
            RememberNormalBounds();
            ScheduleLayoutSave();
        };
        SizeChanged += Window_SizeChanged;
        StateChanged += Window_StateChanged;
        Loaded += async (_, _) =>
        {
            ClampToVisibleWorkArea();
            if (!_displayEventsSubscribed)
            {
                SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
                _displayEventsSubscribed = true;
            }
            _desktopGuardTimer.Start();
            await ReloadAsync();
        };
        DpiChanged += (_, _) => _ = Dispatcher.InvokeAsync(ClampToVisibleWorkArea, DispatcherPriority.Loaded);
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
            if (_undoOperation == UndoOperation.Archive && _undoItemIds.Count > 0)
            {
                _undoItemIds = await _database.FilterArchivedItemIdsAsync(_undoItemIds);
                if (_undoItemIds.Count == 0) DismissUndo();
            }
            ApplyNoteState();
            if (Note.Kind == NoteKind.Project)
            {
                var progress = await _database.GetProjectProgressAsync(Note.Id);
                UpdateProgress(progress.Completed, progress.Total);
            }
            else
            {
                UpdateProgress(0, 0);
            }
            StatusText.Text = _undoOperation == UndoOperation.None
                ? $"{_items.Count(i => !i.IsCompleted)} 项未完成"
                : _undoStatus;
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
        if (Note.IsCollapsed)
        {
            Note.IsCollapsed = false;
            ApplyCollapsedState();
            await SaveLayoutAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        }
        Activate();
        foreach (var element in FindVisualChildren<Border>(ItemsList))
        {
            if (element.Tag is Guid id && id == itemId)
            {
                element.Background = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
                element.BorderBrush = new SolidColorBrush(Color.FromRgb(91, 91, 214));
                element.BorderThickness = new Thickness(2);
                element.BringIntoView();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    element.ClearValue(Border.BackgroundProperty);
                    element.ClearValue(Border.BorderBrushProperty);
                    element.ClearValue(Border.BorderThicknessProperty);
                };
                timer.Start();
                break;
            }
        }
    }

    public void ForceClose()
    {
        _allowClose = true;
        _desktopGuardTimer.Stop();
        _undoTimer.Stop();
        if (_displayEventsSubscribed)
        {
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            _displayEventsSubscribed = false;
        }
        MorePopup.IsOpen = false;
        ReminderPopup.IsOpen = false;
        if (_windowSource is not null)
        {
            _windowSource.RemoveHook(WindowMessageHook);
            _windowSource = null;
        }
        Close();
    }

    private void StickyNoteWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (!_exposeForUiTest) WindowNative.ConfigureToolWindow(this);
        WindowNative.DisableMaximize(this);
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (WindowNative.IsMaximizeSystemCommand(message, wParam))
        {
            handled = true;
            WindowNative.DisableMaximize(this);
            return 0;
        }

        if (message == WindowNative.WmMoving &&
            WindowNative.SnapMovingRectangle(lParam, _windowManager.GetSnapTargets(Note.Id)))
        {
            handled = true;
            return 1;
        }

        return 0;
    }

    private void ApplyNoteState()
    {
        TitleText.Text = Note.Title;
        NoteBorder.Background = BrushFrom(Note.Color);
        Topmost = Note.WindowMode == NoteWindowMode.Topmost;
        PinButton.Opacity = Topmost ? 1 : 0.75;
        PinButton.ToolTip = Topmost ? "取消始终置顶" : "始终置顶";
        AutomationProperties.SetName(PinButton, Topmost ? "取消始终置顶" : "始终置顶");
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

    private void UpdateProgress(int completed, int total)
    {
        var progress = total == 0 ? 0 : completed * 100d / total;
        ProjectProgress.Value = progress;
        var due = string.Empty;
        ProjectProgressText.Foreground = new SolidColorBrush(Color.FromRgb(117, 108, 94));
        if (Note.ProjectDueAt is { } dueAt)
        {
            var localDue = dueAt.LocalDateTime;
            var overdue = localDue.Date < DateTime.Today && completed < total;
            due = overdue ? $" · 已逾期 {localDue:MM-dd}" : $" · {localDue:MM-dd}";
            if (overdue) ProjectProgressText.Foreground = new SolidColorBrush(Color.FromRgb(180, 35, 24));
        }
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
            if (_windowSource is not null) WindowNative.DisableMaximize(this);
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
        PinButton.Opacity = Topmost ? 1 : 0.75;
        PinButton.ToolTip = Topmost ? "取消始终置顶" : "始终置顶";
        AutomationProperties.SetName(PinButton, Topmost ? "取消始终置顶" : "始终置顶");
        await _database.UpdateNoteMetadataAsync(Note);
        _windowManager.RefreshManager();
    }

    private async void CollapseButton_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        ReminderPopup.IsOpen = false;
        Note.IsCollapsed = !Note.IsCollapsed;
        ApplyCollapsedState();
        await SaveLayoutAsync();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        ReminderPopup.IsOpen = false;
        _manualMinimize = true;
        ShowInTaskbar = true;
        WindowNative.ConfigureTaskbarWindow(this);
        WindowState = WindowState.Minimized;
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        ReminderPopup.IsOpen = false;
        MorePopup.Width = Math.Max(250, ActualWidth - 20);
        MorePopup.HorizontalOffset = -(MorePopup.Width - MoreButton.ActualWidth);
        MorePopup.IsOpen = !MorePopup.IsOpen;
        if (MorePopup.IsOpen) UpdateColorSelection();
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
        MorePopup.IsOpen = false;
        await EditNoteAsync();
    }

    private async void HideNoteMenu_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        await _windowManager.HideNoteAsync(Note.Id);
    }

    private async void DeleteNoteMenu_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = false;
        await _windowManager.DeleteNoteAsync(Note.Id, this);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || (!MorePopup.IsOpen && !ReminderPopup.IsOpen)) return;
        MorePopup.IsOpen = false;
        ReminderPopup.IsOpen = false;
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
        var dialog = new NoteEditorDialog(_database, Note) { Owner = this };
        await dialog.RestoreWindowSizeAsync();
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
        if (_addingItem || string.IsNullOrWhiteSpace(NewItemBox.Text)) return;
        _addingItem = true;
        var text = NewItemBox.Text;
        NewItemBox.IsEnabled = false;
        AddItemButton.IsEnabled = false;
        try
        {
            await _database.AddItemAsync(Note.Id, text);
            NewItemBox.Clear();
            await ReloadAsync();
            ItemsScroller.ScrollToEnd();
            _windowManager.RefreshManager();
        }
        finally
        {
            _addingItem = false;
            NewItemBox.IsEnabled = true;
            AddItemButton.IsEnabled = true;
            NewItemBox.Focus();
        }
    }

    private async void TaskCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox { DataContext: ChecklistItem item } checkBox) return;
        checkBox.IsEnabled = false;
        try
        {
            await _database.SetItemCompletedAsync(item.Id, checkBox.IsChecked == true);
            await ReloadAsync();
            _windowManager.RefreshManager();
        }
        finally
        {
            checkBox.IsEnabled = true;
        }
    }

    private async void TaskText_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_loading || sender is not TextBox { DataContext: ChecklistItem item } box) return;
        await SaveTaskTextAsync(box, item);
    }

    private async void TaskText_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: ChecklistItem item } box) return;
        if (e.Key == Key.Escape)
        {
            box.Text = item.Text;
            Keyboard.ClearFocus();
            e.Handled = true;
            return;
        }
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        await SaveTaskTextAsync(box, item);
        Keyboard.ClearFocus();
    }

    private async Task SaveTaskTextAsync(TextBox box, ChecklistItem item)
    {
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

    private async void DeleteItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChecklistItem item }) return;
        await _database.DeleteItemAsync(item.Id);
        await ReloadAsync();
        ShowUndo(UndoOperation.Delete, [item.Id], "已删除 1 项");
        _windowManager.RefreshManager();
    }

    private void ReminderButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChecklistItem item } button) return;
        MorePopup.IsOpen = false;
        _reminderItemId = item.Id;
        ReminderPopup.PlacementTarget = button;
        ReminderPopup.HorizontalOffset = -(300 - button.ActualWidth);
        ReminderItemText.Text = item.Text;
        var initial = item.ReminderAt?.LocalDateTime
                      ?? (DateTime.Now.Hour < 18 ? DateTime.Today.AddHours(18) : DateTime.Today.AddDays(1).AddHours(9));
        ReminderDatePicker.SelectedDate = initial.Date;
        ReminderTimeBox.Text = initial.ToString("HH:mm", CultureInfo.InvariantCulture);
        ReminderClearButton.Visibility = item.ReminderAt is null ? Visibility.Collapsed : Visibility.Visible;
        ReminderErrorText.Visibility = Visibility.Collapsed;
        ReminderPopup.IsOpen = true;
        ReminderDatePicker.Focus();
    }

    private void QuickReminder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        if (tag.StartsWith("relative-", StringComparison.Ordinal))
        {
            var durationText = tag["relative-".Length..];
            if (TimeSpan.TryParseExact(durationText, "hh\\:mm", CultureInfo.InvariantCulture, out var duration))
            {
                var future = DateTime.Now.Add(duration);
                ReminderDatePicker.SelectedDate = future.Date;
                ReminderTimeBox.Text = future.ToString("HH:mm", CultureInfo.InvariantCulture);
                ReminderErrorText.Visibility = Visibility.Collapsed;
            }
            return;
        }
        var tomorrow = tag.StartsWith("tomorrow", StringComparison.Ordinal);
        var timeText = tag[(tag.IndexOf('-') + 1)..];
        ReminderDatePicker.SelectedDate = DateTime.Today.AddDays(tomorrow ? 1 : 0);
        ReminderTimeBox.Text = timeText;
        ReminderErrorText.Visibility = Visibility.Collapsed;
    }

    private async void ReminderSave_Click(object sender, RoutedEventArgs e)
    {
        if (_reminderItemId is not Guid itemId || ReminderDatePicker.SelectedDate is not DateTime date)
        {
            ShowReminderError("请选择提醒日期。");
            return;
        }
        if (!TimeSpan.TryParseExact(ReminderTimeBox.Text.Trim(), "hh\\:mm", CultureInfo.InvariantCulture,
                out var time))
        {
            ShowReminderError("时间请按 09:00 这样的格式填写。");
            return;
        }
        var local = date.Date.Add(time);
        var reminderAt = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        if (reminderAt <= DateTimeOffset.Now)
        {
            ShowReminderError("提醒时间需要晚于现在。");
            return;
        }
        var item = _items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null) return;
        item.ReminderAt = reminderAt;
        await _database.UpdateItemAsync(item);
        ReminderPopup.IsOpen = false;
        await ReloadAsync();
        _windowManager.RefreshManager();
    }

    private async void ReminderClear_Click(object sender, RoutedEventArgs e)
    {
        if (_reminderItemId is not Guid itemId) return;
        var item = _items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null) return;
        item.ReminderAt = null;
        await _database.UpdateItemAsync(item);
        ReminderPopup.IsOpen = false;
        await ReloadAsync();
        _windowManager.RefreshManager();
    }

    private void ReminderCancel_Click(object sender, RoutedEventArgs e) => ReminderPopup.IsOpen = false;

    private void ShowReminderError(string message)
    {
        ReminderErrorText.Text = message;
        ReminderErrorText.Visibility = Visibility.Visible;
    }

    private async void ClearCompleted_Click(object sender, RoutedEventArgs e)
    {
        var archived = await _database.ArchiveCompletedAsync(Note.Id);
        if (archived.Count == 0)
        {
            StatusText.Text = "没有已完成事项";
            return;
        }
        await ReloadAsync();
        ShowUndo(UndoOperation.Archive, archived, $"已清理 {archived.Count} 项");
        _windowManager.RefreshManager();
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undoOperation == UndoOperation.None || _undoItemIds.Count == 0) return;
        _undoTimer.Stop();
        UndoButton.IsEnabled = false;
        try
        {
            if (_undoOperation == UndoOperation.Archive)
                await _database.UndoArchiveAsync(_undoItemIds);
            else if (_undoOperation == UndoOperation.Delete)
                foreach (var id in _undoItemIds) await _database.RestoreDeletedItemAsync(id);
            _undoOperation = UndoOperation.None;
            _undoItemIds.Clear();
            _undoStatus = string.Empty;
            UndoButton.Visibility = Visibility.Collapsed;
            await ReloadAsync();
            _windowManager.RefreshManager();
        }
        finally
        {
            UndoButton.IsEnabled = true;
        }
    }

    private void ShowUndo(UndoOperation operation, IEnumerable<Guid> itemIds, string status)
    {
        _undoTimer.Stop();
        _undoOperation = operation;
        _undoItemIds = itemIds.ToList();
        _undoStatus = status;
        StatusText.Text = status;
        CollapsedStatusText.Text = status;
        UndoButton.Visibility = Visibility.Visible;
        _undoTimer.Start();
    }

    private void DismissUndo()
    {
        _undoTimer.Stop();
        _undoOperation = UndoOperation.None;
        _undoItemIds.Clear();
        _undoStatus = string.Empty;
        UndoButton.Visibility = Visibility.Collapsed;
        var normalStatus = $"{_items.Count(i => !i.IsCompleted)} 项未完成";
        StatusText.Text = normalStatus;
        CollapsedStatusText.Text = normalStatus;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source) return;
        if (FindVisualParent<Button>(source) is not null || FindVisualParent<TextBox>(source) is not null) return;
        _preDragBounds = CurrentNormalBounds();
        _windowDragInProgress = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The mouse button may already have been released before WPF enters its native move loop.
        }
        finally
        {
            _windowDragInProgress = false;
            WindowNative.DisableMaximize(this);
            if (WindowState == WindowState.Maximized)
                RestoreFromUnexpectedMaximize();
            else
                WindowNative.SnapWindow(this, _windowManager.GetSnapTargets(Note.Id));
            RememberNormalBounds();
            ScheduleLayoutSave();
        }
    }

    private void Header_MouseEnter(object sender, MouseEventArgs e) => HeaderButtons.Opacity = 1;
    private void Header_MouseLeave(object sender, MouseEventArgs e) => HeaderButtons.Opacity = 0.72;

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

    private void TaskRow_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(Guid)) || sender is not Border row ||
            row.DataContext is not ChecklistItem target || (Guid)e.Data.GetData(typeof(Guid)) == target.Id)
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        var insertAfter = e.GetPosition(row).Y >= row.ActualHeight / 2;
        row.BorderBrush = new SolidColorBrush(Color.FromRgb(91, 91, 214));
        row.BorderThickness = insertAfter ? new Thickness(1, 1, 1, 3) : new Thickness(1, 3, 1, 1);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void TaskRow_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is not Border row) return;
        row.ClearValue(Border.BorderBrushProperty);
        row.ClearValue(Border.BorderThicknessProperty);
    }

    private async void TaskRow_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(Guid)) || sender is not Border { DataContext: ChecklistItem target } row) return;
        var sourceId = (Guid)e.Data.GetData(typeof(Guid));
        var insertAfter = e.GetPosition(row).Y >= row.ActualHeight / 2;
        row.ClearValue(Border.BorderBrushProperty);
        row.ClearValue(Border.BorderThicknessProperty);
        _dragItemId = null;
        await _database.ReorderItemAsync(sourceId, target.Id, insertAfter);
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
        RememberNormalBounds();
        ScheduleLayoutSave();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (_restoringUnexpectedMaximize) return;
        if (WindowState == WindowState.Maximized)
        {
            RestoreFromUnexpectedMaximize();
            return;
        }
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
        _windowManager.RefreshManager();
    }

    private void RestoreFromUnexpectedMaximize()
    {
        if (_restoringUnexpectedMaximize || WindowState == WindowState.Minimized) return;
        _restoringUnexpectedMaximize = true;
        try
        {
            var bounds = _windowDragInProgress && IsUsableBounds(_preDragBounds)
                ? _preDragBounds
                : IsUsableBounds(_lastNormalBounds) ? _lastNormalBounds : RestoreBounds;
            WindowState = WindowState.Normal;
            if (IsUsableBounds(bounds))
            {
                Left = bounds.Left;
                Top = bounds.Top;
                Width = Math.Max(MinWidth, bounds.Width);
                Height = Note.IsCollapsed ? 96 : Math.Max(MinHeight, bounds.Height);
            }
            WindowNative.DisableMaximize(this);
            ClampToVisibleWorkArea();
            WindowNative.SnapWindow(this, _windowManager.GetSnapTargets(Note.Id));
            RememberNormalBounds();
            ScheduleLayoutSave();
        }
        finally
        {
            _restoringUnexpectedMaximize = false;
        }
    }

    private void RememberNormalBounds()
    {
        if (!IsLoaded || WindowState != WindowState.Normal || _restoringUnexpectedMaximize) return;
        var bounds = CurrentNormalBounds();
        if (IsUsableBounds(bounds)) _lastNormalBounds = bounds;
    }

    private Rect CurrentNormalBounds() =>
        new(Left, Top, Math.Max(ActualWidth, Width), Math.Max(ActualHeight, Height));

    private static bool IsUsableBounds(Rect bounds) =>
        !bounds.IsEmpty && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top) &&
        double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) &&
        bounds.Width > 0 && bounds.Height > 0;

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

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e) =>
        _ = Dispatcher.InvokeAsync(ClampToVisibleWorkArea, DispatcherPriority.Loaded);

    private void ClampToVisibleWorkArea()
    {
        if (!IsLoaded || WindowState != WindowState.Normal) return;
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var screen = Forms.Screen.FromHandle(handle);
        var dpi = VisualTreeHelper.GetDpi(this);
        var workArea = screen.WorkingArea;
        var left = workArea.Left / dpi.DpiScaleX;
        var top = workArea.Top / dpi.DpiScaleY;
        var width = workArea.Width / dpi.DpiScaleX;
        var height = workArea.Height / dpi.DpiScaleY;
        const double margin = 0;

        Width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, width - margin * 2));
        if (!Note.IsCollapsed)
            Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, height - margin * 2));
        Left = Clamp(Left, left + margin, left + width - Width - margin);
        Top = Clamp(Top, top + margin, top + height - Height - margin);
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
