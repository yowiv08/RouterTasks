using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugins.RouterTasks;

namespace RouterTasks.Tests;

[TestClass]
public sealed class NotificationPolicyTests
{
    private static async Task EnableAsync(Fixture f, params string[] events)
    {
        f.Config.NotificationEvents = events.Length > 0 ? events : ["entered", "dropped", "recovered", "stopped", "paused", "exhausted"];
        await f.SeedAsync();
        await f.Plugin.Store.UpdateAsync(d => { d.Settings.TelegramEnabled = true; return true; });
    }

    [TestMethod]
    [DataRow("Busy")]
    [DataRow("Network")]
    [DataRow("Timeout")]
    [DataRow("Transient")]
    [DataRow("Protocol")]
    public async Task ShortFailureAndRecoveryDoNotNotify(string failure)
    {
        var kind = Enum.Parse<ProbeKind>(failure);
        var probe = new ScriptProbe((n, _, token) => n switch
        {
            1 or 3 => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)),
            2 => Task.FromResult(new ProbeResult(kind, 500, "temporary failure")),
            _ => ScriptProbe.Block(token)
        });
        await using var f = new Fixture(probe); f.Config.Mode = "keepalive";
        await EnableAsync(f); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(probe.Calls == 4));
        var doc = await f.Plugin.Store.ReadAsync(); var state = doc.Tasks.Single().Runtime;
        CollectionAssert.AreEqual(new[] { "entered" }, doc.Notifications.Select(n => n.Kind).ToArray());
        Assert.AreEqual(0L, state.OutageSequence); Assert.AreEqual(0L, state.Recoveries);
        Assert.IsNull(state.DropPendingSince); Assert.IsNull(state.FailureSince); Assert.IsTrue(state.SessionConfirmed);
        Assert.AreEqual("keepalive", state.Phase);
    }

    [TestMethod]
    [DataRow(15)]
    [DataRow(120)]
    public async Task DelayedFailureStartsFullObservationAndSuccessClearsIt(int recoverySeconds)
    {
        var clock = new ManualClock();
        var probe = new ScriptProbe((n, _, token) => n switch
        {
            1 or 3 => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)),
            2 => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500)),
            _ => ScriptProbe.Block(token)
        });
        await using var f = new Fixture(probe, runtimeClock: clock); f.Config.Mode = "keepalive";
        await EnableAsync(f); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(clock.Pending == 1));
        clock.Advance(1000);
        await Fixture.Until(async () => (await f.TaskAsync()).Runtime.DropPendingSince is not null && clock.Pending == 1);
        var task = await f.TaskAsync();
        Assert.AreEqual(clock.Now, task.Runtime.DropPendingSince); Assert.IsFalse(task.Runtime.OutageNotified);
        Assert.AreEqual("recovering", task.Runtime.Status);
        Assert.HasCount(1, (await f.Plugin.Store.ReadAsync()).Notifications);
        clock.Advance(recoverySeconds);
        await Fixture.Until(async () => (await f.TaskAsync()).Runtime.Heartbeats == 2 && clock.Pending == 1);
        task = await f.TaskAsync(); Assert.IsNull(task.Runtime.DropPendingSince);
        Assert.AreEqual(0L, task.Runtime.Recoveries); Assert.AreEqual(0L, task.Runtime.OutageSequence);
        Assert.HasCount(1, (await f.Plugin.Store.ReadAsync()).Notifications);
    }

    [TestMethod]
    public async Task ConfirmedOutageAndRecoveryNotifyOncePerIncident()
    {
        var clock = new ManualClock();
        var probe = new ScriptProbe((n, _, token) => n switch
        {
            1 or 6 => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)),
            2 or 5 => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500)),
            3 => Task.FromResult(new ProbeResult(ProbeKind.Network)),
            4 => Task.FromResult(new ProbeResult(ProbeKind.Timeout)),
            _ => ScriptProbe.Block(token)
        });
        await using var f = new Fixture(probe, runtimeClock: clock); f.Config.Mode = "keepalive";
        await EnableAsync(f); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(clock.Pending == 1));
        clock.Advance(300);
        await Fixture.Until(async () => (await f.TaskAsync()).Runtime.DropPendingSince is not null && clock.Pending == 1);
        clock.Advance(119);
        await Fixture.Until(() => Task.FromResult(probe.Calls == 3 && clock.Pending == 1));
        Assert.HasCount(1, (await f.Plugin.Store.ReadAsync()).Notifications);
        clock.Advance(15);
        await Fixture.Until(async () => (await f.TaskAsync()).Runtime.Phase == "queue" && clock.Pending == 1);
        var doc = await f.Plugin.Store.ReadAsync();
        CollectionAssert.AreEqual(new[] { "entered", "dropped" }, doc.Notifications.Select(n => n.Kind).ToArray());
        Assert.AreEqual(1L, doc.Tasks.Single().Runtime.OutageSequence);
        clock.Advance(10);
        await Fixture.Until(() => Task.FromResult(probe.Calls == 5 && clock.Pending == 1));
        Assert.HasCount(2, (await f.Plugin.Store.ReadAsync()).Notifications);
        clock.Advance(10);
        await Fixture.Until(async () => (await f.TaskAsync()).Runtime.Recoveries == 1 && clock.Pending == 1);
        doc = await f.Plugin.Store.ReadAsync();
        CollectionAssert.AreEqual(new[] { "entered", "dropped", "recovered" }, doc.Notifications.Select(n => n.Kind).ToArray());
        Assert.AreEqual(1L, doc.Tasks.Single().Runtime.Recoveries); Assert.IsFalse(doc.Tasks.Single().Runtime.OutageNotified);
        Assert.IsNull(doc.Tasks.Single().Runtime.DropPendingSince);
        CollectionAssert.AreEqual(new[] { true, true, true, true, false, false }, probe.Phases.ToArray());
        StringAssert.Contains(doc.Notifications[1].Title, "确认掉线");
        StringAssert.Contains(doc.Notifications[1].Message, "发生时间：");
        StringAssert.Contains(doc.Notifications[2].Title, "掉线后恢复");
    }

    [TestMethod]
    [DataRow(true, true, "dropped")]
    [DataRow(true, false, "dropped")]
    [DataRow(false, true, "paused")]
    [DataRow(false, false, "")]
    public async Task DropAndAutomaticPauseShareOneSelectedNotice(bool dropSelected, bool pauseSelected, string expected)
    {
        Fixture? owner = null;
        var probe = new ScriptProbe((n, _, _) =>
        {
            if (n == 2) owner!.Clock.Advance(180);
            if (n == 3) owner!.Clock.Advance(105);
            return Task.FromResult(new ProbeResult(n == 1 ? ProbeKind.Accepted : ProbeKind.Busy, n == 1 ? 200 : 500));
        });
        await using var f = owner = new Fixture(probe); f.Config.Mode = "keepalive"; f.Config.AutoRequeue = false;
        await EnableAsync(f);
        await f.Plugin.Store.UpdateAsync(d =>
        {
            d.Tasks[0].Config.NotificationEvents = new[] { dropSelected ? "dropped" : "", pauseSelected ? "paused" : "" }.Where(s => s.Length > 0).ToArray();
            return true;
        });
        await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var doc = await f.Plugin.Store.ReadAsync(); Assert.AreEqual("paused", doc.Tasks.Single().Runtime.Status);
        Assert.AreEqual(1L, doc.Tasks.Single().Runtime.OutageSequence); Assert.IsTrue(doc.Tasks.Single().Runtime.OutageNotified);
        Assert.HasCount(expected.Length > 0 ? 1 : 0, doc.Notifications);
        if (expected.Length > 0)
        {
            Assert.AreEqual(expected, doc.Notifications.Single().Kind);
            StringAssert.Contains(doc.Notifications.Single().Message, "任务自动暂停");
        }
    }

    [TestMethod]
    public async Task ConfirmedOutageSurvivesPauseAndRecoveryCanBeSelectedIndependently()
    {
        Fixture? owner = null;
        var probe = new ScriptProbe((n, _, token) =>
        {
            if (n == 2) owner!.Clock.Advance(180);
            if (n == 3) owner!.Clock.Advance(105);
            return n switch
            {
                1 or 4 => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)),
                2 or 3 => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500)),
                _ => ScriptProbe.Block(token)
            };
        });
        await using var f = owner = new Fixture(probe); f.Config.Mode = "keepalive"; f.Config.AutoRequeue = false;
        await EnableAsync(f, "recovered"); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        Assert.IsTrue((await f.TaskAsync()).Runtime.OutageNotified);
        Assert.HasCount(0, (await f.Plugin.Store.ReadAsync()).Notifications);
        await f.Plugin.Engine.ActionAsync("task", "resume"); await Fixture.Until(() => Task.FromResult(probe.Calls == 5));
        var doc = await f.Plugin.Store.ReadAsync();
        Assert.HasCount(1, doc.Notifications); Assert.AreEqual("recovered", doc.Notifications.Single().Kind);
        Assert.AreEqual(1L, doc.Tasks.Single().Runtime.Recoveries); Assert.AreEqual(1L, doc.Tasks.Single().Runtime.OutageSequence);
    }

    [TestMethod]
    public async Task ManualActionsStaySilentAndOnlyRestartRepeatsFirstEntry()
    {
        var clock = new ManualClock();
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)));
        await using var f = new Fixture(probe, runtimeClock: clock); f.Config.Mode = "keepalive";
        await EnableAsync(f); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(clock.Pending == 1));
        await f.Plugin.Engine.ActionAsync("task", "pause");
        await f.Plugin.Engine.ActionAsync("task", "resume");
        await Fixture.Until(() => Task.FromResult(probe.Calls == 2 && clock.Pending == 1));
        await f.Plugin.Engine.ActionAsync("task", "requeue");
        await Fixture.Until(() => Task.FromResult(probe.Calls == 3 && clock.Pending == 1));
        await f.Plugin.Engine.ActionAsync("task", "cancel");
        var doc = await f.Plugin.Store.ReadAsync();
        Assert.HasCount(1, doc.Notifications); Assert.AreEqual("entered", doc.Notifications.Single().Kind);
        Assert.AreEqual(0L, doc.Tasks.Single().Runtime.Recoveries);
        await f.Plugin.Engine.ActionAsync("task", "restart");
        await Fixture.Until(() => Task.FromResult(probe.Calls == 4 && clock.Pending == 1));
        doc = await f.Plugin.Store.ReadAsync();
        Assert.HasCount(2, doc.Notifications); Assert.IsTrue(doc.Notifications.All(n => n.Kind == "entered"));
        Assert.AreNotEqual(doc.Notifications[0].Id, doc.Notifications[1].Id);
    }

    [TestMethod]
    public async Task BudgetExhaustionRepeatsOnlyAfterNewRequests()
    {
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500)));
        await using var f = new Fixture(probe); f.Config.Mode = "queue"; f.Config.Queue.MaxAttemptsPerLane = 1;
        await EnableAsync(f); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        await f.Plugin.Engine.ActionAsync("task", "resume");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var doc = await f.Plugin.Store.ReadAsync();
        Assert.HasCount(1, doc.Notifications); Assert.AreEqual("exhausted", doc.Notifications.Single().Kind); Assert.AreEqual(1, probe.Calls);
        await f.Plugin.Store.UpdateAsync(d => { d.Tasks[0].Config.Queue.MaxAttemptsPerLane = 2; return true; });
        await f.Plugin.Engine.ActionAsync("task", "resume");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        doc = await f.Plugin.Store.ReadAsync(); Assert.HasCount(2, doc.Notifications); Assert.AreEqual(2, probe.Calls);
        Assert.IsTrue(doc.Notifications.All(n => n.Kind == "exhausted"));
    }

    [TestMethod]
    [DataRow("keepalive", "Network")]
    [DataRow("keepalive", "Timeout")]
    [DataRow("queue", "Network")]
    [DataRow("queue", "Timeout")]
    public async Task DisabledRetryPausesWithOneActionableNotice(string mode, string failure)
    {
        var kind = Enum.Parse<ProbeKind>(failure);
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(kind, Error: "failed")));
        await using var f = new Fixture(probe); f.Config.Mode = mode;
        f.Config.Keepalive.RetryNetworkErrors = f.Config.Queue.RetryNetworkErrors = false;
        f.Config.Keepalive.RetryTimeouts = f.Config.Queue.RetryTimeouts = false;
        await EnableAsync(f); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var doc = await f.Plugin.Store.ReadAsync();
        Assert.AreEqual("paused", doc.Tasks.Single().Runtime.Status); Assert.AreEqual(1, probe.Calls);
        Assert.HasCount(1, doc.Notifications); Assert.AreEqual("paused", doc.Notifications.Single().Kind);
    }

    [TestMethod]
    public async Task PermanentErrorStopsImmediatelyWithoutDropNotice()
    {
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Permanent, 401, "invalid token")));
        await using var f = new Fixture(probe); f.Config.Mode = "keepalive";
        await EnableAsync(f); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var doc = await f.Plugin.Store.ReadAsync();
        Assert.AreEqual("failed", doc.Tasks.Single().Runtime.Status); Assert.AreEqual(1, probe.Calls);
        Assert.HasCount(1, doc.Notifications); Assert.AreEqual("stopped", doc.Notifications.Single().Kind);
        await f.Plugin.Engine.ActionAsync("task", "resume");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        doc = await f.Plugin.Store.ReadAsync(); Assert.HasCount(2, doc.Notifications);
        Assert.IsTrue(doc.Notifications.All(n => n.Kind == "stopped"));
    }

    [TestMethod]
    public async Task ConcurrentFailuresRespectLongestRetryAfter()
    {
        var clock = new ManualClock();
        var probe = new ScriptProbe((n, _, _) => Task.FromResult(n == 1
            ? new ProbeResult(ProbeKind.Busy, 500) : new ProbeResult(ProbeKind.Transient, 503, RetryAfterSeconds: 90)));
        await using var f = new Fixture(probe, runtimeClock: clock); f.Config.Mode = "keepalive"; f.Config.Keepalive.Concurrency = 2;
        await EnableAsync(f); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(probe.Calls == 2 && clock.Pending == 1));
        Assert.AreEqual(clock.Now.AddSeconds(90), (await f.TaskAsync()).Runtime.NextAttemptAt);
        Assert.HasCount(0, (await f.Plugin.Store.ReadAsync()).Notifications);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartupFailureWithoutCurrentSuccessCannotBecomeAnOutage(bool historicalSuccess)
    {
        Fixture? owner = null;
        var probe = new ScriptProbe((_, _, _) =>
        {
            owner!.Clock.Advance(1000);
            return Task.FromResult(new ProbeResult(ProbeKind.Network));
        });
        await using var f = owner = new Fixture(probe); f.Config.Mode = "keepalive"; f.Config.Keepalive.MaxAttemptsPerLane = 1;
        await EnableAsync(f, "dropped", "recovered");
        await f.Plugin.Store.UpdateAsync(d =>
        {
            d.Tasks[0].Runtime.Entered = historicalSuccess;
            d.Tasks[0].Runtime.LastSuccessAt = historicalSuccess ? f.Clock.Now.AddHours(-1) : null;
            return true;
        });
        await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var doc = await f.Plugin.Store.ReadAsync();
        Assert.AreEqual(0L, doc.Tasks.Single().Runtime.OutageSequence); Assert.HasCount(0, doc.Notifications);
        Assert.AreEqual("exhausted", doc.Tasks.Single().Runtime.Status);
    }

    [TestMethod]
    public async Task ZeroConfirmationWindowConfirmsAtIdleThresholdAndRequeueHonorsRetryAfter()
    {
        var clock = new ManualClock();
        var probe = new ScriptProbe((n, _, token) => n switch
        {
            1 => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)),
            2 => Task.FromResult(new ProbeResult(ProbeKind.Busy, 500, RetryAfterSeconds: 90)),
            _ => ScriptProbe.Block(token)
        });
        await using var f = new Fixture(probe, runtimeClock: clock); f.Config.Mode = "keepalive"; f.Config.Keepalive.DropConfirmSeconds = 0;
        await EnableAsync(f, "dropped"); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(clock.Pending == 1));
        clock.Advance(300);
        await Fixture.Until(async () => (await f.TaskAsync()).Runtime.Phase == "queue" && clock.Pending == 1);
        var doc = await f.Plugin.Store.ReadAsync(); Assert.HasCount(1, doc.Notifications);
        Assert.AreEqual("dropped", doc.Notifications.Single().Kind);
        Assert.AreEqual(clock.Now.AddSeconds(90), doc.Tasks.Single().Runtime.NextAttemptAt);
    }
}
