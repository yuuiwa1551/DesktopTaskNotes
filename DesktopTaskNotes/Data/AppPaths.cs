namespace DesktopTaskNotes.Data;

public sealed class AppPaths
{
    public AppPaths()
        : this(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopTaskNotes"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "桌面事项贴备份"))
    {
    }

    public AppPaths(string dataDirectory, string backupDirectory)
    {
        DataDirectory = dataDirectory;
        BackupDirectory = backupDirectory;
        DatabasePath = Path.Combine(DataDirectory, "desktop-task-notes.db");
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupDirectory);
    }

    public string DataDirectory { get; }
    public string BackupDirectory { get; }
    public string DatabasePath { get; }
}
