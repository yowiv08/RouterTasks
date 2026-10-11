using System.Text.Json;

namespace Plugins.RouterTasks;

/// <summary>仅在激活后的管理访问中打开。进程级独占锁避免新旧代际或两个实例同时写入。</summary>
internal sealed class TaskStore(string directory, Func<List<RequestTemplate>> presets, IRuntimeClock clock) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private StoreDocument? _document;
    private FileStream? _lease;
    private bool _disposed;
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    private string FilePath => Path.Combine(DirectoryPath, "state.json");

    public async Task<StoreDocument> ReadAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { await EnsureAsync(token); return JsonData.Clone(_document!); }
        finally { _gate.Release(); }
    }

    public async Task<T> UpdateAsync<T>(Func<StoreDocument, T> update, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            await EnsureAsync(token);
            var candidate = JsonData.Clone(_document!);
            var result = update(candidate);
            candidate.Revision++;
            Trim(candidate);
            await PersistAsync(candidate, token);
            _document = candidate;
            return JsonData.Clone(result);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_document is not null) return;
        Directory.CreateDirectory(DirectoryPath);
        _lease ??= new FileStream(Path.Combine(DirectoryPath, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        StoreDocument loaded;
        try
        {
            if (File.Exists(FilePath))
            {
                await using var input = File.OpenRead(FilePath);
                loaded = await JsonSerializer.DeserializeAsync<StoreDocument>(input, JsonData.Options, token)
                    ?? throw new IOException("状态文件为空；请从备份恢复，不能自动覆盖");
            }
            else loaded = new StoreDocument { Templates = presets() };
            if (loaded.SchemaVersion != 1) throw new IOException("不支持的数据版本；未修改原文件");
            if (loaded.Tasks is null || loaded.Sites is null || loaded.Templates is null || loaded.Keys is null
                || loaded.Notifications is null || loaded.Stats is null) throw new IOException("状态文件结构无效；未修改原文件");
            Validation.Settings(loaded.Settings);
            foreach (var item in loaded.Tasks)
            {
                if (item?.Runtime is null || item.Config is null || item.Runtime.Lanes is null || item.Events is null
                    || item.Runtime.QueueLaneAttempts is null || item.Runtime.KeepaliveLaneAttempts is null)
                    throw new IOException("任务状态结构无效；未修改原文件");
                if (item.Config.Settings is { } settings) Validation.Settings(settings);
                if (item.Runtime.Status is "running" or "recovering")
                {
                    item.Runtime.Status = "paused";
                    item.Runtime.PauseReason = "宿主重启或插件更新；未确认请求不自动重放";
                    if (item.Runtime.ResponseState == "reading") item.Runtime.ResponseState = "interrupted";
                    AddEvent(loaded, item, clock.Now, "paused", item.Runtime.PauseReason);
                }
                item.Runtime.Generation = null;
                item.Runtime.SessionConfirmed = false;
                item.Runtime.FailureSince = null; item.Runtime.DropPendingSince = null;
                item.Runtime.NextAttemptAt = null;
                foreach (var lane in item.Runtime.Lanes) { lane.NextAttemptAt = null; lane.Status = "stopped"; }
            }
            foreach (var notification in loaded.Notifications.Where(n => n.State is "queued" or "retrying" or "sending"))
            { notification.State = "paused"; notification.Error = "宿主重启后待人工恢复投递（在途投递可能已送达）"; }
            Trim(loaded);
            await PersistAsync(loaded, token);
            _document = loaded;
        }
        catch { _lease.Dispose(); _lease = null; throw; }
    }

    private async Task PersistAsync(StoreDocument document, CancellationToken token)
    {
        var temporary = Path.Combine(DirectoryPath, $"state.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(output, document, JsonData.Options, token);
                await output.FlushAsync(token);
                output.Flush(flushToDisk: true);
            }
            token.ThrowIfCancellationRequested();
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void Trim(StoreDocument document)
    {
        foreach (var item in document.Tasks)
        {
            var settings = SettingsFor(document, item.Config.Id);
            if (item.Events.Count > settings.MaxEventsPerTask)
                item.Events.RemoveRange(0, item.Events.Count - settings.MaxEventsPerTask);
            item.Runtime.ResponseSummary = TextOutput.LimitUtf8(item.Runtime.ResponseSummary, settings.SummaryBytes);
        }
        foreach (var taskId in document.Notifications.Select(n => n.TaskId).Distinct().ToArray())
        {
            var settings = SettingsFor(document, taskId);
            var before = clock.Now.AddDays(-settings.HistoryDays);
            document.Notifications.RemoveAll(n => n.TaskId == taskId && n.CreatedAt < before && n.State is "sent" or "dead");
            var excess = document.Notifications.Count(n => n.TaskId == taskId) - settings.MaxNotificationRecords;
            if (excess > 0)
                foreach (var id in document.Notifications.Where(n => n.TaskId == taskId && n.State is "sent" or "dead")
                    .OrderBy(n => n.CreatedAt).Take(excess).Select(n => n.Id).ToArray())
                    document.Notifications.RemoveAll(n => n.Id == id);
        }
        var day = clock.Now.AddDays(-30).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");
        document.Stats.RemoveAll(s => string.CompareOrdinal(s.Day, day) < 0);
    }

    public static TaskRecord Find(StoreDocument document, string id) => document.Tasks.Find(t => t.Config.Id == id)
        ?? throw new KeyNotFoundException("任务不存在");
    public static PluginSettings SettingsFor(StoreDocument document, string taskId)
        => document.Tasks.Find(t => t.Config.Id == taskId)?.Config.Settings ?? document.Settings;
    public static void AddEvent(StoreDocument document, TaskRecord task, DateTimeOffset at, string kind, string message, int? lane = null)
    {
        task.Runtime.UpdatedAt = at;
        task.Events.Add(new TaskEvent(at, kind, TextOutput.LimitUtf8(message, 2048), lane));
        if (task.Events.Count > SettingsFor(document, task.Config.Id).MaxEventsPerTask) task.Events.RemoveAt(0);
    }
    public static void Notify(StoreDocument document, TaskRecord task, DateTimeOffset at, string kind, string message)
    {
        if (!task.Config.NotificationEvents.Contains(kind)) return;
        var settings = task.Config.Settings ?? document.Settings;
        var channels = new List<string>();
        if (settings.TelegramEnabled) channels.Add("telegram");
        if (settings.BarkEnabled) channels.Add("bark");
        var scope = kind switch
        {
            "entered" => $"{task.Runtime.RunId}:entered",
            "dropped" or "recovered" => $"{task.Runtime.RunId}:outage:{task.Runtime.OutageSequence}:{kind}",
            _ => $"{task.Runtime.RunId}:{task.Runtime.Phase}:round:{task.Runtime.QueueRound}:attempt:{task.Runtime.TotalAttempts}:outage:{task.Runtime.OutageSequence}:{kind}"
        };
        var title = kind switch
        {
            "entered" => "首次进入", "dropped" => "确认掉线", "recovered" => "掉线后恢复",
            "stopped" => "错误停止", "paused" => "自动暂停", "exhausted" => "预算耗尽", _ => kind
        };
        foreach (var channel in channels)
        {
            var id = $"{scope}:{channel}";
            var legacyId = $"{task.Runtime.RunId}:{task.Runtime.QueueRound}:{task.Runtime.OutageSequence}:{kind}:{channel}";
            if (document.Notifications.Any(n => n.Id == id || n.Id == legacyId)) continue;
            while (document.Notifications.Count(n => n.TaskId == task.Config.Id) >= settings.MaxNotificationRecords)
            {
                var old = document.Notifications.Where(n => n.TaskId == task.Config.Id && n.State is "sent" or "dead").MinBy(n => n.CreatedAt);
                if (old is null) break;
                document.Notifications.Remove(old);
            }
            if (document.Notifications.Count(n => n.TaskId == task.Config.Id) >= settings.MaxNotificationRecords)
            {
                document.NotificationOverflowCount++;
                document.NotificationWarning = $"{task.Config.Name}：通知队列已满，新事件未入队。请恢复投递或提高记录上限。";
                AddEvent(document, task, at, "notification-overflow", $"{channel}/{kind}：{document.NotificationWarning}");
                continue;
            }
            document.Notifications.Add(new NotificationItem { Id = id, TaskId = task.Config.Id, Kind = kind,
                ProfileId = task.Config.Settings is null ? CredentialVault.ProfileId : CredentialVault.AccountProfile(task.Config.Id),
                Channel = channel, Title = TextOutput.Summary($"RouterTasks · {task.Config.Name} · {title}"),
                Message = $"发生时间：{at.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss}（UTC+8）\n{TextOutput.Summary(message)}",
                CreatedAt = at, NextAttemptAt = at });
        }
    }
    public static void Count(StoreDocument document, DateTimeOffset at, string protocol, string kind, string taskId = "")
    {
        var day = at.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd");
        var stat = document.Stats.Find(s => s.Day == day && s.Protocol == protocol && s.TaskId == taskId);
        if (stat is null) { stat = new DailyStat { Day = day, Protocol = protocol, TaskId = taskId }; document.Stats.Add(stat); }
        if (kind == "entered") stat.Entered++;
        else if (kind == "recovered") stat.Recoveries++;
        else stat.Heartbeats++;
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try { _disposed = true; _document = null; _lease?.Dispose(); _lease = null; }
        finally { _gate.Release(); }
    }
}
