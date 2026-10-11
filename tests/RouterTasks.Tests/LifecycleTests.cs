using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugins.RouterTasks;

namespace RouterTasks.Tests;

[TestClass]
public sealed class LifecycleTests
{
    [TestMethod]
    public async Task ChangingConcurrencyDoesNotRefundRemovedLanes()
    {
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500)));
        await using var f = new Fixture(probe); f.Config.Queue.Concurrency = 3; f.Config.Queue.MaxAttemptsPerLane = 1;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        await f.Plugin.Store.UpdateAsync(d => { d.Tasks[0].Config.Queue.Concurrency = 1; return true; });
        await f.Plugin.Engine.ActionAsync("task", "resume"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        await f.Plugin.Store.UpdateAsync(d => { d.Tasks[0].Config.Queue.Concurrency = 3; return true; });
        await f.Plugin.Engine.ActionAsync("task", "resume"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        Assert.AreEqual(3, probe.Calls); Assert.AreEqual(3L, (await f.TaskAsync()).Runtime.TotalAttempts);
    }

    [TestMethod]
    public async Task SourcePromptIsOptInAndFailuresUseCachedOrOriginalPrompt()
    {
        var calls = 0;
        await using var f = new Fixture(handler: (_, _) => { calls++; return Task.FromResult(Fixture.Response(503, "unavailable", "text/plain")); });
        await f.SeedAsync(); var source = new SourcePromptService(f.Plugin.Store, f.Http.Object, f.Clock);
        var run = await f.Plugin.ResolveAsync(f.Config, default);
        Assert.AreEqual(f.Config.Prompt, await source.GetAsync(run, default)); Assert.AreEqual(0, calls);
        run.Config.UseSourcePrompt = true;
        Assert.AreEqual(f.Config.Prompt, await source.GetAsync(run, default)); Assert.AreEqual(3, calls);
        await source.GetAsync(run, default); Assert.AreEqual(3, calls);
        await f.Plugin.Store.UpdateAsync(d => { d.SourceSnippet = "int main(void) { return 0; }"; d.SourceUpdatedAt = f.Clock.Now; return true; });
        StringAssert.Contains(await source.GetAsync(run, default), "int main"); Assert.AreEqual(3, calls);
    }
    [TestMethod]
    public async Task KeepaliveBudgetIsIndependentAndResumeDoesNotRefundIt()
    {
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)));
        await using var f = new Fixture(probe); f.Config.Mode = "keepalive"; f.Config.Keepalive.MaxAttemptsPerLane = 2;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        Assert.AreEqual("exhausted", (await f.TaskAsync()).Runtime.Status); Assert.AreEqual(2, probe.Calls);
        await f.Plugin.Engine.ActionAsync("task", "resume"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        Assert.AreEqual(2, probe.Calls); Assert.AreEqual(0, (await f.TaskAsync()).Runtime.QueueRound);
    }
    [TestMethod]
    public async Task BusyHeartbeatPreservesAnAcceptingPeer()
    {
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSuccess = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ScriptProbe(async (n, _, token) =>
        {
            if (n == 2)
            {
                allStarted.TrySetResult(); await releaseSuccess.Task.WaitAsync(token);
                return new ProbeResult(ProbeKind.Accepted, 200);
            }
            await allStarted.Task.WaitAsync(token); return new ProbeResult(ProbeKind.Busy, 500);
        });
        await using var f = new Fixture(probe); f.Config.Mode = "keepalive"; f.Config.AutoRequeue = false; f.Config.Keepalive.Concurrency = 2;
        f.Config.Keepalive.MaxAttemptsPerLane = 1;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(async () => (await f.TaskAsync()).Events.Any(e => e.Kind == "request-failed"));
        Assert.AreEqual(1, f.Plugin.Engine.ActiveCount); releaseSuccess.SetResult();
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        Assert.AreEqual(2, probe.Calls); Assert.IsTrue((await f.TaskAsync()).Runtime.Entered);
        Assert.AreEqual(0L, (await f.TaskAsync()).Runtime.Recoveries);
    }

    [TestMethod]
    public async Task ControlledClockSeparatesLostContactAndConfirmedOffline()
    {
        var clock = new ManualClock();
        var probe = new ScriptProbe((n, _, _) => Task.FromResult(new ProbeResult(n == 1 ? ProbeKind.Accepted : ProbeKind.Network, n == 1 ? 200 : null)));
        await using var f = new Fixture(probe, runtimeClock: clock); f.Config.Mode = "keepalive"; f.Config.AutoRequeue = false;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(clock.Pending == 1));
        clock.Advance(120); await Fixture.Until(async () => (await f.TaskAsync()).Runtime.FailureSince is not null && clock.Pending == 1);
        Assert.AreEqual("running", (await f.TaskAsync()).Runtime.Status);
        clock.Advance(180); await Fixture.Until(async () => (await f.TaskAsync()).Runtime.Status == "recovering" && clock.Pending == 1);
        Assert.IsFalse((await f.TaskAsync()).Runtime.SessionConfirmed);
        clock.Advance(120); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var task = await f.TaskAsync(); Assert.AreEqual("paused", task.Runtime.Status); Assert.AreEqual(4, probe.Calls);
        Assert.HasCount(1, task.Events.Where(e => e.Kind == "dropped").ToArray());
        clock.Advance(10000); Assert.AreEqual(4, probe.Calls);
    }

    [TestMethod]
    public async Task MultipleTransientRecoveriesOnlyRecordLogs()
    {
        var probe = new ScriptProbe((n, _, token) => n <= 5
            ? Task.FromResult(new ProbeResult(n % 2 == 1 ? ProbeKind.Accepted : ProbeKind.Network, n % 2 == 1 ? 200 : null)) : ScriptProbe.Block(token));
        await using var f = new Fixture(probe); f.Config.Mode = "keepalive"; f.Config.NotificationEvents = ["recovered"];
        await f.SeedAsync(); await f.Plugin.Store.UpdateAsync(d => { d.Settings.TelegramEnabled = true; return true; });
        await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(probe.Calls == 6));
        var doc = await f.Plugin.Store.ReadAsync(); var task = doc.Tasks.Single();
        Assert.AreEqual(0, task.Runtime.QueueRound); Assert.AreEqual(0L, task.Runtime.Recoveries);
        Assert.AreEqual(0L, task.Runtime.OutageSequence); Assert.HasCount(0, doc.Notifications);
        Assert.HasCount(2, task.Events.Where(e => e.Kind == "heartbeat-resumed").ToArray());
    }

    [TestMethod]
    public async Task HistoricalSuccessCannotConfirmANewSession()
    {
        var probe = new ScriptProbe((n, _, token) => n == 1 ? Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)) : ScriptProbe.Block(token));
        await using var f = new Fixture(probe); f.Config.Mode = "keepalive";
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(probe.Calls == 2));
        await f.Plugin.Engine.ActionAsync("task", "pause"); await f.Plugin.Engine.ActionAsync("task", "resume");
        await Fixture.Until(() => Task.FromResult(probe.Calls == 3));
        var task = await f.TaskAsync(); Assert.IsNotNull(task.Runtime.LastSuccessAt); Assert.IsFalse(task.Runtime.SessionConfirmed);
        Assert.AreEqual(1L, (await f.Plugin.Store.ReadAsync()).Stats.Sum(s => s.Entered));
    }

    [TestMethod]
    public async Task ExhaustedResumeDoesNotResetBudgetButManualRoundDoes()
    {
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500)));
        await using var f = new Fixture(probe); f.Config.Queue.MaxAttemptsPerLane = 1;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        await f.Plugin.Engine.ActionAsync("task", "resume"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        Assert.AreEqual(1, probe.Calls);
        await f.Plugin.Engine.ActionAsync("task", "requeue"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var task = await f.TaskAsync(); Assert.AreEqual(2, probe.Calls); Assert.AreEqual(2, task.Runtime.QueueRound); Assert.AreEqual(2L, task.Runtime.TotalAttempts);
    }

    [TestMethod]
    public async Task LateAcceptedResultCannotEscapeCancellationOrOverwriteNewGeneration()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ScriptProbe(async (n, _, _) => { if (n == 1) await release.Task; return new ProbeResult(ProbeKind.Accepted, 200); });
        await using var f = new Fixture(probe); f.Config.Mode = "queue"; await f.SeedAsync();
        await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(probe.Calls == 1));
        var pause = f.Plugin.Engine.ActionAsync("task", "pause");
        await Task.Delay(30); Assert.IsFalse(pause.IsCompleted, "暂停必须等待旧请求退出");
        release.SetResult(); await pause;
        Assert.IsFalse((await f.TaskAsync()).Runtime.Entered);
        await f.Plugin.Engine.ActionAsync("task", "resume"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        Assert.AreEqual("completed", (await f.TaskAsync()).Runtime.Status);
        Assert.AreEqual(1L, (await f.Plugin.Store.ReadAsync()).Stats.Sum(s => s.Entered));
    }
}
