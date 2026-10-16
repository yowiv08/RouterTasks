using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugins.RouterTasks;
using Router.Contracts.Host;

namespace RouterTasks.Tests;

[TestClass]
public sealed class ModeTransitionTests
{
    private static async Task<TaskRecord> SaveConfig(Fixture f, string endpoint, TaskConfig config)
    {
        var document = await f.Plugin.Store.ReadAsync();
        var previous = document.Tasks.Find(t => t.Config.Id == config.Id);
        PluginResult result;
        if (endpoint == "accounts")
        {
            SaveAccountInput input;
            if (previous is null)
                input = new(config, f.Site, "TOKEN-new-account", Secrets: new(CredentialVersion: 0));
            else
            {
                var detail = JsonData.Read<AccountDetail>((await f.Plugin.AccountDetailAsync(Fixture.Context(id: config.Id))).Body);
                input = new(config with { Settings = config.Settings ?? detail.Config.Settings }, detail.Site, detail.Token,
                    detail.CredentialVersion, detail.ConfigRevision, detail.Secrets);
            }
            result = await f.Plugin.SaveAccountAsync(Fixture.Context(input));
        }
        else if (endpoint == "tasks")
            result = await f.Plugin.SaveTaskAsync(Fixture.Context(new SaveTaskInput(config, previous?.ConfigRevision)));
        else
            result = await f.Plugin.ImportAsync(Fixture.Context(new ConfigExport(1, document.Sites, document.Templates, [config], document.Settings)));
        Assert.AreEqual(200, result.StatusCode, JsonSerializer.Serialize(result.Body));
        return TaskStore.Find(await f.Plugin.Store.ReadAsync(), config.Id);
    }

    private static void AssertFreshRun(TaskRecord task, TaskRecord previous, string mode)
    {
        Assert.AreNotEqual(previous.Runtime.RunId, task.Runtime.RunId);
        Assert.AreEqual("paused", task.Runtime.Status);
        Assert.AreEqual(mode == "keepalive" ? "keepalive" : "queue", task.Runtime.Phase);
        Assert.IsFalse(task.Runtime.Entered);
        Assert.AreEqual("none", task.Runtime.ResponseState);
        Assert.IsNull(task.Runtime.StartedAt);
        Assert.IsNull(task.Snapshot);
        Assert.AreEqual(0L, task.Runtime.TotalAttempts);
        Assert.HasCount(0, task.Runtime.QueueLaneAttempts);
        Assert.HasCount(0, task.Runtime.KeepaliveLaneAttempts);
        CollectionAssert.IsSubsetOf(previous.Events.ToArray(), task.Events.ToArray());
    }

