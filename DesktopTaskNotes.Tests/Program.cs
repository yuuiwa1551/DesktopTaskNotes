using DesktopTaskNotes.Data;
using DesktopTaskNotes.Models;
using DesktopTaskNotes.Services;

var root = Path.Combine(Path.GetTempPath(), $"desktop-task-notes-tests-{Guid.NewGuid():N}");
var paths = new AppPaths(Path.Combine(root, "data"), Path.Combine(root, "backups"));
try
{
    var database = new DatabaseService(paths.DatabasePath);
    await database.InitializeAsync();

    var workArea = new SnapRectangle(0, 0, 1920, 1080);
    var edgeSnapped = WindowSnapService.Snap(
        new SnapRectangle(6, 100, 306, 500), workArea, [], 8);
    Assert(edgeSnapped.Left == 0, "便利贴靠近屏幕左侧时应精确吸附到工作区边缘");

    var cornerSnapped = WindowSnapService.Snap(
        new SnapRectangle(1614, 676, 1914, 1076), workArea, [], 8);
    Assert(cornerSnapped.Right == workArea.Right && cornerSnapped.Bottom == workArea.Bottom,
        "便利贴靠近屏幕右下角时应同时贴齐两条边");

    var obstacle = new SnapRectangle(100, 100, 400, 500);
    var noteSnapped = WindowSnapService.Snap(
        new SnapRectangle(406, 130, 706, 530), workArea, [obstacle], 8);
    Assert(noteSnapped.Left == obstacle.Right && !noteSnapped.Intersects(obstacle),
        "两张便利贴靠近时应吸在一起且不重叠");

    var shallowOverlapSnapped = WindowSnapService.Snap(
        new SnapRectangle(394, 150, 694, 550), workArea, [obstacle], 8);
    Assert(shallowOverlapSnapped.Left == obstacle.Right && !shallowOverlapSnapped.Intersects(obstacle),
        "轻微越过贴合线时仍应辅助吸回相邻边缘");

    var intentionalOverlap = WindowSnapService.Snap(
        new SnapRectangle(388, 150, 688, 550), workArea, [obstacle], 8);
    Assert(intentionalOverlap.Left == 388 && intentionalOverlap.Intersects(obstacle),
        "继续拖过软吸附距离后应允许两张便利贴重叠");

    var crossingMonitor = WindowSnapService.Snap(
        new SnapRectangle(1800, 200, 2100, 600), workArea, [], 8, false);
    Assert(crossingMonitor.Left == 1800 && crossingMonitor.Right == 2100,
        "实时吸附不应把正在跨显示器拖动的便利贴锁回原屏幕");

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

    await database.ReorderItemAsync(first.Id, second.Id, insertAfter: true);
    var reordered = await database.GetItemsForNoteAsync(project.Id);
    Assert(reordered[0].Id == second.Id, "拖放排序应移动到目标项之后");

    var third = await database.AddItemAsync(project.Id, "第三阶段");
    var fourth = await database.AddItemAsync(project.Id, "第四阶段");
    await database.ReorderItemAsync(second.Id, fourth.Id, true);
    reordered = await database.GetItemsForNoteAsync(project.Id);
    Assert(reordered.Select(i => i.Id).SequenceEqual([first.Id, third.Id, fourth.Id, second.Id]),
        "跨多项拖放应插入到目标位置而不是交换首尾");

    var removable = await database.AddItemAsync(project.Id, "误添加事项");
    await database.DeleteItemAsync(removable.Id);
    Assert(!(await database.GetItemsForNoteAsync(project.Id)).Any(i => i.Id == removable.Id),
        "单条待办删除后不应继续显示");
    await database.RestoreDeletedItemAsync(removable.Id);
    Assert((await database.GetItemsForNoteAsync(project.Id)).Any(i => i.Id == removable.Id),
        "刚删除的单条待办应可撤销恢复");

    second.Details = "提醒测试备注";
    second.ReminderAt = DateTimeOffset.Now.AddMinutes(-1);
    await database.UpdateItemAsync(second);
    var due = await database.GetDueRemindersAsync(DateTimeOffset.Now);
    Assert(due.Any(r => r.ItemId == second.Id), "过期且未完成的事项应进入提醒队列");
    await database.MarkReminderNotifiedAsync(second.Id, DateTimeOffset.Now);
    Assert(!(await database.GetDueRemindersAsync(DateTimeOffset.Now)).Any(r => r.ItemId == second.Id),
        "已经通知的提醒不应重复出现");

    var reminderToCancel = await database.AddItemAsync(project.Id, "完成时取消提醒");
    reminderToCancel.ReminderAt = DateTimeOffset.Now.AddHours(2);
    await database.UpdateItemAsync(reminderToCancel);
    await database.SetItemCompletedAsync(reminderToCancel.Id, true);
    var completedWithCanceledReminder = (await database.GetItemsForNoteAsync(project.Id))
        .Single(i => i.Id == reminderToCancel.Id);
    Assert(completedWithCanceledReminder.ReminderAt is null,
        "勾选完成后应真正清除尚未触发的提醒");

    var found = await database.SearchActiveItemsAsync("提醒测试");
    Assert(found.Any(i => i.Id == second.Id), "搜索应覆盖待办备注");
    var matchingNotes = await database.GetNoteSummariesAsync(search: "提醒测试");
    Assert(matchingNotes.Any(n => n.Id == project.Id), "按待办或备注搜索时应返回所属便利贴");

    await database.SetItemCompletedAsync(third.Id, true);
    var progressBeforeArchive = await database.GetProjectProgressAsync(project.Id);
    await database.ArchiveCompletedAsync(project.Id);
    var progressAfterArchive = await database.GetProjectProgressAsync(project.Id);
    Assert(progressAfterArchive == progressBeforeArchive,
        "清理已完成后项目累计进度不应归零");

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

    Console.WriteLine("PASS: 25 integration assertions");
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
