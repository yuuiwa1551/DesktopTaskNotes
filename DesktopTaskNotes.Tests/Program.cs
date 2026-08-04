using DesktopTaskNotes.Data;
using DesktopTaskNotes.Models;
using DesktopTaskNotes.Services;

var root = Path.Combine(Path.GetTempPath(), $"desktop-task-notes-tests-{Guid.NewGuid():N}");
var paths = new AppPaths(Path.Combine(root, "data"), Path.Combine(root, "backups"));
try
{
    var database = new DatabaseService(paths.DatabasePath);
    await database.InitializeAsync();

    var initialNotes = await database.GetActiveNotesAsync();
    Assert(initialNotes.Count == 1, "首次启动应创建一张欢迎便利贴");

    var project = await database.CreateNoteAsync(NoteKind.Project, "半年项目", "#DDEEFF");
    project.ProjectDueAt = DateTimeOffset.Now.AddMonths(6);
    await database.UpdateNoteMetadataAsync(project);

    var first = await database.AddItemAsync(project.Id, "第一阶段");
    var second = await database.AddItemAsync(project.Id, "第二阶段");
    await database.SetItemCompletedAsync(first.Id, true);

    var summary = (await database.GetNoteSummariesAsync()).Single(n => n.Id == project.Id);
    Assert(summary.ActiveCount == 1 && summary.CompletedCount == 1 && summary.ProgressPercent == 50,
        "项目进度应按当前未归档子任务计算");

    var archived = await database.ArchiveCompletedAsync(project.Id);
    Assert(archived.SequenceEqual([first.Id]), "只应归档已勾选事项");
    Assert((await database.GetArchiveAsync()).Any(a => a.ItemId == first.Id), "归档事项应可检索");
    await database.UndoArchiveAsync(archived);
    Assert((await database.GetItemsForNoteAsync(project.Id)).Count == 2, "撤销清理应恢复事项");

    await database.ReorderItemAsync(first.Id, second.Id);
    var reordered = await database.GetItemsForNoteAsync(project.Id);
    Assert(reordered[0].Id == second.Id, "拖放排序应交换事项顺序");

    var removable = await database.AddItemAsync(project.Id, "误添加事项");
    await database.DeleteItemAsync(removable.Id);
    Assert(!(await database.GetItemsForNoteAsync(project.Id)).Any(i => i.Id == removable.Id),
        "单条待办删除后不应继续显示");

    second.Details = "提醒测试备注";
    second.ReminderAt = DateTimeOffset.Now.AddMinutes(-1);
    await database.UpdateItemAsync(second);
    var due = await database.GetDueRemindersAsync(DateTimeOffset.Now);
    Assert(due.Any(r => r.ItemId == second.Id), "过期且未完成的事项应进入提醒队列");
    await database.MarkReminderNotifiedAsync(second.Id, DateTimeOffset.Now);
    Assert(!(await database.GetDueRemindersAsync(DateTimeOffset.Now)).Any(r => r.ItemId == second.Id),
        "已经通知的提醒不应重复出现");

    var found = await database.SearchActiveItemsAsync("提醒测试");
    Assert(found.Any(i => i.Id == second.Id), "搜索应覆盖待办备注");

    await database.SoftDeleteNoteAsync(project.Id);
    Assert((await database.GetNoteSummariesAsync(true)).Any(n => n.Id == project.Id), "删除后应进入回收站");
    await database.RestoreNoteAsync(project.Id);
    Assert((await database.GetNoteAsync(project.Id))?.DeletedAt is null, "回收站应可恢复便利贴");

    var backup = new BackupService(paths, database);
    var backupPath = Path.Combine(paths.BackupDirectory, "integration.dtnbackup");
    await backup.CreateBackupAsync(backupPath);
    Assert(File.Exists(backupPath) && new FileInfo(backupPath).Length > 0, "应生成非空备份包");

    second.Text = "备份后修改";
    await database.UpdateItemAsync(second);
    await backup.RestoreBackupAsync(backupPath);
    var restoredDatabase = new DatabaseService(paths.DatabasePath);
    await restoredDatabase.InitializeAsync();
    var restoredItems = await restoredDatabase.GetItemsForNoteAsync(project.Id);
    Assert(restoredItems.Single(i => i.Id == second.Id).Text == "第二阶段", "恢复应还原备份时的数据");

    Console.WriteLine("PASS: 14 integration assertions");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException($"ASSERT FAILED: {message}");
    Console.WriteLine($"PASS: {message}");
}