    [TestMethod]
    [DataRow("accounts", "keepalive", "queue")]
    [DataRow("tasks", "keepalive", "queue")]
    [DataRow("import", "keepalive", "queue")]
    [DataRow("accounts", "queue", "keepalive")]
    [DataRow("tasks", "queue", "keepalive")]
    [DataRow("import", "queue", "keepalive")]
    public async Task SwitchingAcceptedOrCompletedTaskAllowsContinuation(string endpoint, string oldMode, string newMode)
    {
        var clock = new ManualClock();
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)));
        await using var f = new Fixture(probe, runtimeClock: clock); f.Config.Mode = oldMode;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start");
        if (oldMode == "keepalive")
        {
            await Fixture.Until(() => Task.FromResult(clock.Pending == 1));
            await f.Plugin.Engine.ActionAsync("task", "pause");
        }
        else await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        await f.Plugin.Store.UpdateAsync(d =>
        {
            d.Notifications.Add(new NotificationItem { Id = "historical-notice", TaskId = "task", State = "sent", CreatedAt = clock.Now });
            return true;
        });
        var previous = await f.TaskAsync();
        Assert.IsTrue(previous.Runtime.Entered); Assert.AreEqual(1, probe.Calls);
        var saved = await SaveConfig(f, endpoint, previous.Config with { Mode = newMode });
        AssertFreshRun(saved, previous, newMode);
        var afterSave = await f.Plugin.Store.ReadAsync();
        Assert.AreEqual(1L, afterSave.Stats.Sum(s => s.Entered));
        Assert.IsTrue(afterSave.Notifications.Any(n => n.Id == "historical-notice"));
        await f.Plugin.Engine.ActionAsync("task", "resume");
        if (newMode == "keepalive")
        {
            await Fixture.Until(() => Task.FromResult(clock.Pending == 1));
            await f.Plugin.Engine.ActionAsync("task", "pause");
        }
        else await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var continued = await f.TaskAsync();
        Assert.AreEqual(2, probe.Calls); Assert.AreEqual(1L, continued.Runtime.TotalAttempts);
        Assert.AreEqual(newMode, continued.Snapshot!.Config.Mode);
        Assert.AreEqual(newMode == "queue" ? "completed" : "paused", continued.Runtime.Status);
        Assert.AreEqual(2L, (await f.Plugin.Store.ReadAsync()).Stats.Sum(s => s.Entered));
        CollectionAssert.AreEqual(new[] { oldMode == "keepalive", newMode == "keepalive" }, probe.Phases.ToArray());
    }

    [TestMethod]
    [DataRow("accounts")]
    [DataRow("tasks")]
    [DataRow("import")]
    public async Task SwitchingBackToExhaustedKeepaliveGetsFreshPhaseBudgets(string endpoint)
    {
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)));
        await using var f = new Fixture(probe); f.Config.Mode = "keepalive";
        f.Config.Queue.MaxAttemptsPerLane = 1; f.Config.Keepalive.MaxAttemptsPerLane = 1;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var first = await f.TaskAsync(); Assert.AreEqual("exhausted", first.Runtime.Status);
        await SaveConfig(f, endpoint, first.Config with { Mode = "queue" });
        await f.Plugin.Engine.ActionAsync("task", "resume");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var second = await f.TaskAsync(); Assert.AreEqual("completed", second.Runtime.Status);
        var third = await SaveConfig(f, endpoint, second.Config with { Mode = "keepalive" });
        AssertFreshRun(third, second, "keepalive"); Assert.AreNotEqual(first.Runtime.RunId, third.Runtime.RunId);
        await f.Plugin.Engine.ActionAsync("task", "resume");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var final = await f.TaskAsync();
        Assert.AreEqual(3, probe.Calls); Assert.AreEqual("exhausted", final.Runtime.Status);
        Assert.AreEqual(1L, final.Runtime.TotalAttempts); Assert.AreEqual(1L, final.Runtime.KeepaliveLaneAttempts[1]);
        Assert.HasCount(0, final.Runtime.QueueLaneAttempts);
        CollectionAssert.AreEqual(new[] { true, false, true }, probe.Phases.ToArray());
    }

    [TestMethod]
    [DataRow("accounts", "queue")]
    [DataRow("accounts", "keepalive")]
    [DataRow("accounts", "queue_then_keepalive")]
    [DataRow("tasks", "queue")]
    [DataRow("tasks", "keepalive")]
    [DataRow("tasks", "queue_then_keepalive")]
    [DataRow("import", "queue")]
    [DataRow("import", "keepalive")]
    [DataRow("import", "queue_then_keepalive")]
    public async Task SameModeEditAndResumeKeepConsumedAttemptBudget(string endpoint, string mode)
    {
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500)));
        await using var f = new Fixture(probe); f.Config.Mode = mode;
        f.Config.Queue.MaxAttemptsPerLane = 1; f.Config.Keepalive.MaxAttemptsPerLane = 1;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var previous = await f.TaskAsync(); Assert.AreEqual("exhausted", previous.Runtime.Status);
        var saved = await SaveConfig(f, endpoint, previous.Config with { Name = "Updated account" });
        Assert.AreEqual(previous.Runtime.RunId, saved.Runtime.RunId);
        Assert.AreEqual(previous.Runtime.StartedAt, saved.Runtime.StartedAt); Assert.IsNotNull(saved.Snapshot);
        Assert.AreEqual(1L, saved.Runtime.TotalAttempts);
        await f.Plugin.Engine.ActionAsync("task", "resume");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var continued = await f.TaskAsync();
        Assert.AreEqual(1, probe.Calls); Assert.AreEqual("exhausted", continued.Runtime.Status);
        Assert.AreEqual(previous.Runtime.RunId, continued.Runtime.RunId); Assert.AreEqual(1L, continued.Runtime.TotalAttempts);
    }

    [TestMethod]
    [DataRow("accounts")]
    [DataRow("tasks")]
    [DataRow("import")]
    public async Task SameQueueModeEditKeepsAcceptedRequestReplayProtection(string endpoint)
    {
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)));
        await using var f = new Fixture(probe); f.Config.Mode = "queue";
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var previous = await f.TaskAsync();
        await SaveConfig(f, endpoint, previous.Config with { Name = "Updated account" });
        await Assert.ThrowsExactlyAsync<ConflictException>(() => f.Plugin.Engine.ActionAsync("task", "resume"));
        Assert.AreEqual(1, probe.Calls); Assert.IsTrue((await f.TaskAsync()).Runtime.Entered);
        await f.Plugin.Engine.ActionAsync("task", "restart");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        Assert.AreEqual(2, probe.Calls); Assert.AreNotEqual(previous.Runtime.RunId, (await f.TaskAsync()).Runtime.RunId);
    }

    [TestMethod]
    [DataRow("accounts", "queue")]
    [DataRow("accounts", "keepalive")]
    [DataRow("accounts", "queue_then_keepalive")]
    [DataRow("tasks", "queue")]
    [DataRow("tasks", "keepalive")]
    [DataRow("tasks", "queue_then_keepalive")]
    [DataRow("import", "queue")]
    [DataRow("import", "keepalive")]
    [DataRow("import", "queue_then_keepalive")]
    public async Task NewlySavedTasksResumeInTheConfiguredInitialPhase(string endpoint, string mode)
    {
        var probe = new ScriptProbe((_, _, token) => ScriptProbe.Block(token));
        await using var f = new Fixture(probe); await f.SeedAsync();
        var saved = await SaveConfig(f, endpoint, f.Config with { Id = "new-task", Mode = mode });
        Assert.AreEqual("paused", saved.Runtime.Status);
        Assert.AreEqual(mode == "keepalive" ? "keepalive" : "queue", saved.Runtime.Phase);
        await f.Plugin.Engine.ActionAsync("new-task", "resume");
        await Fixture.Until(() => Task.FromResult(probe.Calls == 1));
        await f.Plugin.Engine.ActionAsync("new-task", "pause");
        var continued = TaskStore.Find(await f.Plugin.Store.ReadAsync(), "new-task");
        Assert.AreEqual(1L, continued.Runtime.TotalAttempts);
        Assert.AreEqual(saved.Runtime.RunId, continued.Runtime.RunId);
        CollectionAssert.AreEqual(new[] { mode == "keepalive" }, probe.Phases.ToArray());
    }

    [TestMethod]
    [DataRow("keepalive", "queue")]
    [DataRow("queue", "keepalive")]
    [DataRow("queue_then_keepalive", "keepalive")]
    public async Task LoadingLegacyModeMismatchRepairsStateBeforeContinuation(string oldMode, string newMode)
    {
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)));
        await using var f = new Fixture(probe); f.Config.Mode = oldMode;
        f.Config.Queue.MaxAttemptsPerLane = 1; f.Config.Keepalive.MaxAttemptsPerLane = 1;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var previous = await f.TaskAsync(); var priorCalls = probe.Calls;
        // 重现旧版保存逻辑：阶段和当前配置更新，旧快照、接受状态和两阶段预算未清理。
        await f.Plugin.Store.UpdateAsync(d =>
        {
            var task = d.Tasks[0]; task.Config.Mode = newMode;
            task.Runtime.Phase = newMode == "keepalive" ? "keepalive" : "queue"; task.Runtime.Lanes.Clear();
            return true;
        });
        await f.Plugin.StopAsync(default);
        await using var restored = new Fixture(probe, directory: f.Directory, runtimeClock: f.Clock);
        var migrated = await restored.TaskAsync(); AssertFreshRun(migrated, previous, newMode);
        Assert.AreEqual(priorCalls, probe.Calls, "加载和迁移不能自动发送请求");
        await restored.Plugin.Engine.ActionAsync("task", "resume");
        await Fixture.Until(() => Task.FromResult(restored.Plugin.Engine.ActiveCount == 0));
        var continued = await restored.TaskAsync();
        Assert.AreEqual(priorCalls + 1, probe.Calls); Assert.AreEqual(1L, continued.Runtime.TotalAttempts);
        Assert.AreEqual(newMode == "queue" ? "completed" : "exhausted", continued.Runtime.Status);
    }

    [TestMethod]
    public async Task LoadingUnchangedModeAndFailedResolveKeepTheExistingRun()
    {
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500)));
        await using var f = new Fixture(probe); f.Config.Mode = "keepalive"; f.Config.Keepalive.MaxAttemptsPerLane = 1;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var previous = await f.TaskAsync(); await f.Plugin.StopAsync(default);
        await using var restored = new Fixture(probe, directory: f.Directory, runtimeClock: f.Clock);
        var loaded = await restored.TaskAsync();
        Assert.AreEqual(previous.Runtime.RunId, loaded.Runtime.RunId);
        Assert.AreEqual(1L, loaded.Runtime.KeepaliveLaneAttempts[1]);
        restored.AccountData.TryRemove("key", out var key);
        var beforeResolve = JsonSerializer.Serialize(loaded, JsonData.Options);
        await Assert.ThrowsExactlyAsync<FormatException>(() => restored.Plugin.Engine.ActionAsync("task", "resume"));
        Assert.AreEqual(beforeResolve, JsonSerializer.Serialize(await restored.TaskAsync(), JsonData.Options));
        restored.AccountData["key"] = key!;
        await restored.Plugin.Engine.ActionAsync("task", "resume");
        await Fixture.Until(() => Task.FromResult(restored.Plugin.Engine.ActiveCount == 0));
        Assert.AreEqual(1, probe.Calls); Assert.AreEqual("exhausted", (await restored.TaskAsync()).Runtime.Status);
    }
}
