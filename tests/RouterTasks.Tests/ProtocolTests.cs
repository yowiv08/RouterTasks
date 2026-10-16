using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Plugins.RouterTasks;
using Router.Contracts.Host;

namespace RouterTasks.Tests;

[TestClass]
public sealed class ProtocolTests
{
    [TestMethod]
    public async Task FullResponseSummaryUsesConfiguredUtf8LimitBeyond2048Bytes()
    {
        var payload = "event: response.created\ndata: {}\n\nevent: response.output_text.delta\ndata: {\"delta\":\"" + new string('中', 2000)
            + "\"}\n\nevent: response.completed\ndata: {}\n\n";
        await using var f = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(200, payload)));
        f.Config.Mode = "queue"; f.Config.Queue.ReadFullResponse = true; await f.SeedAsync();
        await f.Plugin.Store.UpdateAsync(d => { d.Settings.SummaryBytes = 5000; return true; });
        var result = await new HttpProbeClient(f.Http.Object, f.Clock).ProbeAsync(await f.Plugin.ResolveAsync(f.Config, default), false, "P", _ => Task.FromResult(true), default);
        Assert.AreEqual(4998, Encoding.UTF8.GetByteCount(result.Summary)); Assert.IsTrue(result.Completed);
    }

    [TestMethod]
    public async Task FullResponseTotalDeadlineCanBeShorterThanFirstEventDeadline()
    {
        await using var f = new Fixture(handler: async (_, token) => { await Task.Delay(120, token); return Fixture.Response(200, "event: response.created\ndata: {}\n\n"); });
        f.Config.Mode = "queue"; f.Config.Queue.ReadFullResponse = true; f.Config.Queue.FirstEventTimeoutSeconds = 1;
        f.Config.Queue.ResponseTimeoutSeconds = .03; await f.SeedAsync(); var accepted = 0;
        var result = await new HttpProbeClient(f.Http.Object, f.Clock).ProbeAsync(await f.Plugin.ResolveAsync(f.Config, default), false, "P", _ => { accepted++; return Task.FromResult(true); }, default);
        Assert.AreEqual(ProbeKind.Timeout, result.Kind); Assert.AreEqual(0, accepted);
    }

    [TestMethod]
    public async Task ProxyPoolFailureNeverFallsBackToDirectOrRefundsAttempts()
    {
        await using var f = new Fixture(); f.Site.Route = "pool"; await f.SeedAsync();
        var pool = new Mock<IProxyPoolHttpClientFactory>();
        pool.Setup(p => p.CreateClientAsync(It.IsAny<ProxyPoolHttpClientOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("代理池没有可用节点"));
        f.Http.SetupGet(h => h.Pool).Returns(pool.Object);
        await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var task = await f.TaskAsync(); Assert.AreEqual("failed", task.Runtime.Status); Assert.AreEqual(1L, task.Runtime.TotalAttempts);
        StringAssert.Contains(task.Runtime.LastError!, "代理池没有可用节点");
        f.Http.Verify(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
        pool.Verify(p => p.CreateClientAsync(It.Is<ProxyPoolHttpClientOptions>(o => !o.AllowDirectFallback && !o.AllowAutoRedirect), It.IsAny<CancellationToken>()), Times.Once);
    }
    [TestMethod]
    [DataRow(500, "Busy")]
    [DataRow(408, "Transient")]
    [DataRow(425, "Transient")]
    [DataRow(429, "Transient")]
    [DataRow(502, "Transient")]
    [DataRow(503, "Transient")]
    [DataRow(504, "Transient")]
    [DataRow(400, "Permanent")]
    [DataRow(401, "Permanent")]
    [DataRow(402, "Permanent")]
    [DataRow(403, "Permanent")]
    [DataRow(404, "Permanent")]
    public async Task ConfiguredQueueCodesAndTransientHttpFailuresAreDistinct(int status, string expected)
    {
        await using var f = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(status, "service error", "text/plain")));
        await f.SeedAsync(); var run = await f.Plugin.ResolveAsync(f.Config, default);
        var probe = new HttpProbeClient(f.Http.Object, f.Clock);
        var result = await probe.ProbeAsync(run, false, "PROMPT", _ => throw new AssertFailedException("错误响应不能接受"), default);
        Assert.AreEqual(expected, result.Kind.ToString());
    }
    [TestMethod]
    public async Task SiteAndTaskOverridesAndFatalPriority()
    {
        var body = "queued";
        await using var f = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(503, body, "application/json")));
        f.Site.QueueHttpStatuses = [503]; await f.SeedAsync(); var run = await f.Plugin.ResolveAsync(f.Config, default);
        var probe = new HttpProbeClient(f.Http.Object, f.Clock);
        Assert.AreEqual(ProbeKind.Busy, (await probe.ProbeAsync(run, false, "P", _ => Task.FromResult(true), default)).Kind);
        run.Config.QueueHttpStatuses = [500];
        Assert.AreEqual(ProbeKind.Transient, (await probe.ProbeAsync(run, false, "P", _ => Task.FromResult(true), default)).Kind);
        run.Config.QueueHttpStatuses = [503]; body = "insufficient_quota · TOKEN-secret-test-only";
        var result = await probe.ProbeAsync(run, false, "P", _ => Task.FromResult(true), default);
        Assert.AreEqual(ProbeKind.Permanent, result.Kind); StringAssert.Contains(result.Error!, "TOKEN-secret-test-only");
    }
    [TestMethod]
    [DataRow("application/json", "{\"ok\":true}")]
    [DataRow("text/event-stream", ": ping\n\nevent: ping\ndata: {}\n\n")]
    [DataRow("text/event-stream", "data: [DONE]\n\n")]
    [DataRow("text/event-stream", "event: response.created\ndata: {}")]
    [DataRow("text/event-stream", "data: {invalid}\n\n")]
    [DataRow("text/event-stream", "event: response.created\ndata: {\"type\":\"error\"}\n\n")]
    [DataRow("text/event-stream", "event: response.created\ndata: {\"error\":{\"message\":\"failed\"}}\n\n")]
    public async Task Http200IsNotAcceptance(string type, string text)
    {
        await using var f = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(200, text, type)));
        await f.SeedAsync(); var run = await f.Plugin.ResolveAsync(f.Config, default); var accepted = 0;
        var result = await new HttpProbeClient(f.Http.Object, f.Clock).ProbeAsync(run, false, "P", _ => { accepted++; return Task.FromResult(true); }, default);
        Assert.AreEqual(0, accepted); Assert.AreEqual(ProbeKind.Protocol, result.Kind);
    }
    [TestMethod]
    public async Task FullResponseOnlyAfterFormalAcceptanceAndFinalMarker()
    {
        var text = "event: response.created\ndata: {}\n\nevent: response.output_text.delta\ndata: {\"delta\":\"中文回答\"}\n\nevent: response.completed\ndata: {}\n\n";
        await using var f = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(200, text)));
        f.Config.Mode = "queue"; f.Config.Queue.ReadFullResponse = true; await f.SeedAsync();
        var result = await new HttpProbeClient(f.Http.Object, f.Clock).ProbeAsync(await f.Plugin.ResolveAsync(f.Config, default), false, "P", _ => Task.FromResult(true), default);
        Assert.AreEqual(ProbeKind.Accepted, result.Kind); Assert.IsTrue(result.Completed); Assert.AreEqual("中文回答", result.Summary);
    }
    [TestMethod]
    public async Task AcceptedThenTruncatedIsNotRetriedAndRemainsAccepted()
    {
        var requests = 0;
        await using var f = new Fixture(handler: (_, _) => { requests++; return Task.FromResult(Fixture.Response(200, "event: response.created\ndata: {}\n\n")); });
        f.Config.Mode = "queue"; f.Config.Queue.ReadFullResponse = true; await f.SeedAsync();
        await f.Plugin.Engine.ActionAsync("task", "start"); await Fixture.Until(() => Task.FromResult(f.Plugin.Engine.ActiveCount == 0));
        var task = await f.TaskAsync(); Assert.AreEqual("completed", task.Runtime.Status); Assert.AreEqual("interrupted", task.Runtime.ResponseState); Assert.AreEqual(1, requests);
    }
    [TestMethod]
    public async Task Utf8SplitCrLfAndMultilineDataAreParsed()
    {
        var bytes = Encoding.UTF8.GetBytes(": comment\r\nevent: response.created\r\ndata: {\r\ndata: \"hello\":\"中文\"}\r\n\r\n");
        using var stream = new OneByteStream(bytes); var frames = new List<SseFrame>();
        await foreach (var frame in SseReader.ReadAsync(stream, default)) frames.Add(frame);
        Assert.HasCount(1, frames); Assert.AreEqual("response.created", frames[0].Event); StringAssert.Contains(frames[0].Data, "中文");
    }
    [TestMethod]
    public async Task MessagesUseMessageStart()
    {
        await using var f = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(200, "data: {\"type\":\"message_start\"}\n\n")));
        f.Template.Protocol = "messages"; f.Template.Path = "messages?beta=true"; await f.SeedAsync();
        var result = await new HttpProbeClient(f.Http.Object, f.Clock).ProbeAsync(await f.Plugin.ResolveAsync(f.Config, default), true, "P", _ => Task.FromResult(true), default);
        Assert.AreEqual(ProbeKind.Accepted, result.Kind);
    }
    [TestMethod]
    public async Task BodyLimitAndTimeoutAreRealFailures()
    {
        await using var f = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(200, "data: {\"text\":\"" + new string('a', 1000) + "\"}\n\n")));
        f.Config.Queue.MaxResponseBytes = 20; await f.SeedAsync();
        var result = await new HttpProbeClient(f.Http.Object, f.Clock).ProbeAsync(await f.Plugin.ResolveAsync(f.Config, default), false, "P", _ => Task.FromResult(true), default);
        Assert.AreEqual(ProbeKind.Protocol, result.Kind);
        var http = new Mock<IPluginHttpServices>();
        http.Setup(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>())).Returns(() => new HttpClient(new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Fixture.Response(200, ""); })));
        var run = await f.Plugin.ResolveAsync(f.Config, default); run.Config.Queue.FirstEventTimeoutSeconds = .03;
        result = await new HttpProbeClient(http.Object, f.Clock).ProbeAsync(run, false, "P", _ => Task.FromResult(true), default);
        Assert.AreEqual(ProbeKind.Timeout, result.Kind);
    }
    [TestMethod]
    public async Task ExternalCancelIsNotTurnedIntoRetry()
    {
        await using var f = new Fixture(handler: async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Fixture.Response(200, ""); });
        await f.SeedAsync(); using var cancel = new CancellationTokenSource(30);
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
        {
            try { await new HttpProbeClient(f.Http.Object, f.Clock).ProbeAsync(await f.Plugin.ResolveAsync(f.Config, default), false, "P", _ => Task.FromResult(true), cancel.Token); }
            catch (TaskCanceledException) { throw new OperationCanceledException(cancel.Token); }
        });
    }
    private sealed class OneByteStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], token);
    }
}
