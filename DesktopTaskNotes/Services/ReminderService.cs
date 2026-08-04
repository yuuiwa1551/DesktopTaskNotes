using DesktopTaskNotes.Data;
using DesktopTaskNotes.Models;
using Forms = System.Windows.Forms;

namespace DesktopTaskNotes.Services;

public sealed class ReminderService : IDisposable
{
    private readonly DatabaseService _database;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Func<Guid, Guid, Task> _openItem;
    private readonly System.Threading.Timer _timer;
    private Reminder? _current;
    private int _checking;

    public ReminderService(DatabaseService database, Forms.NotifyIcon trayIcon, Func<Guid, Guid, Task> openItem)
    {
        _database = database;
        _trayIcon = trayIcon;
        _openItem = openItem;
        _trayIcon.BalloonTipClicked += OnBalloonTipClicked;
        _timer = new System.Threading.Timer(_ => _ = CheckAsync(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(30));
    }

    private async Task CheckAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        try
        {
            var reminders = await _database.GetDueRemindersAsync(DateTimeOffset.Now, 1);
            var reminder = reminders.FirstOrDefault();
            if (reminder is null) return;
            await _database.MarkReminderNotifiedAsync(reminder.ItemId, DateTimeOffset.Now);
            _current = reminder;
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _trayIcon.BalloonTipTitle = $"到期提醒 · {reminder.NoteTitle}";
                _trayIcon.BalloonTipText = reminder.ItemText;
                _trayIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
                _trayIcon.ShowBalloonTip(8000);
            });
        }
        catch
        {
            // Reminders retry on the next timer tick. A transient database error must not terminate the app.
        }
        finally
        {
            Interlocked.Exchange(ref _checking, 0);
        }
    }

    private async void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        if (_current is { } reminder) await _openItem(reminder.NoteId, reminder.ItemId);
    }

    public void Dispose()
    {
        _timer.Dispose();
        _trayIcon.BalloonTipClicked -= OnBalloonTipClicked;
    }
}
