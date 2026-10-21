using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Plugins.RouterTasks;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace RouterTasks.Tests;

[TestClass]
public sealed class ProbeNetworkCancellationTests
{
    [TestMethod]
    public async Task PausingAnAcceptedResponsePreservesItsSelectedProxyAndDrainsTheSingleRequest()
    {
        await using var fixture = new Fixture();
        fixture.Site.Route = "pool";
        fixture.Site.SubscriptionIds = ["manual"];
        fixture.Site.ProxyId = "chosen";
        fixture.Config.Mode = "queue";
        fixture.Config.Queue.ReadFullResponse = true;
        await fixture.SeedAsync();

        var chosen = new ProxyEndpoint { Id = "chosen", SubscriptionId = "manual", Host = "127.0.0.1", Port = 3128 };
        var alternative = new ProxyEndpoint { Id = "alternative", SubscriptionId = "manual", Host = "127.0.0.1", Port = 3129 };
        var host = new Mock<IPluginHost>();
        var store = new Mock<IProxyStore>();
        var subscriptions = new Mock<IProxySubscriptionService>();
        var clients = new Mock<IProxyHttpClientFactory>(MockBehavior.Strict);
        host.Setup(h => h.GetServiceAsync<IProxyStore>()).ReturnsAsync(store.Object);
        host.Setup(h => h.GetServiceAsync<IProxySubscriptionService>()).ReturnsAsync(subscriptions.Object);
        host.Setup(h => h.GetServiceAsync<IProxyHttpClientFactory>()).ReturnsAsync(clients.Object);
        store.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { chosen, alternative });
        subscriptions.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ProxySubscription>());
        var posts = 0;
        var stream = new BlockingSseStream();
        clients.Setup(c => c.CreateClient(It.Is<ProxyEndpoint>(p => p.Id == "chosen"), "router-tasks"))
            .Returns(() => new HttpClient(new Handler((request, _) =>
            {
                Assert.AreEqual(HttpMethod.Post, request.Method);
                Interlocked.Increment(ref posts);
                var content = new StreamContent(stream);
                content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
            })));
        var network = new NetworkClientFactory(fixture.Http.Object, host.Object);
        await using var engine = new TaskEngine(fixture.Plugin.Store, new HttpProbeClient(network, fixture.Clock), fixture.Clock,
            fixture.Plugin.ResolveAsync, (run, token) => Task.FromResult(run.Config.Prompt));

        await engine.ActionAsync("task", "start");
        await stream.Blocked.WaitAsync(TimeSpan.FromSeconds(3));
        await Fixture.Until(async () => (await fixture.TaskAsync()).Runtime.ResponseState == "reading");
        var reading = await fixture.TaskAsync();
        Assert.IsTrue(reading.Runtime.Entered);
        Assert.AreEqual("pool", reading.Runtime.LastRoute);
        Assert.AreEqual("chosen", reading.Runtime.LastProxy!.Id);
        Assert.HasCount(1, reading.Events.Where(e => e.Kind == "network").ToArray());

        await engine.ActionAsync("task", "pause");

        var paused = await fixture.TaskAsync();
        Assert.AreEqual("paused", paused.Runtime.Status);
        Assert.AreEqual("interrupted", paused.Runtime.ResponseState);
        Assert.AreEqual("pool", paused.Runtime.LastRoute);
        Assert.AreEqual("chosen", paused.Runtime.LastProxy!.Id);
        Assert.AreEqual("http://127.0.0.1:3128", paused.Runtime.LastProxy.Address);
        var lane = paused.Runtime.Lanes.Single();
        Assert.AreEqual("pool", lane.LastRoute);
        Assert.AreEqual("chosen", lane.LastProxy!.Id);
        var networkEvent = paused.Events.Single(e => e.Kind == "network");
        Assert.AreEqual(1, networkEvent.Lane);
        StringAssert.Contains(networkEvent.Message, "chosen");
        StringAssert.Contains(networkEvent.Message, "http://127.0.0.1:3128");
        Assert.HasCount(1, paused.Events.Where(e => e.Kind == "accepted").ToArray());
        Assert.AreEqual(1L, paused.Runtime.TotalAttempts);
        Assert.AreEqual(1, Volatile.Read(ref posts));
        Assert.AreEqual(0, engine.ActiveCount);
        Assert.IsTrue(stream.CancellationObserved);
        Assert.IsTrue(stream.Disposed);
        clients.Verify(c => c.CreateClient(It.Is<ProxyEndpoint>(p => p.Id == "chosen"), "router-tasks"), Times.Once);
        clients.Verify(c => c.Dispose(), Times.Never);
        fixture.Http.Verify(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
        fixture.Http.VerifyGet(h => h.Pool, Times.Never);
    }

    private sealed class BlockingSseStream() : MemoryStream(Encoding.UTF8.GetBytes(
        "event: response.created\ndata: {\"type\":\"response.created\"}\n\n"), writable: false)
    {
        private readonly TaskCompletionSource _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;
        private int _cancelled;
        public Task Blocked => _blocked.Task;
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public bool CancellationObserved => Volatile.Read(ref _cancelled) != 0;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (Position < Length) return base.Read(buffer.Span);
            _blocked.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { Interlocked.Exchange(ref _cancelled, 1); throw; }
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token)
            => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();

        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Exchange(ref _disposed, 1);
            base.Dispose(disposing);
        }
    }
}
