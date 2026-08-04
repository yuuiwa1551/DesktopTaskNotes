using System.Drawing;
using System.Threading;
using System.Windows;
using DesktopTaskNotes.Data;
using DesktopTaskNotes.Models;
using DesktopTaskNotes.Services;
using Forms = System.Windows.Forms;
using Application = System.Windows.Application;

namespace DesktopTaskNotes;

public partial class App : Application
{
    private const string MutexName = "Local\\DesktopTaskNotes.Singleton.1";
    private const string ActivateEventName = "Local\\DesktopTaskNotes.Activate.1";
    private string _mutexName = MutexName;
    private string _activateEventName = ActivateEventName;
    private Mutex? _mutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _signalCancellation;
    private Forms.NotifyIcon? _trayIcon;
    private TrayMenuWindow? _trayMenuWindow;
    private ReminderService? _reminderService;
    private WindowManager? _windowManager;
    private AppPaths? _paths;
    private bool _exiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var isolatedTestRoot = Environment.GetEnvironmentVariable("DESKTOP_TASK_NOTES_TEST_ROOT");
        var isIsolatedTest = !string.IsNullOrWhiteSpace(isolatedTestRoot);
        if (isIsolatedTest)
        {
            var normalizedRoot = Path.GetFullPath(isolatedTestRoot!);
            var rootHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(normalizedRoot)))[..12];
            _mutexName = $"Local\\DesktopTaskNotes.IsolatedTest.{rootHash}";
            _activateEventName = $"Local\\DesktopTaskNotes.IsolatedTest.Activate.{rootHash}";
        }

        _mutex = new Mutex(true, _mutexName, out var createdNew);
        if (!createdNew)
        {
            try { EventWaitHandle.OpenExisting(_activateEventName).Set(); } catch { }
            Shutdown();
            return;
        }

        try
        {
            _paths = isIsolatedTest
                ? new AppPaths(isolatedTestRoot!, Path.Combine(isolatedTestRoot!, "backups"))
                : new AppPaths();
            var database = new DatabaseService(_paths.DatabasePath);
            await database.InitializeAsync();
            var backupService = new BackupService(_paths, database);
            _windowManager = new WindowManager(database, backupService, _paths);

            CreateTrayIcon();
            _reminderService = new ReminderService(database, _trayIcon!, _windowManager.OpenItemAsync);
            await _windowManager.LoadNotesAsync();

            var startupSetting = await database.GetSettingAsync("StartWithWindows");
            if (!isIsolatedTest && string.Equals(startupSetting, "true", StringComparison.OrdinalIgnoreCase) && !StartupService.IsEnabled())
            {
                try { StartupService.SetEnabled(true); } catch { }
            }

            try { await backupService.CreateAutomaticBackupIfDueAsync(); }
            catch (Exception backupError) { WriteErrorLog("自动备份失败", backupError); }

            StartActivationListener();
        }
        catch (Exception ex)
        {
            WriteErrorLog("启动失败", ex);
            MessageBox.Show($"桌面事项贴启动失败：\n{ex.Message}", "桌面事项贴", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApplication(false);
        }
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Information,
            Text = "桌面事项贴",
            Visible = true
        };
        if (_windowManager is not null) _trayMenuWindow = new TrayMenuWindow(_windowManager, this);
        _trayIcon.MouseUp += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Right)
                Dispatcher.Invoke(() => _trayMenuWindow?.ShowAtCursor());
        };
        _trayIcon.DoubleClick += (_, _) => _windowManager?.ShowManager();
    }

    private void StartActivationListener()
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _activateEventName);
        _signalCancellation = new CancellationTokenSource();
        var cancellation = _signalCancellation.Token;
        _ = Task.Run(() =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (!_activateEvent.WaitOne(500)) continue;
                Dispatcher.Invoke(() => _windowManager?.ShowManager());
            }
        }, cancellation);
    }

    public void ExitApplication(bool confirm, bool restart = false)
    {
        if (_exiting) return;
        if (confirm && MessageBox.Show(
                "退出后，便利贴和到期提醒都不会显示，直到下次启动。确定退出吗？",
                "退出桌面事项贴", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _exiting = true;
        _signalCancellation?.Cancel();
        _activateEvent?.Set();
        _reminderService?.Dispose();
        _windowManager?.CloseAll();
        _trayMenuWindow?.ForceClose();
        _trayMenuWindow = null;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _activateEvent?.Dispose();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        if (restart)
        {
            try { System.Diagnostics.Process.Start(StartupService.CreateLaunchInfo()); }
            catch (Exception restartError) { WriteErrorLog("恢复后重新启动失败", restartError); }
        }
        Shutdown();
    }

    private void WriteErrorLog(string context, Exception error)
    {
        try
        {
            var directory = _paths?.DataDirectory
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopTaskNotes");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "error.log"),
                $"[{DateTimeOffset.Now:O}] {context}\n{error}\n\n");
        }
        catch
        {
            // Logging must never hide the original error.
        }
    }
}
