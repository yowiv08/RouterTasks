using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugins.RouterTasks;

namespace RouterTasks.Tests;

[TestClass]
public sealed class ProbeBoundaryUpgradeTests
{
    [TestMethod]
    [DataRow("responses")]
    [DataRow("messages")]
    public async Task BareDoneEndsTheStreamWithoutClaimingProtocolCompletion(string protocol)
    {
        var acceptance = protocol == "responses" ? "response.created" : "message_start";
        var result = await FullProbe(protocol, $"event: {acceptance}\ndata: {{}}\n\ndata: [DONE]\n\n");
        Assert.AreEqual(ProbeKind.Accepted, result.Kind);
        Assert.IsFalse(result.Completed);
        StringAssert.Contains(result.Error!, "未收到协议完成事件");
    }

    [TestMethod]
    [DataRow("response.completed", "completed", true)]
    [DataRow("response.done", "completed", true)]
    [DataRow("response.completed", "failed", false)]
    [DataRow("response.completed", "incomplete", false)]
    [DataRow("response.completed", "cancelled", false)]
    [DataRow("response.completed", "in_progress", false)]
    [DataRow("response.done", "failed", false)]
    [DataRow("response.done", "in_progress", false)]
    public async Task ResponseTerminalEventsHonorTheActualResponseStatus(string terminal, string status, bool completed)
    {
        var result = await FullProbe("responses", "event: response.created\ndata: {}\n\n"
            + $"event: {terminal}\ndata: {{\"response\":{{\"status\":\"{status}\"}}}}\n\n");
        Assert.AreEqual(ProbeKind.Accepted, result.Kind);
        Assert.AreEqual(completed, result.Completed);
        if (!completed) Assert.IsFalse(string.IsNullOrEmpty(result.Error));
    }

    [TestMethod]
    [DataRow("event: response.done\ndata: {}\n\n")]
    [DataRow("event: response.completed\ndata: {\"response\":[]}\n\n")]
    [DataRow("event: response.completed\ndata: {\"response\":{\"status\":\"completed\",\"error\":{\"message\":\"failed\"}}}\n\n")]
    public async Task AmbiguousOrFailedTerminalPayloadsNeverComplete(string terminal)
    {
        var result = await FullProbe("responses", "event: response.created\ndata: {}\n\n" + terminal);
        Assert.AreEqual(ProbeKind.Accepted, result.Kind);
        Assert.IsFalse(result.Completed);
        Assert.IsFalse(string.IsNullOrEmpty(result.Error));
    }

