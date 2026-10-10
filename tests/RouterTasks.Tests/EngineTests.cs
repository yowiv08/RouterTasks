using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugins.RouterTasks;

namespace RouterTasks.Tests;

[TestClass]
public sealed class EngineTests
{
    [TestMethod]
    public async Task HybridQueuesThenKeepsAliveAndRequeuesWithFreshBudget()
    {
        var probe = new ScriptProbe((n, _, token) => n switch
        {
            1 => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500, "HTTP 500")),
            2 => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)),
            3 => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500, "HTTP 500")),
            4 => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)),
            _ => ScriptProbe.Block(token)
        });
        await using var fixture = new Fixture(probe); fixture.Config.Queue.MaxAttemptsPerLane = 2;
        await fixture.SeedAsync(); await fixture.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(async () => (await fixture.TaskAsync()).Runtime.Recoveries == 1 && probe.Calls >= 5);
        var task = await fixture.TaskAsync();
        CollectionAssert.AreEqual(new[] { false, false, true, false, true }, probe.Phases.Take(5).ToArray());
        Assert.AreEqual(2, task.Runtime.QueueRound); Assert.IsGreaterThanOrEqualTo(5L, task.Runtime.TotalAttempts);
        Assert.AreEqual("keepalive", task.Runtime.Phase); Assert.AreNotEqual("completed", task.Runtime.Status);
        await fixture.Plugin.Engine.ActionAsync("task", "pause"); Assert.AreEqual(0, fixture.Plugin.Engine.ActiveCount);
    }

    [TestMethod]
    public async Task KeepaliveStartsWithHeartbeatAndDisabledRequeuePausesWithoutAnotherRequest()
    {
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500, "HTTP 500")));
        await using var fixture = new Fixture(probe); fixture.Config.Mode = "keepalive"; fixture.Config.AutoRequeue = false;
        await fixture.SeedAsync(); await fixture.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(fixture.Plugin.Engine.ActiveCount == 0));
        var task = await fixture.TaskAsync(); Assert.AreEqual("paused", task.Runtime.Status); Assert.IsFalse(task.Runtime.Entered);
        Assert.AreEqual(1, probe.Calls); Assert.IsTrue(probe.Phases.Single()); StringAssert.Contains(task.Runtime.PauseReason!, "已关闭");
    }

    [TestMethod]
    public async Task ManualRequeueWorksWithAutomaticSwitchOff()
    {
        var probe = new ScriptProbe((n, _, token) => n switch { 1 => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500)),
            2 => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)), _ => ScriptProbe.Block(token) });
        await using var f = new Fixture(probe); f.Config.Mode = "keepalive"; f.Config.AutoRequeue = false;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        await f.Plugin.Engine.ActionAsync("task", "requeue"); await Fixture.Until(() => Task.FromResult(probe.Calls >= 3));
        CollectionAssert.AreEqual(new[] { true, false, true }, probe.Phases.Take(3).ToArray());
    }

    [TestMethod]
    public async Task EveryQueueLaneHasItsOwnBudgetAndExhaustionDoesNotReset()
    {
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500)));
        await using var f = new Fixture(probe); f.Config.Queue.Concurrency = 3; f.Config.Queue.MaxAttemptsPerLane = 2;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var task = await f.TaskAsync(); Assert.AreEqual("exhausted", task.Runtime.Status); Assert.AreEqual(6, probe.Calls);
        Assert.AreEqual(6L, task.Runtime.TotalAttempts); Assert.IsTrue(task.Runtime.Lanes.All(l => l.Attempts == 2)); Assert.AreEqual(1, task.Runtime.QueueRound);
    }

    [TestMethod]
    public async Task SimultaneousSuccessCountsOnceAndStopsPeers()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var arrived = 0;
        var probe = new ScriptProbe(async (_, _, token) => { if (Interlocked.Increment(ref arrived) == 8) barrier.SetResult(); await barrier.Task.WaitAsync(token); return new ProbeResult(ProbeKind.Accepted, 200); });
        await using var f = new Fixture(probe); f.Config.Mode = "queue"; f.Config.Queue.Concurrency = 8;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var task = await f.TaskAsync(); Assert.AreEqual("completed", task.Runtime.Status);
        Assert.HasCount(1, task.Events.Where(e => e.Kind == "accepted").ToList());
        Assert.AreEqual(1L, (await f.Plugin.Store.ReadAsync()).Stats.Sum(s => s.Entered));
    }

    [TestMethod]
    public async Task ConfirmedNetworkOutageRequeuesButShortFailureDoesNot()
    {
        Fixture? f = null;
        var probe = new ScriptProbe((n, _, token) =>
        {
            if (n == 1) return Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200));
            if (n == 2) return Task.FromResult(new ProbeResult(ProbeKind.Network, Error: "short interruption"));
            if (n == 3) { f!.Clock.Advance(500); return Task.FromResult(new ProbeResult(ProbeKind.Timeout)); }
            return ScriptProbe.Block(token);
        });
        await using var fixture = f = new Fixture(probe); f.Config.Mode = "keepalive";
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(probe.Calls >= 4));
        CollectionAssert.AreEqual(new[] { true, true, true, false }, probe.Phases.Take(4).ToArray());
        Assert.HasCount(1, (await f.TaskAsync()).Events.Where(e => e.Kind == "dropped").ToList());
    }

    [TestMethod]
    public async Task PauseCancelsInflightAndResumeDoesNotRefundAttempts()
    {
        var probe = new ScriptProbe((_, _, token) => ScriptProbe.Block(token));
        await using var f = new Fixture(probe); await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(probe.Calls == 1)); await f.Plugin.Engine.ActionAsync("task", "pause");
        Assert.AreEqual(1L, (await f.TaskAsync()).Runtime.TotalAttempts);
        await f.Plugin.Engine.ActionAsync("task", "resume"); await Fixture.Until(() => Task.FromResult(probe.Calls == 2));
        Assert.AreEqual(2L, (await f.TaskAsync()).Runtime.TotalAttempts);
        await f.Plugin.StopAsync(CancellationToken.None); Assert.AreEqual(0, f.Plugin.Engine.ActiveCount);
    }

    [TestMethod]
    public async Task FatalLaneFailureCancelsAnUnlimitedPeer()
    {
        var probe = new ScriptProbe((n, _, token) => n == 1 ? Task.FromException<ProbeResult>(new InvalidOperationException("unexpected probe failure")) : ScriptProbe.Block(token));
        await using var f = new Fixture(probe); f.Config.Queue.Concurrency = 3; f.Config.Queue.MaxAttemptsPerLane = null;
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        Assert.AreEqual("failed", (await f.TaskAsync()).Runtime.Status);
    }

    [TestMethod]
    public async Task RetryAfterIsRespected()
    {
        DateTimeOffset? first = null, second = null; Fixture? f = null;
        var probe = new ScriptProbe((n, _, _) => { if (n == 1) { first = f!.Clock.Now; return Task.FromResult(new ProbeResult(ProbeKind.Busy, 500, RetryAfterSeconds: 90)); }
            second = f!.Clock.Now; return Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)); });
        await using var fixture = f = new Fixture(probe); f.Config.Mode = "queue";
        await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        Assert.IsTrue(second - first >= TimeSpan.FromSeconds(90));
    }
}
