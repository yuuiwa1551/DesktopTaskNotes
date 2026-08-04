using System.Globalization;

namespace DesktopTaskNotes.Models;

public enum NoteKind
{
    Checklist = 0,
    Project = 1
}

public enum NoteWindowMode
{
    Desktop = 0,
    Topmost = 1
}

public sealed class StickyNote
{
    public Guid Id { get; set; }
    public NoteKind Kind { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Color { get; set; } = "#FFF2A8";
    public NoteWindowMode WindowMode { get; set; }
    public double Left { get; set; } = 120;
    public double Top { get; set; } = 120;
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 430;
    public bool IsCollapsed { get; set; }
    public bool IsHidden { get; set; }
    public DateTimeOffset? ProjectDueAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class ChecklistItem
{
    public Guid Id { get; set; }
    public Guid NoteId { get; set; }
    public string NoteTitle { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public DateTimeOffset? ReminderAt { get; set; }
    public DateTimeOffset? ReminderNotifiedAt { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string ReminderDisplay => ReminderAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? string.Empty;
    public string StateDisplay => IsCompleted ? "已完成" : ReminderAt < DateTimeOffset.Now ? "已逾期" : "进行中";
}

public sealed class Reminder
{
    public Guid ItemId { get; init; }
    public Guid NoteId { get; init; }
    public string NoteTitle { get; init; } = string.Empty;
    public string ItemText { get; init; } = string.Empty;
    public DateTimeOffset DueAt { get; init; }
}

public sealed class ArchiveEntry
{
    public Guid ItemId { get; init; }
    public Guid NoteId { get; init; }
    public string NoteTitle { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string Details { get; init; } = string.Empty;
    public DateTimeOffset ArchivedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

public sealed class NoteSummary
{
    public Guid Id { get; init; }
    public NoteKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public bool IsHidden { get; init; }
    public NoteWindowMode WindowMode { get; init; }
    public int ActiveCount { get; init; }
    public int CompletedCount { get; init; }
    public int TotalCount => ActiveCount + CompletedCount;
    public int ProgressPercent => TotalCount == 0 ? 0 : (int)Math.Round(CompletedCount * 100d / TotalCount);
    public DateTimeOffset? ProjectDueAt { get; init; }
    public DateTimeOffset? DeletedAt { get; init; }
    public string KindDisplay => Kind == NoteKind.Project ? "项目贴" : "清单贴";
    public string ModeDisplay => WindowMode == NoteWindowMode.Topmost ? "始终置顶" : "桌面模式";
    public string VisibilityDisplay => IsHidden ? "已隐藏" : "显示中";
}

public sealed class BackupManifest
{
    public int FormatVersion { get; init; } = 1;
    public string ApplicationVersion { get; init; } = "1.0.0";
    public DateTimeOffset CreatedAt { get; init; }
    public string DatabaseEntry { get; init; } = "desktop-task-notes.db";
    public string Sha256 { get; init; } = string.Empty;
}