    [TestMethod]
    public async Task NestedPermanentErrorBeforeAcceptanceStopsWithoutClaimingAcceptance()
    {
        await using var fixture = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(200,
            "event: response.created\ndata: {\"response\":{\"status\":\"failed\",\"error\":{\"code\":\"insufficient_quota\"}}}\n\n")));
        await fixture.SeedAsync();
        var accepted = 0;
        var result = await new HttpProbeClient(fixture.Http.Object, fixture.Clock).ProbeAsync(
            await fixture.Plugin.ResolveAsync(fixture.Config, default), false, "P", _ => { accepted++; return Task.FromResult(true); }, default);
        Assert.AreEqual(ProbeKind.Permanent, result.Kind);
        Assert.AreEqual(0, accepted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationDuringAcceptanceCannotReturnASuccess(bool readFull)
    {
        await using var fixture = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(200,
            "event: response.created\ndata: {}\n\nevent: response.completed\ndata: {}\n\n")));
        fixture.Config.Mode = "queue"; fixture.Config.Queue.ReadFullResponse = readFull;
        await fixture.SeedAsync();
        using var cancellation = new CancellationTokenSource();
        var run = await fixture.Plugin.ResolveAsync(fixture.Config, default);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new HttpProbeClient(fixture.Http.Object, fixture.Clock)
            .ProbeAsync(run, false, "P", _ => { cancellation.Cancel(); return Task.FromResult(true); }, cancellation.Token));
    }

    [TestMethod]
    public async Task BufferedSseFramesStillObserveCancellation()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: first\n\ndata: second\n\n"));
        using var cancellation = new CancellationTokenSource();
        await using var reader = SseReader.ReadAsync(stream, cancellation.Token).GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await reader.MoveNextAsync());
    }

    [TestMethod]
    public async Task IdleTimeoutAndCallerCancellationRemainDistinctWithAReusedReadTimer()
    {
        using var source = new BlockingReadStream();
        await using (var stream = new TimedReadStream(source, 100, TimeSpan.FromMilliseconds(80)))
            await Assert.ThrowsExactlyAsync<TimeoutException>(async () => Assert.AreEqual(1, await stream.ReadAsync(new byte[1])));
        await using (var stream = new TimedReadStream(source, 100, TimeSpan.FromSeconds(30)))
        {
            using var cancellation = new CancellationTokenSource();
            var read = stream.ReadAsync(new byte[1], cancellation.Token).AsTask();
            cancellation.Cancel();
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => read);
        }
    }

    [TestMethod]
    public async Task IdleTimerStopsBetweenReadsAndRestartsForEverySubsequentRead()
    {
        using var source = new BytesThenBlockStream([1, 2, 3]);
        await using var stream = new TimedReadStream(source, 100, TimeSpan.FromMilliseconds(100));
        var buffer = new byte[1];
        Assert.AreEqual(1, await stream.ReadAsync(buffer));
        Assert.AreEqual((byte)1, buffer[0]);
        // 消费端思考时间不计入一次网络读取的空闲超时。
        await Task.Delay(180);
        Assert.AreEqual(1, await stream.ReadAsync(buffer));
        Assert.AreEqual((byte)2, buffer[0]);
        using var cancellation = new CancellationTokenSource();
        Assert.AreEqual(1, await stream.ReadAsync(buffer, cancellation.Token));
        Assert.AreEqual((byte)3, buffer[0]);
        await Assert.ThrowsExactlyAsync<TimeoutException>(async () => Assert.AreEqual(1, await stream.ReadAsync(buffer, cancellation.Token)));
    }

    [TestMethod]
    public async Task CallerCancellationAfterSuccessfulReadsStillTakesPriority()
    {
        using var source = new BytesThenBlockStream([1]);
        await using var stream = new TimedReadStream(source, 100, TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();
        Assert.AreEqual(1, await stream.ReadAsync(new byte[1], cancellation.Token));
        var read = stream.ReadAsync(new byte[1], cancellation.Token).AsTask();
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => read);
    }

    [TestMethod]
    public async Task TimedReadsCheckCancellationEvenWhenTheSourceIgnoresItsToken()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new CancelOnReadStream(cancellation);
        await using var stream = new TimedReadStream(source, 1, TimeSpan.FromSeconds(30));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => Assert.AreEqual(2, await stream.ReadAsync(new byte[2], cancellation.Token)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ActualNodeIsStoredForSuccessfulAndFailedRequests(bool accepted)
    {
        var node = new ProxyPoolNode("chosen", "manual", "http://127.0.0.1:1234", "Available", true);
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(
            accepted ? ProbeKind.Accepted : ProbeKind.Permanent, accepted ? 200 : 401,
            accepted ? null : "denied", Completed: accepted, Proxy: node, Route: "pool")));
        await using var fixture = new Fixture(probe);
        fixture.Config.Mode = "queue"; fixture.Site.Route = "pool";
        await fixture.SeedAsync();
        await fixture.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(fixture.Plugin.Engine.ActiveCount == 0));
        var task = await fixture.TaskAsync();
        Assert.AreEqual(node, task.Runtime.LastProxy);
        Assert.AreEqual("pool", task.Runtime.LastRoute);
        Assert.AreEqual(node, task.Runtime.Lanes[0].LastProxy);
        Assert.IsTrue(task.Events.Any(e => e.Message.Contains(node.Address) && e.Message.Contains(node.Id)));
    }

    private static async Task<ProbeResult> FullProbe(string protocol, string payload)
    {
        await using var fixture = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(200, payload)));
        fixture.Config.Mode = "queue"; fixture.Config.Queue.ReadFullResponse = true; fixture.Template.Protocol = protocol;
        await fixture.SeedAsync();
        return await new HttpProbeClient(fixture.Http.Object, fixture.Clock).ProbeAsync(
            await fixture.Plugin.ResolveAsync(fixture.Config, default), false, "P", _ => Task.FromResult(true), default);
    }

    private sealed class BlockingReadStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
    }
    private sealed class CancelOnReadStream(CancellationTokenSource cancellation) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { cancellation.Cancel(); return ValueTask.FromResult(buffer.Length); }
    }
    private sealed class BytesThenBlockStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position < Length) return await base.ReadAsync(buffer, token);
            await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0;
        }
    }
}
