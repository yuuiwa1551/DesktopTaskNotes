using DesktopTaskNotes.Models;
using Microsoft.Data.Sqlite;

namespace DesktopTaskNotes.Data;

public sealed class DatabaseService
{
    private readonly string _databasePath;
    private readonly string _connectionString;

    public DatabaseService(string databasePath)
    {
        _databasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync()
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;

            CREATE TABLE IF NOT EXISTS Notes (
                Id TEXT PRIMARY KEY,
                Kind INTEGER NOT NULL,
                Title TEXT NOT NULL,
                Color TEXT NOT NULL,
                WindowMode INTEGER NOT NULL,
                LeftPosition REAL NOT NULL,
                TopPosition REAL NOT NULL,
                Width REAL NOT NULL,
                Height REAL NOT NULL,
                IsCollapsed INTEGER NOT NULL,
                IsHidden INTEGER NOT NULL,
                ProjectDueAt TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                DeletedAt TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS Items (
                Id TEXT PRIMARY KEY,
                NoteId TEXT NOT NULL,
                SortOrder INTEGER NOT NULL,
                Text TEXT NOT NULL,
                Details TEXT NOT NULL,
                ReminderAt TEXT NULL,
                ReminderNotifiedAt TEXT NULL,
                IsCompleted INTEGER NOT NULL,
                CompletedAt TEXT NULL,
                ArchivedAt TEXT NULL,
                DeletedAt TEXT NULL,
                CreatedAt TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL,
                FOREIGN KEY(NoteId) REFERENCES Notes(Id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS Settings (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_Items_Note_Sort ON Items(NoteId, SortOrder);
            CREATE INDEX IF NOT EXISTS IX_Items_Reminder ON Items(ReminderAt, ReminderNotifiedAt);
            CREATE INDEX IF NOT EXISTS IX_Items_Archive ON Items(ArchivedAt);
            PRAGMA user_version=1;
            """;
        await command.ExecuteNonQueryAsync();

        if (await GetSettingAsync("FirstRunComplete") is null)
        {
            var welcome = await CreateNoteAsync(NoteKind.Checklist, "欢迎使用桌面事项贴", "#FFF2A8");
            await AddItemAsync(welcome.Id, "点击左侧方框即可完成事项");
            await AddItemAsync(welcome.Id, "按 Enter 可以继续添加待办");
            await AddItemAsync(welcome.Id, "右上角图钉可以切换始终置顶");
            await SetSettingAsync("FirstRunComplete", "true");
            await SetSettingAsync("StartWithWindows", "true");
        }

        await PurgeDeletedNotesAsync(DateTimeOffset.Now.AddDays(-30));
    }

    public async Task<StickyNote> CreateNoteAsync(NoteKind kind, string title, string color)
    {
        var count = (await GetActiveNotesAsync()).Count;
        var offset = (count % 8) * 28;
        var now = DateTimeOffset.Now;
        var note = new StickyNote
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Title = string.IsNullOrWhiteSpace(title) ? (kind == NoteKind.Project ? "新项目" : "新清单") : title.Trim(),
            Color = color,
            WindowMode = NoteWindowMode.Desktop,
            Left = 120 + offset,
            Top = 100 + offset,
            Width = 340,
            Height = 430,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Notes (Id, Kind, Title, Color, WindowMode, LeftPosition, TopPosition, Width, Height,
                IsCollapsed, IsHidden, ProjectDueAt, CreatedAt, UpdatedAt, DeletedAt)
            VALUES ($id, $kind, $title, $color, $mode, $left, $top, $width, $height,
                0, 0, NULL, $created, $updated, NULL);
            """;
        AddNoteParameters(command, note);
        await command.ExecuteNonQueryAsync();
        return note;
    }

    public async Task<List<StickyNote>> GetActiveNotesAsync()
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Notes WHERE DeletedAt IS NULL ORDER BY CreatedAt;";
        return await ReadNotesAsync(command);
    }

    public async Task<StickyNote?> GetNoteAsync(Guid id)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Notes WHERE Id=$id LIMIT 1;";
        command.Parameters.AddWithValue("$id", id.ToString());
        var notes = await ReadNotesAsync(command);
        return notes.FirstOrDefault();
    }

    public async Task<List<NoteSummary>> GetNoteSummariesAsync(bool deletedOnly = false, string search = "")
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT n.Id, n.Kind, n.Title, n.IsHidden, n.WindowMode, n.ProjectDueAt, n.DeletedAt,
                   SUM(CASE WHEN i.ArchivedAt IS NULL AND i.DeletedAt IS NULL AND i.IsCompleted=0 THEN 1 ELSE 0 END) ActiveCount,
                   SUM(CASE WHEN i.ArchivedAt IS NULL AND i.DeletedAt IS NULL AND i.IsCompleted=1 THEN 1 ELSE 0 END) CompletedCount
            FROM Notes n
            LEFT JOIN Items i ON i.NoteId=n.Id
            WHERE n.DeletedAt IS {(deletedOnly ? "NOT" : string.Empty)} NULL
              AND ($search='' OR n.Title LIKE $like OR EXISTS (
                    SELECT 1 FROM Items matching
                    WHERE matching.NoteId=n.Id AND matching.DeletedAt IS NULL
                      AND (matching.Text LIKE $like OR matching.Details LIKE $like)
                  ))
            GROUP BY n.Id
            ORDER BY n.UpdatedAt DESC;
            """;
        command.Parameters.AddWithValue("$search", search.Trim());
        command.Parameters.AddWithValue("$like", $"%{search.Trim()}%");
        var result = new List<NoteSummary>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new NoteSummary
            {
                Id = Guid.Parse(reader.GetString(0)),
                Kind = (NoteKind)reader.GetInt32(1),
                Title = reader.GetString(2),
                IsHidden = reader.GetInt32(3) != 0,
                WindowMode = (NoteWindowMode)reader.GetInt32(4),
                ProjectDueAt = ParseNullableDate(reader, 5),
                DeletedAt = ParseNullableDate(reader, 6),
                ActiveCount = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                CompletedCount = reader.IsDBNull(8) ? 0 : reader.GetInt32(8)
            });
        }
        return result;
    }

    public async Task<(int Completed, int Total)> GetProjectProgressAsync(Guid noteId)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN IsCompleted=1 THEN 1 ELSE 0 END), 0), COUNT(*)
            FROM Items
            WHERE NoteId=$note AND DeletedAt IS NULL;
            """;
        command.Parameters.AddWithValue("$note", noteId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return (0, 0);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }

    public async Task UpdateNoteMetadataAsync(StickyNote note)
    {
        note.UpdatedAt = DateTimeOffset.Now;
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Notes SET Kind=$kind, Title=$title, Color=$color, WindowMode=$mode,
                IsCollapsed=$collapsed, IsHidden=$hidden, ProjectDueAt=$due, UpdatedAt=$updated
            WHERE Id=$id;
            """;
        AddNoteParameters(command, note);
        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateNoteLayoutAsync(Guid id, double left, double top, double width, double height, bool collapsed)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Notes SET LeftPosition=$left, TopPosition=$top, Width=$width, Height=$height,
                IsCollapsed=$collapsed, UpdatedAt=$updated WHERE Id=$id;
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$left", left);
        command.Parameters.AddWithValue("$top", top);
        command.Parameters.AddWithValue("$width", width);
        command.Parameters.AddWithValue("$height", height);
        command.Parameters.AddWithValue("$collapsed", collapsed ? 1 : 0);
        command.Parameters.AddWithValue("$updated", ToDb(DateTimeOffset.Now));
        await command.ExecuteNonQueryAsync();
    }

    public async Task SetNoteHiddenAsync(Guid id, bool hidden)
    {
        await ExecuteAsync("UPDATE Notes SET IsHidden=$value, UpdatedAt=$now WHERE Id=$id;", ("$value", hidden ? 1 : 0), ("$now", ToDb(DateTimeOffset.Now)), ("$id", id.ToString()));
    }

    public async Task SoftDeleteNoteAsync(Guid id)
    {
        await ExecuteAsync("UPDATE Notes SET DeletedAt=$now, IsHidden=1, UpdatedAt=$now WHERE Id=$id;", ("$now", ToDb(DateTimeOffset.Now)), ("$id", id.ToString()));
    }

    public async Task RestoreNoteAsync(Guid id)
    {
        await ExecuteAsync("UPDATE Notes SET DeletedAt=NULL, IsHidden=0, UpdatedAt=$now WHERE Id=$id;", ("$now", ToDb(DateTimeOffset.Now)), ("$id", id.ToString()));
    }

    public async Task PermanentlyDeleteNoteAsync(Guid id)
    {
        await ExecuteAsync("DELETE FROM Notes WHERE Id=$id;", ("$id", id.ToString()));
    }

    public async Task PurgeDeletedNotesAsync(DateTimeOffset cutoff)
    {
        await ExecuteAsync("DELETE FROM Notes WHERE DeletedAt IS NOT NULL AND DeletedAt < $cutoff;", ("$cutoff", ToDb(cutoff)));
    }

    public async Task<List<ChecklistItem>> GetItemsForNoteAsync(Guid noteId)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.*, n.Title FROM Items i JOIN Notes n ON n.Id=i.NoteId
            WHERE i.NoteId=$note AND i.ArchivedAt IS NULL AND i.DeletedAt IS NULL
            ORDER BY i.SortOrder, i.CreatedAt;
            """;
        command.Parameters.AddWithValue("$note", noteId.ToString());
        return await ReadItemsAsync(command);
    }

    public async Task<ChecklistItem> AddItemAsync(Guid noteId, string text)
    {
        var existing = await GetItemsForNoteAsync(noteId);
        var now = DateTimeOffset.Now;
        var item = new ChecklistItem
        {
            Id = Guid.NewGuid(),
            NoteId = noteId,
            SortOrder = existing.Count == 0 ? 10 : existing.Max(i => i.SortOrder) + 10,
            Text = text.Trim(),
            CreatedAt = now,
            UpdatedAt = now
        };
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Items (Id, NoteId, SortOrder, Text, Details, ReminderAt, ReminderNotifiedAt,
                IsCompleted, CompletedAt, ArchivedAt, DeletedAt, CreatedAt, UpdatedAt)
            VALUES ($id, $note, $sort, $text, '', NULL, NULL, 0, NULL, NULL, NULL, $created, $updated);
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString());
        command.Parameters.AddWithValue("$note", noteId.ToString());
        command.Parameters.AddWithValue("$sort", item.SortOrder);
        command.Parameters.AddWithValue("$text", item.Text);
        command.Parameters.AddWithValue("$created", ToDb(now));
        command.Parameters.AddWithValue("$updated", ToDb(now));
        await command.ExecuteNonQueryAsync();
        return item;
    }

    public async Task UpdateItemAsync(ChecklistItem item)
    {
        item.UpdatedAt = DateTimeOffset.Now;
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Items SET Text=$text, Details=$details, ReminderAt=$reminder,
                ReminderNotifiedAt=NULL, UpdatedAt=$updated WHERE Id=$id;
            """;
        command.Parameters.AddWithValue("$id", item.Id.ToString());
        command.Parameters.AddWithValue("$text", item.Text.Trim());
        command.Parameters.AddWithValue("$details", item.Details.Trim());
        command.Parameters.AddWithValue("$reminder", (object?)ToDb(item.ReminderAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$updated", ToDb(item.UpdatedAt));
        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteItemAsync(Guid id)
    {
        var now = DateTimeOffset.Now;
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE Items SET DeletedAt=$now, UpdatedAt=$now WHERE Id=$id;";
        command.Parameters.AddWithValue("$now", ToDb(now));
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync();
    }

    public async Task RestoreDeletedItemAsync(Guid id)
    {
        await ExecuteAsync("UPDATE Items SET DeletedAt=NULL, UpdatedAt=$now WHERE Id=$id;",
            ("$now", ToDb(DateTimeOffset.Now)), ("$id", id.ToString()));
    }

    public async Task SetItemCompletedAsync(Guid id, bool completed)
    {
        var now = DateTimeOffset.Now;
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Items SET IsCompleted=$completed, CompletedAt=$completedAt,
                ReminderAt=CASE WHEN $completed=1 THEN NULL ELSE ReminderAt END,
                ReminderNotifiedAt=NULL,
                UpdatedAt=$now WHERE Id=$id;
            """;
        command.Parameters.AddWithValue("$completed", completed ? 1 : 0);
        command.Parameters.AddWithValue("$completedAt", completed ? ToDb(now) : DBNull.Value);
        command.Parameters.AddWithValue("$now", ToDb(now));
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<Guid>> ArchiveCompletedAsync(Guid noteId)
    {
        var items = await GetItemsForNoteAsync(noteId);
        var ids = items.Where(i => i.IsCompleted).Select(i => i.Id).ToList();
        if (ids.Count == 0) return ids;

        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var id in ids)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "UPDATE Items SET ArchivedAt=$now, UpdatedAt=$now WHERE Id=$id;";
            command.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.Now));
            command.Parameters.AddWithValue("$id", id.ToString());
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        return ids;
    }

    public async Task UndoArchiveAsync(IEnumerable<Guid> itemIds)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        foreach (var id in itemIds)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "UPDATE Items SET ArchivedAt=NULL, UpdatedAt=$now WHERE Id=$id;";
            command.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.Now));
            command.Parameters.AddWithValue("$id", id.ToString());
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<List<Guid>> FilterArchivedItemIdsAsync(IEnumerable<Guid> itemIds)
    {
        var result = new List<Guid>();
        await using var connection = await OpenAsync();
        foreach (var id in itemIds.Distinct())
        {
            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM Items WHERE Id=$id AND ArchivedAt IS NOT NULL AND DeletedAt IS NULL;";
            command.Parameters.AddWithValue("$id", id.ToString());
            if (Convert.ToInt32(await command.ExecuteScalarAsync()) > 0) result.Add(id);
        }
        return result;
    }

    public async Task ReorderItemAsync(Guid sourceId, Guid targetId, bool insertAfter = false)
    {
        if (sourceId == targetId) return;
        await using var connection = await OpenAsync();

        var noteLookup = connection.CreateCommand();
        noteLookup.CommandText = "SELECT NoteId FROM Items WHERE Id=$id AND ArchivedAt IS NULL AND DeletedAt IS NULL;";
        noteLookup.Parameters.AddWithValue("$id", sourceId.ToString());
        var noteValue = await noteLookup.ExecuteScalarAsync();
        if (noteValue is not string noteId) return;

        var orderedLookup = connection.CreateCommand();
        orderedLookup.CommandText = """
            SELECT Id FROM Items
            WHERE NoteId=$note AND ArchivedAt IS NULL AND DeletedAt IS NULL
            ORDER BY SortOrder, CreatedAt;
            """;
        orderedLookup.Parameters.AddWithValue("$note", noteId);
        var ordered = new List<Guid>();
        await using (var reader = await orderedLookup.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) ordered.Add(Guid.Parse(reader.GetString(0)));
        }

        if (!ordered.Remove(sourceId)) return;
        var targetIndex = ordered.IndexOf(targetId);
        if (targetIndex < 0) return;
        if (insertAfter) targetIndex++;
        ordered.Insert(targetIndex, sourceId);

        await using var transaction = await connection.BeginTransactionAsync();
        for (var index = 0; index < ordered.Count; index++)
        {
            await UpdateSortAsync(connection, transaction, ordered[index], (index + 1) * 10);
        }
        await transaction.CommitAsync();
    }

    public async Task<List<ChecklistItem>> SearchActiveItemsAsync(string search)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.*, n.Title FROM Items i JOIN Notes n ON n.Id=i.NoteId
            WHERE n.DeletedAt IS NULL AND i.DeletedAt IS NULL AND i.ArchivedAt IS NULL
              AND ($search='' OR n.Title LIKE $like OR i.Text LIKE $like OR i.Details LIKE $like)
            ORDER BY i.IsCompleted, i.ReminderAt IS NULL, i.ReminderAt, i.UpdatedAt DESC;
            """;
        command.Parameters.AddWithValue("$search", search.Trim());
        command.Parameters.AddWithValue("$like", $"%{search.Trim()}%");
        return await ReadItemsAsync(command);
    }

    public async Task<List<ArchiveEntry>> GetArchiveAsync(string search = "")
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.Id, i.NoteId, n.Title, i.Text, i.Details, i.ArchivedAt, i.CompletedAt
            FROM Items i LEFT JOIN Notes n ON n.Id=i.NoteId
            WHERE i.ArchivedAt IS NOT NULL AND i.DeletedAt IS NULL
              AND ($search='' OR n.Title LIKE $like OR i.Text LIKE $like OR i.Details LIKE $like)
            ORDER BY i.ArchivedAt DESC;
            """;
        command.Parameters.AddWithValue("$search", search.Trim());
        command.Parameters.AddWithValue("$like", $"%{search.Trim()}%");
        var result = new List<ArchiveEntry>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new ArchiveEntry
            {
                ItemId = Guid.Parse(reader.GetString(0)),
                NoteId = Guid.Parse(reader.GetString(1)),
                NoteTitle = reader.IsDBNull(2) ? "已删除便利贴" : reader.GetString(2),
                Text = reader.GetString(3),
                Details = reader.GetString(4),
                ArchivedAt = ParseDate(reader.GetString(5)),
                CompletedAt = ParseNullableDate(reader, 6)
            });
        }
        return result;
    }

    public async Task<Guid> RestoreArchivedItemAsync(Guid itemId)
    {
        await using var connection = await OpenAsync();
        var lookup = connection.CreateCommand();
        lookup.CommandText = """
            SELECT i.NoteId, CASE WHEN n.Id IS NULL OR n.DeletedAt IS NOT NULL THEN 0 ELSE 1 END
            FROM Items i LEFT JOIN Notes n ON n.Id=i.NoteId WHERE i.Id=$id;
            """;
        lookup.Parameters.AddWithValue("$id", itemId.ToString());
        await using var reader = await lookup.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("找不到要恢复的事项。");
        var noteId = Guid.Parse(reader.GetString(0));
        var noteAvailable = reader.GetInt32(1) == 1;
        await reader.DisposeAsync();

        if (!noteAvailable)
        {
            var recovered = (await GetActiveNotesAsync()).FirstOrDefault(n => n.Title == "已恢复事项")
                ?? await CreateNoteAsync(NoteKind.Checklist, "已恢复事项", "#D9F2E6");
            noteId = recovered.Id;
        }

        var command = connection.CreateCommand();
        command.CommandText = "UPDATE Items SET NoteId=$note, ArchivedAt=NULL, UpdatedAt=$now WHERE Id=$id;";
        command.Parameters.AddWithValue("$note", noteId.ToString());
        command.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.Now));
        command.Parameters.AddWithValue("$id", itemId.ToString());
        await command.ExecuteNonQueryAsync();
        return noteId;
    }

    public async Task<List<Reminder>> GetDueRemindersAsync(DateTimeOffset now, int limit = 5)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.Id, i.NoteId, n.Title, i.Text, i.ReminderAt
            FROM Items i JOIN Notes n ON n.Id=i.NoteId
            WHERE n.DeletedAt IS NULL AND i.DeletedAt IS NULL AND i.ArchivedAt IS NULL
              AND i.IsCompleted=0 AND i.ReminderAt IS NOT NULL AND i.ReminderAt <= $now
              AND i.ReminderNotifiedAt IS NULL
            ORDER BY i.ReminderAt LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$now", ToDb(now));
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<Reminder>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new Reminder
            {
                ItemId = Guid.Parse(reader.GetString(0)),
                NoteId = Guid.Parse(reader.GetString(1)),
                NoteTitle = reader.GetString(2),
                ItemText = reader.GetString(3),
                DueAt = ParseDate(reader.GetString(4))
            });
        }
        return result;
    }

    public async Task MarkReminderNotifiedAsync(Guid itemId, DateTimeOffset notifiedAt)
    {
        await ExecuteAsync("UPDATE Items SET ReminderNotifiedAt=$now WHERE Id=$id;", ("$now", ToDb(notifiedAt)), ("$id", itemId.ToString()));
    }

    public async Task<string?> GetSettingAsync(string key)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Settings WHERE Key=$key;";
        command.Parameters.AddWithValue("$key", key);
        return (string?)await command.ExecuteScalarAsync();
    }

    public async Task SetSettingAsync(string key, string value)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Settings(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=$value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task BackupDatabaseAsync(string destinationPath)
    {
        if (File.Exists(destinationPath)) File.Delete(destinationPath);
        await using var source = await OpenAsync();
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Pooling = false
        }.ToString());
        await destination.OpenAsync();
        source.BackupDatabase(destination);
    }

    public static async Task ValidateDatabaseAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('Notes','Items','Settings');";
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        if (count != 3) throw new InvalidDataException("备份中缺少必要的数据表。");
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000;";
        await command.ExecuteNonQueryAsync();
        return connection;
    }

    private static void AddNoteParameters(SqliteCommand command, StickyNote note)
    {
        command.Parameters.AddWithValue("$id", note.Id.ToString());
        command.Parameters.AddWithValue("$kind", (int)note.Kind);
        command.Parameters.AddWithValue("$title", note.Title);
        command.Parameters.AddWithValue("$color", note.Color);
        command.Parameters.AddWithValue("$mode", (int)note.WindowMode);
        command.Parameters.AddWithValue("$left", note.Left);
        command.Parameters.AddWithValue("$top", note.Top);
        command.Parameters.AddWithValue("$width", note.Width);
        command.Parameters.AddWithValue("$height", note.Height);
        command.Parameters.AddWithValue("$collapsed", note.IsCollapsed ? 1 : 0);
        command.Parameters.AddWithValue("$hidden", note.IsHidden ? 1 : 0);
        command.Parameters.AddWithValue("$due", (object?)ToDb(note.ProjectDueAt) ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", ToDb(note.CreatedAt));
        command.Parameters.AddWithValue("$updated", ToDb(note.UpdatedAt));
    }

    private static async Task<List<StickyNote>> ReadNotesAsync(SqliteCommand command)
    {
        var result = new List<StickyNote>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new StickyNote
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
                Kind = (NoteKind)reader.GetInt32(reader.GetOrdinal("Kind")),
                Title = reader.GetString(reader.GetOrdinal("Title")),
                Color = reader.GetString(reader.GetOrdinal("Color")),
                WindowMode = (NoteWindowMode)reader.GetInt32(reader.GetOrdinal("WindowMode")),
                Left = reader.GetDouble(reader.GetOrdinal("LeftPosition")),
                Top = reader.GetDouble(reader.GetOrdinal("TopPosition")),
                Width = reader.GetDouble(reader.GetOrdinal("Width")),
                Height = reader.GetDouble(reader.GetOrdinal("Height")),
                IsCollapsed = reader.GetInt32(reader.GetOrdinal("IsCollapsed")) != 0,
                IsHidden = reader.GetInt32(reader.GetOrdinal("IsHidden")) != 0,
                ProjectDueAt = ParseNullableDate(reader, reader.GetOrdinal("ProjectDueAt")),
                CreatedAt = ParseDate(reader.GetString(reader.GetOrdinal("CreatedAt"))),
                UpdatedAt = ParseDate(reader.GetString(reader.GetOrdinal("UpdatedAt"))),
                DeletedAt = ParseNullableDate(reader, reader.GetOrdinal("DeletedAt"))
            });
        }
        return result;
    }

    private static async Task<List<ChecklistItem>> ReadItemsAsync(SqliteCommand command)
    {
        var result = new List<ChecklistItem>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new ChecklistItem
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("Id"))),
                NoteId = Guid.Parse(reader.GetString(reader.GetOrdinal("NoteId"))),
                NoteTitle = reader.GetString(reader.GetOrdinal("Title")),
                SortOrder = reader.GetInt32(reader.GetOrdinal("SortOrder")),
                Text = reader.GetString(reader.GetOrdinal("Text")),
                Details = reader.GetString(reader.GetOrdinal("Details")),
                ReminderAt = ParseNullableDate(reader, reader.GetOrdinal("ReminderAt")),
                ReminderNotifiedAt = ParseNullableDate(reader, reader.GetOrdinal("ReminderNotifiedAt")),
                IsCompleted = reader.GetInt32(reader.GetOrdinal("IsCompleted")) != 0,
                CompletedAt = ParseNullableDate(reader, reader.GetOrdinal("CompletedAt")),
                ArchivedAt = ParseNullableDate(reader, reader.GetOrdinal("ArchivedAt")),
                DeletedAt = ParseNullableDate(reader, reader.GetOrdinal("DeletedAt")),
                CreatedAt = ParseDate(reader.GetString(reader.GetOrdinal("CreatedAt"))),
                UpdatedAt = ParseDate(reader.GetString(reader.GetOrdinal("UpdatedAt")))
            });
        }
        return result;
    }

    private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql, Guid id)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id.ToString());
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task UpdateSortAsync(SqliteConnection connection, System.Data.Common.DbTransaction transaction, Guid id, int order)
    {
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE Items SET SortOrder=$sort, UpdatedAt=$now WHERE Id=$id;";
        command.Parameters.AddWithValue("$sort", order);
        command.Parameters.AddWithValue("$now", ToDb(DateTimeOffset.Now));
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync();
    }

    private static string ToDb(DateTimeOffset value) => value.ToString("O");
    private static string? ToDb(DateTimeOffset? value) => value?.ToString("O");
    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
    private static DateTimeOffset? ParseNullableDate(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ParseDate(reader.GetString(ordinal));
}
