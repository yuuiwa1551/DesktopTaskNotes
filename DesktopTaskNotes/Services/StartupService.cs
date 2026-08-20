using Microsoft.Win32;
using System.Diagnostics;
using System.Reflection;

namespace DesktopTaskNotes.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopTaskNotes";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(ValueName) is string command
            && string.Equals(command, GetLaunchCommand(), StringComparison.OrdinalIgnoreCase);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
        if (enabled)
        {
            key.SetValue(ValueName, GetLaunchCommand());
        }
        else
        {
            key.DeleteValue(ValueName, false);
        }
    }

    public static ProcessStartInfo CreateLaunchInfo()
    {
        var (executable, arguments) = GetLaunchTarget();
        return new ProcessStartInfo(executable)
        {
            Arguments = arguments ?? string.Empty,
            UseShellExecute = true
        };
    }

    private static string GetLaunchCommand()
    {
        var (executable, arguments) = GetLaunchTarget();
        return arguments is null ? $"\"{executable}\"" : $"\"{executable}\" {arguments}";
    }

    private static (string Executable, string? Arguments) GetLaunchTarget()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法确定应用程序路径。");
        if (!string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            return (executable, null);

        var entryAssembly = Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(entryAssembly))
            throw new InvalidOperationException("无法确定应用程序文件路径。");

        // Prefer the Windows GUI app host produced beside the managed DLL. Launching
        // the DLL through dotnet.exe also launches dotnet's console window.
        var appHost = Path.ChangeExtension(entryAssembly, ".exe");
        if (File.Exists(appHost))
            return (appHost, null);

        return (executable, $"\"{entryAssembly}\"");
    }
}
