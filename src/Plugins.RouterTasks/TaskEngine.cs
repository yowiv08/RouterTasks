using System.Collections.Concurrent;

namespace Plugins.RouterTasks;

/// <summary>管理账号任务的异步排队、保活、取消和运行快照。</summary>
internal sealed class TaskEngine(TaskStore store, IProbeClient probes, IRuntimeClock clock,
    Func<TaskConfig, CancellationToken, Task<RunSnapshot>> resolve,
    Func<RunSnapshot, CancellationToken, Task<string>> prompt) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ActiveRun> _runs = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _stopped;
    private readonly ConcurrentDictionary<string, string> _failures = new();
    public IReadOnlyDictionary<string, string> Failures => _failures;
    public bool IsRunning(string id) => _runs.ContainsKey(id);
    public int ActiveCount => _runs.Count;

    public async Task ActionAsync(string id, string action, CancellationToken token = default)
    {
        Validation.Id(id);
        await _commands.WaitAsync(token);
        try
        {
            if (_stopped) throw new ConflictException("插件正在停止");
            var existing = _runs.GetValueOrDefault(id);
            if (action is "pause" or "cancel")
            {
                if (existing is not null) { existing.StopReason = action == "cancel" ? "cancelled" : "paused"; await existing.CancelAsync(); await existing.Work; }
                await store.UpdateAsync(document =>
                {
                    var task = TaskStore.Find(document, id);
                    if (task.Runtime.Status != "completed")
                    {
                        task.Runtime.Status = action == "cancel" ? "cancelled" : "paused";
                        task.Runtime.PauseReason = action == "cancel" ? "用户取消" : "用户暂停";
                        task.Runtime.NextAttemptAt = null;
                    }
                    return true;
                }, token);
                return;
            }
            if (action is not ("start" or "resume" or "restart" or "retry" or "requeue")) throw new FormatException("未知任务操作");
            if (existing is not null && action is "start" or "resume") return;
            var current = TaskStore.Find(await store.ReadAsync(token), id);
            if (current.Runtime.ResponseState == "reading" && action == "retry") throw new ConflictException("已接受的响应不能立即重放；请等待结束或取消");
            if (existing is not null) { existing.StopReason = "paused"; await existing.CancelAsync(); await existing.Work; }
            current = TaskStore.Find(await store.ReadAsync(token), id);
            if (current.Runtime.Status == "completed" && action != "restart") throw new ConflictException("已完成任务需要显式重新开始");
            if (current.Config.Mode == "queue" && current.Runtime.Entered && current.Runtime.ResponseState != "none" && action != "restart")
                throw new ConflictException("该请求已经被接受，不能继续重放；如需新请求，请显式重新开始");
            var snapshot = await resolve(current.Config, token);
            var generation = Guid.NewGuid().ToString("N");
            snapshot = snapshot with { Generation = generation };
            await store.UpdateAsync(document =>
            {
                var task = TaskStore.Find(document, id);
                if (action == "restart") task.Runtime = new TaskRuntime();
                var runtime = task.Runtime;
                if (runtime.StartedAt is null)
                {
                    runtime.StartedAt = clock.Now;
                    runtime.Phase = task.Config.Mode == "keepalive" ? "keepalive" : "queue";
                }
                if (action == "requeue") { runtime.Phase = "queue"; runtime.QueueRound++; runtime.Lanes.Clear(); runtime.QueueLaneAttempts.Clear(); }
                if (runtime.Phase == "queue" && runtime.QueueRound == 0) runtime.QueueRound = 1;
                runtime.Generation = generation; runtime.Status = "running";
                runtime.PauseReason = null; runtime.NextAttemptAt = null;
                runtime.SessionConfirmed = false; runtime.MonitoringSince = clock.Now;
                runtime.FailureSince = null; runtime.OutageNotified = false;
                task.Snapshot = new SavedRunSnapshot(snapshot.Config, snapshot.Site, snapshot.Template, snapshot.Settings, clock.Now);
                // 继续不复用旧连接，不退款已记录的尝试。历史接受不能冒充当前心跳。
                TaskStore.AddEvent(document, task, clock.Now, action, "任务启动/继续；请求使用新的配置快照");
                return true;
            }, token);
            var active = new ActiveRun(snapshot, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            _failures.TryRemove(id, out _);
            _runs[id] = active;
            active.Work = ExecuteAsync(active);
        }
        finally { _commands.Release(); }
    }

    private async Task ExecuteAsync(ActiveRun active)
    {
        await Task.Yield(); // 先将 Work 和取消源登记到当前插件代际，再开始任何网络操作。
        var token = active.Stop.Token;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var runtime = (await CurrentAsync(active, token)).Runtime;
                var queue = runtime.Phase == "queue";
                var result = await CycleAsync(active, queue, token);
                if (result.Kind == ProbeKind.Accepted)
                {
                    if (active.Snapshot.Config.Mode == "queue")
                    {
                        await ChangeAsync(active, (document, task) =>
                        {
                            task.Runtime.Status = "completed";
                            task.Runtime.ResponseState = active.Snapshot.Config.Queue.ReadFullResponse ? (result.Completed ? "completed" : "interrupted") : "closed";
                            task.Runtime.ResponseSummary = result.Summary;
                            task.Runtime.LastError = result.Error;
                            TaskStore.AddEvent(document, task, clock.Now, "completed", result.Error ?? "请求已接受，任务结束");
                        });
                        return;
                    }
                    await ChangeAsync(active, (document, task) =>
                    {
                        task.Runtime.Phase = "keepalive"; task.Runtime.Status = "running";
                        task.Runtime.ResponseState = "closed";
                        if (queue) { task.Runtime.Lanes.Clear(); task.Runtime.KeepaliveLaneAttempts.Clear(); }
                        TaskStore.AddEvent(document, task, clock.Now, "keepalive", "已确认接受事件，按保活参数继续");
                    });
                    await WaitAsync(active, null, Jitter(active.Snapshot.Config.Keepalive.HeartbeatSeconds, active.Snapshot.Config.Keepalive.HeartbeatMaxSeconds), token);
                    continue;
                }
                if (result.Error == "budget_exhausted") { await StopWithAsync(active, "exhausted", "当前阶段所有线程的次数预算已耗尽", "exhausted"); return; }
                if (result.Kind is ProbeKind.Permanent or ProbeKind.Protocol)
                { await StopWithAsync(active, "failed", result.Error ?? "永久错误", "stopped"); return; }
                if (!queue && result.Kind == ProbeKind.Busy)
                {
                    await ChangeAsync(active, (document, task) =>
                    {
                        if (task.Runtime.FailureSince is null) task.Runtime.OutageSequence++;
                        task.Runtime.FailureSince ??= clock.Now;
                        task.Runtime.SessionConfirmed = false;
                        if (!task.Runtime.OutageNotified)
                        {
                            task.Runtime.OutageNotified = true;
                            TaskStore.Notify(document, task, clock.Now, "dropped", "收到配置的排队状态码，需要重新排队");
                        }
                    });
                    if (!await RequeueAsync(active, result.Error ?? "命中排队状态码")) return;
                    if (result.RetryAfterSeconds > 0) await WaitAsync(active, null, result.RetryAfterSeconds, token);
                    continue;
                }
                if (!queue)
                {
                    var settings = active.Snapshot.Config.Keepalive;
                    if (result.Kind == ProbeKind.Network && !settings.RetryNetworkErrors || result.Kind == ProbeKind.Timeout && !settings.RetryTimeouts)
                    { await StopWithAsync(active, "failed", result.Error ?? "已关闭失败重试", "stopped"); return; }
                    var dropped = false;
                    await ChangeAsync(active, (document, task) =>
                    {
                        var state = task.Runtime;
                        if (state.FailureSince is null) state.OutageSequence++;
                        state.FailureSince ??= clock.Now;
                        var last = state.LastSuccessAt > state.MonitoringSince ? state.LastSuccessAt!.Value : state.MonitoringSince ?? state.FailureSince.Value;
                        var elapsed = (clock.Now - last).TotalSeconds;
                        if (elapsed >= settings.IdleEvictionSeconds) { state.Status = "recovering"; state.SessionConfirmed = false; }
                        dropped = elapsed >= settings.IdleEvictionSeconds + settings.DropConfirmSeconds;
                        if (dropped && !state.OutageNotified)
                        {
                            state.OutageNotified = true;
                            TaskStore.AddEvent(document, task, clock.Now, "dropped", "超过失联与掉线确认窗口");
                            TaskStore.Notify(document, task, clock.Now, "dropped", "超过失联与掉线确认窗口");
                        }
                    });
                    if (dropped) { if (!await RequeueAsync(active, "已确认掉线")) return; }
                    else await WaitAsync(active, null, Math.Max(settings.FailureRetrySeconds, result.RetryAfterSeconds), token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            try { await ChangeAsync(active, (document, task) =>
            {
                var reading = task.Runtime.ResponseState == "reading";
                task.Runtime.Status = active.StopReason;
                task.Runtime.SessionConfirmed = false;
                if (reading) task.Runtime.ResponseState = "interrupted";
                task.Runtime.PauseReason = reading ? "已接受的响应被取消；不自动重放" : "用户操作或插件停止；在途结果可能未确认";
                TaskStore.AddEvent(document, task, clock.Now, task.Runtime.Status, task.Runtime.PauseReason);
            }); }
            catch (Exception error) { _failures[active.Snapshot.Config.Id] = TextOutput.Summary(error.Message); }
        }
        catch (Exception error)
        {
            try { await StopWithAsync(active, "failed", TextOutput.Summary(error.Message), "stopped"); }
            catch { /* 状态磁盘失败不能使网络继续运行；任务由 finally 从活动集合移除。 */ }
            active.Failure = TextOutput.Summary(error.Message);
            _failures[active.Snapshot.Config.Id] = active.Failure;
        }
        finally
        {
            try
            {
                await ChangeAsync(active, (_, task) =>
                {
                    task.Runtime.Generation = null; task.Runtime.NextAttemptAt = null;
                    foreach (var lane in task.Runtime.Lanes) { lane.NextAttemptAt = null; if (lane.Status is "requesting" or "waiting") lane.Status = "stopped"; }
                });
            }
            catch { }
            _runs.TryRemove(new KeyValuePair<string, ActiveRun>(active.Snapshot.Config.Id, active));
            active.Stop.Dispose();
        }
    }

    private async Task<ProbeResult> CycleAsync(ActiveRun active, bool queue, CancellationToken token)
    {
        var count = queue ? active.Snapshot.Config.Queue.Concurrency : active.Snapshot.Config.Keepalive.Concurrency;
        await ChangeAsync(active, (_, task) =>
        {
            var old = queue ? task.Runtime.QueueLaneAttempts : task.Runtime.KeepaliveLaneAttempts;
            if (old.Count == 0) foreach (var lane in task.Runtime.Lanes) old[lane.Index] = lane.Attempts;
            task.Runtime.NextAttemptAt = null;
            task.Runtime.Lanes = Enumerable.Range(1, count).Select(i => new LaneState { Index = i, Attempts = old.GetValueOrDefault(i) }).ToList();
        });
        var sources = new List<CancellationTokenSource>();
        try
        {
            for (var i = 0; i < count; i++) sources.Add(CancellationTokenSource.CreateLinkedTokenSource(token));
            using var decision = new SemaphoreSlim(1, 1);
            var winner = -1; ProbeResult? terminal = null;
            async Task<bool> AcceptAsync(int index, int status)
            {
                await decision.WaitAsync(token);
                try
                {
                    if (winner >= 0 || terminal is not null || token.IsCancellationRequested) return false;
                    await ChangeAsync(active, (document, task) =>
                    {
                        var state = task.Runtime;
                        var first = !state.Entered;
                        var recovered = state.Entered && (queue || state.FailureSince is not null);
                        state.Entered = true; state.AcceptedAt ??= clock.Now; state.LastSuccessAt = clock.Now;
                        state.SessionConfirmed = true; state.MonitoringSince = clock.Now;
                        state.LastHttpStatus = status; state.LastError = null; state.MatchedRule = "SSE accepted";
                        state.ResponseState = queue && active.Snapshot.Config.Mode == "queue" && active.Snapshot.Config.Queue.ReadFullResponse ? "reading" : "closed";
                        if (!queue) { state.Heartbeats++; TaskStore.Count(document, clock.Now, active.Snapshot.Template.Protocol, "heartbeat", task.Config.Id); }
                        if (first || recovered)
                        {
                            var kind = first ? "entered" : "recovered";
                            if (recovered) state.Recoveries++;
                            TaskStore.Count(document, clock.Now, active.Snapshot.Template.Protocol, kind, task.Config.Id);
                            TaskStore.Notify(document, task, clock.Now, kind, $"{task.Config.Model} 收到正式 SSE 接受事件");
                        }
                        state.FailureSince = null; state.OutageNotified = false;
                        state.Lanes[index].Status = "accepted";
                        TaskStore.AddEvent(document, task, clock.Now, "accepted", "收到正式 SSE 接受事件；其余线程停止", index + 1);
                    });
                    winner = index;
                    for (var j = 0; j < sources.Count; j++) if (j != index) await sources[j].CancelAsync();
                    return true;
                }
                finally { decision.Release(); }
            }
            async Task<ProbeResult> LaneAsync(int index)
            {
                await Task.Yield();
                var laneToken = sources[index].Token;
                try
                {
                    while (true)
                    {
                        laneToken.ThrowIfCancellationRequested();
                        var state = (await CurrentAsync(active, laneToken)).Runtime;
                        var budget = queue ? active.Snapshot.Config.Queue.MaxAttemptsPerLane : active.Snapshot.Config.Keepalive.MaxAttemptsPerLane;
                        if (budget is { } limit && state.Lanes[index].Attempts >= limit)
                        {
                            await ChangeAsync(active, (_, task) => task.Runtime.Lanes[index].Status = "exhausted");
                            return new ProbeResult(ProbeKind.Lost, Error: "budget_exhausted");
                        }
                        var actualPrompt = await prompt(active.Snapshot, laneToken);
                        laneToken.ThrowIfCancellationRequested();
                        await ChangeAsync(active, (document, task) =>
                        {
                            task.Runtime.TotalAttempts++;
                            var lane = task.Runtime.Lanes[index]; lane.Attempts++; lane.Status = "requesting"; lane.NextAttemptAt = null;
                            (queue ? task.Runtime.QueueLaneAttempts : task.Runtime.KeepaliveLaneAttempts)[lane.Index] = lane.Attempts;
                            TaskStore.AddEvent(document, task, clock.Now, "attempt", $"第 {task.Runtime.QueueRound} 轮 / 线程 {index + 1} / 第 {lane.Attempts} 次（发送前计数）", index + 1);
                        });
                        laneToken.ThrowIfCancellationRequested();
                        var result = await probes.ProbeAsync(active.Snapshot, !queue, actualPrompt, status => AcceptAsync(index, status), laneToken);
                        if (result.Error is not null) result = result with { Error = TextOutput.Summary(result.Error) };
                        if (result.Kind == ProbeKind.Accepted) return result;
                        if (result.Kind == ProbeKind.Lost) return result;
                        await decision.WaitAsync(token);
                        try
                        {
                            if (winner >= 0 || terminal is not null) return new ProbeResult(ProbeKind.Lost);
                            await ChangeAsync(active, (document, task) =>
                            {
                                task.Runtime.LastHttpStatus = result.Status;
                                task.Runtime.LastError = result.Error;
                                task.Runtime.MatchedRule = result.Kind == ProbeKind.Busy ? $"HTTP {result.Status}" : result.Kind.ToString();
                                task.Runtime.Lanes[index].LastError = result.Error;
                                TaskStore.AddEvent(document, task, clock.Now, "request-failed", result.Error ?? result.Kind.ToString(), index + 1);
                            });
                        }
                        finally { decision.Release(); }
                        var retry = result.Kind == ProbeKind.Busy || result.Kind == ProbeKind.Network && active.Snapshot.Config.Queue.RetryNetworkErrors
                            || result.Kind == ProbeKind.Timeout && active.Snapshot.Config.Queue.RetryTimeouts;
                        if (result.Kind is ProbeKind.Permanent or ProbeKind.Protocol || queue && !retry || !queue && result.Kind == ProbeKind.Busy)
                        {
                            await decision.WaitAsync(token);
                            try
                            {
                                if (winner < 0 && terminal is null)
                                {
                                    terminal = result with { Kind = !queue && result.Kind == ProbeKind.Busy ? ProbeKind.Busy : ProbeKind.Permanent };
                                    for (var j = 0; j < sources.Count; j++) if (j != index) await sources[j].CancelAsync();
                                }
                            }
                            finally { decision.Release(); }
                            return result;
                        }
                        if (!queue) return result;
                        var used = (await CurrentAsync(active, laneToken)).Runtime.Lanes[index].Attempts;
                        if (active.Snapshot.Config.Queue.MaxAttemptsPerLane is { } maximum && used >= maximum) continue;
                        await WaitAsync(active, index, Math.Max(Jitter(active.Snapshot.Config.Queue.IntervalSeconds, active.Snapshot.Config.Queue.IntervalMaxSeconds), result.RetryAfterSeconds), laneToken);
                    }
                }
                catch (OperationCanceledException) when (laneToken.IsCancellationRequested) { return new ProbeResult(ProbeKind.Lost); }
                catch
                {
                    // 任一子线程异常必须停止同行；否则无限预算的同行会使 WhenAll 永不退出。
                    foreach (var source in sources) await source.CancelAsync();
                    throw;
                }
            }
            // WhenAll 覆盖每条请求的释放；首胜只取消其他线程，不取消获胜者的完整响应读取。
            var results = await Task.WhenAll(Enumerable.Range(0, count).Select(LaneAsync));
            token.ThrowIfCancellationRequested();
            if (terminal is not null) return terminal;
            if (winner >= 0) return results[winner];
            if (queue || results.All(r => r.Error == "budget_exhausted")) return new ProbeResult(ProbeKind.Lost, Error: "budget_exhausted");
            return results.FirstOrDefault(r => r.Kind == ProbeKind.Busy)
                ?? results.FirstOrDefault(r => r.Kind != ProbeKind.Lost) ?? new ProbeResult(ProbeKind.Network, Error: "心跳未得到确认");
        }
        finally { foreach (var source in sources) source.Dispose(); }
    }

    private async Task<bool> RequeueAsync(ActiveRun active, string reason)
    {
        if (!active.Snapshot.Config.AutoRequeue)
        { await StopWithAsync(active, "paused", $"{reason}；自动重新排队已关闭", "paused"); return false; }
        await ChangeAsync(active, (document, task) =>
        {
            task.Runtime.Phase = "queue"; task.Runtime.Status = "running"; task.Runtime.QueueRound++;
            task.Runtime.SessionConfirmed = false;
            task.Runtime.Lanes.Clear(); task.Runtime.NextAttemptAt = null;
            task.Runtime.QueueLaneAttempts.Clear();
            TaskStore.AddEvent(document, task, clock.Now, "requeue", $"{reason}；开始新的排队轮次，累计次数保留");
        });
        return true;
    }
    private Task StopWithAsync(ActiveRun active, string status, string reason, string? notification = null)
        => ChangeAsync(active, (document, task) =>
        {
            task.Runtime.Status = status; task.Runtime.PauseReason = reason; task.Runtime.LastError = reason;
            task.Runtime.SessionConfirmed = false;
            TaskStore.AddEvent(document, task, clock.Now, notification ?? status, reason);
            TaskStore.Notify(document, task, clock.Now, notification ?? status, reason);
        });
    private async Task WaitAsync(ActiveRun active, int? lane, double seconds, CancellationToken token)
    {
        var until = clock.Now.AddSeconds(seconds);
        await ChangeAsync(active, (_, task) =>
        {
            if (lane is { } index) { task.Runtime.Lanes[index].NextAttemptAt = until; task.Runtime.Lanes[index].Status = "waiting"; }
            task.Runtime.NextAttemptAt = lane is null ? until : task.Runtime.Lanes.Where(l => l.NextAttemptAt is not null).Select(l => l.NextAttemptAt).Min();
        });
        while (clock.Now < until)
            await clock.Delay(TimeSpan.FromSeconds(Math.Min(86400, Math.Max(0.001, (until - clock.Now).TotalSeconds))), token);
    }
    private static double Jitter(double minimum, double? maximum) => maximum is { } max ? minimum + Random.Shared.NextDouble() * (max - minimum) : minimum;
    private async Task<TaskRecord> CurrentAsync(ActiveRun active, CancellationToken token = default)
    {
        var task = TaskStore.Find(await store.ReadAsync(token), active.Snapshot.Config.Id);
        if (task.Runtime.Generation != active.Snapshot.Generation) throw new OperationCanceledException("旧代际结果被丢弃", active.Stop.Token);
        return task;
    }
    private async Task ChangeAsync(ActiveRun active, Action<StoreDocument, TaskRecord> change)
    {
        var changed = await store.UpdateAsync(document =>
        {
            var task = TaskStore.Find(document, active.Snapshot.Config.Id);
            if (task.Runtime.Generation != active.Snapshot.Generation) return false;
            change(document, task); task.Runtime.UpdatedAt = clock.Now; return true;
        });
        if (!changed) throw new OperationCanceledException("运行代次已改变", active.Stop.Token);
    }
    public async ValueTask DisposeAsync()
    {
        await _commands.WaitAsync();
        try
        {
            if (_stopped) return; _stopped = true;
            var running = _runs.Values.ToArray();
            await _lifetime.CancelAsync();
            await Task.WhenAll(running.Select(run => run.Work));
        }
        finally { _commands.Release(); }
    }
    private sealed class ActiveRun(RunSnapshot snapshot, CancellationTokenSource stop)
    {
        public RunSnapshot Snapshot { get; } = snapshot;
        public CancellationTokenSource Stop { get; } = stop;
        public string StopReason { get; set; } = "paused";
        public Task Work { get; set; } = Task.CompletedTask;
        public string? Failure { get; set; }
        public async Task CancelAsync()
        {
            try { await Stop.CancelAsync(); }
            catch (ObjectDisposedException) { /* 自然完成恰好与用户暂停并发；Work 仍需等待。 */ }
        }
    }
}
