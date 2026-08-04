using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopTaskNotes.Data;
using DesktopTaskNotes.Models;

namespace DesktopTaskNotes.Services;

public sealed class BackupService
{
    private readonly AppPaths _paths;
    private readonly DatabaseService _database;

    public BackupService(AppPaths paths, DatabaseService database)
    {
        _paths = paths;
        _database = database;
    }

    public async Task CreateAutomaticBackupIfDueAsync()
    {
        var todayPrefix = $"daily-{DateTime.Now:yyyy-MM-dd}";
        if (!Directory.EnumerateFiles(_paths.BackupDirectory, $"{todayPrefix}*.dtnbackup").Any())
        {
            await CreateBackupAsync(Path.Combine(_paths.BackupDirectory, $"{todayPrefix}.dtnbackup"));
        }

        var calendar = System.Globalization.CultureInfo.InvariantCulture.Calendar;
        var week = calendar.GetWeekOfYear(DateTime.Today, System.Globalization.CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
        var weeklyPrefix = $"weekly-{DateTime.Today.Year}-W{week:00}";
        if (!Directory.EnumerateFiles(_paths.BackupDirectory, $"{weeklyPrefix}*.dtnbackup").Any())
        {
            await CreateBackupAsync(Path.Combine(_paths.BackupDirectory, $"{weeklyPrefix}.dtnbackup"));
        }

        Prune("daily-*.dtnbackup", 7);
        Prune("weekly-*.dtnbackup", 4);
    }

    public async Task CreateBackupAsync(string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"desktop-task-notes-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var databaseCopy = Path.Combine(tempDirectory, "desktop-task-notes.db");
            await _database.BackupDatabaseAsync(databaseCopy);
            string hash;
            await using (var databaseStream = File.OpenRead(databaseCopy))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(databaseStream));
            var manifest = new BackupManifest
            {
                CreatedAt = DateTimeOffset.Now,
                Sha256 = hash
            };
            await File.WriteAllTextAsync(
                Path.Combine(tempDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            var temporaryArchive = destinationPath + ".tmp";
            if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
            ZipFile.CreateFromDirectory(tempDirectory, temporaryArchive, CompressionLevel.Optimal, false);
            File.Move(temporaryArchive, destinationPath, true);
        }
        finally
        {
            if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, true);
        }
    }

    public async Task RestoreBackupAsync(string backupPath)
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"desktop-task-notes-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            ZipFile.ExtractToDirectory(backupPath, tempDirectory);
            var manifestPath = Path.Combine(tempDirectory, "manifest.json");
            if (!File.Exists(manifestPath)) throw new InvalidDataException("备份缺少 manifest.json。");
            var manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(manifestPath))
                ?? throw new InvalidDataException("备份清单无法读取。");
            if (manifest.FormatVersion != 1) throw new InvalidDataException("备份格式版本不受支持。");

            var restoredDatabase = Path.Combine(tempDirectory, manifest.DatabaseEntry);
            if (!File.Exists(restoredDatabase)) throw new InvalidDataException("备份缺少数据库文件。");
            string hash;
            await using (var restoredStream = File.OpenRead(restoredDatabase))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(restoredStream));
            if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份校验失败，文件可能已经损坏。");
            await DatabaseService.ValidateDatabaseAsync(restoredDatabase);

            var preRestore = Path.Combine(_paths.BackupDirectory, $"pre-restore-{DateTime.Now:yyyy-MM-dd-HHmmss}.dtnbackup");
            await CreateBackupAsync(preRestore);
            File.Copy(restoredDatabase, _paths.DatabasePath, true);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                var sidecar = _paths.DatabasePath + suffix;
                if (File.Exists(sidecar)) File.Delete(sidecar);
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, true);
        }
    }

    private void Prune(string pattern, int keep)
    {
        foreach (var file in new DirectoryInfo(_paths.BackupDirectory)
                     .GetFiles(pattern)
                     .OrderByDescending(f => f.LastWriteTimeUtc)
                     .Skip(keep))
        {
            file.Delete();
        }
    }
}
