using System.Net;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Plugins.RouterTasks;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace RouterTasks.Tests;

[TestClass]
public sealed class NetworkTests
{
    private sealed class NetworkFixture
    {
        public Mock<IPluginHttpServices> Http { get; } = new(MockBehavior.Strict);
        public Mock<IPluginHost> Host { get; } = new();
        public Mock<IProxyStore> Store { get; } = new();
        public Mock<IProxySubscriptionService> Subscriptions { get; } = new();
        public Mock<IProxyHttpClientFactory> Clients { get; } = new(MockBehavior.Strict);
        public List<ProxyEndpoint> Nodes { get; } = [];
        public List<ProxySubscription> Sources { get; } = [];
        public List<string> Selected { get; } = [];
        public List<string> Requests { get; } = [];
        public NetworkClientFactory Network { get; }

        public NetworkFixture()
        {
            Host.Setup(h => h.GetServiceAsync<IProxyStore>()).ReturnsAsync(Store.Object);
            Host.Setup(h => h.GetServiceAsync<IProxySubscriptionService>()).ReturnsAsync(Subscriptions.Object);
            Host.Setup(h => h.GetServiceAsync<IProxyHttpClientFactory>()).ReturnsAsync(Clients.Object);
            Store.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => Nodes.ToArray());
            Subscriptions.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => Sources.ToArray());
            Clients.Setup(c => c.CreateClient(It.IsAny<ProxyEndpoint>(), "router-tasks"))
                .Returns((ProxyEndpoint node, string _) =>
                {
                    Selected.Add(node.Id);
                    return new HttpClient(new Handler((request, token) =>
                    {
                        Requests.Add(node.Id + ":" + request.RequestUri!.AbsolutePath);
                        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                    }));
                });
            Network = new NetworkClientFactory(Http.Object, Host.Object);
        }
        public ProxyEndpoint Add(string id, string source, ResourceState state = ResourceState.Active)
        {
            var node = new ProxyEndpoint { Id = id, SubscriptionId = source, Host = "127.0.0.1", Port = 3128,
                Username = "private-user", Password = "private-password", Status = new ResourceStatus { State = state } };
            Nodes.Add(node); return node;
        }
    }

    [TestMethod]
    public async Task FixedNodeUsesOneHostClientAcrossAllRequestsAndNeverDisposesTheHostFactory()
    {
        var f = new NetworkFixture();
        f.Sources.Add(new ProxySubscription { Id = "source", Name = "Source" });
        f.Add("other", "source"); f.Add("chosen", "source"); f.Add("manual", "manual");
        ProxyPoolNode? snapshot = null;
        using (var client = await f.Network.CreateClientAsync(new SiteConfig { Route = "pool", SubscriptionIds = ["source"], ProxyId = "chosen" },
            default, node => snapshot = node))
        {
            Assert.AreEqual(Timeout.InfiniteTimeSpan, client.Timeout);
            foreach (var path in new[] { "models", "usage", "subscription" })
                using (await client.GetAsync("http://upstream.invalid/" + path)) { }
        }
        CollectionAssert.AreEqual(new[] { "chosen" }, f.Selected.ToArray());
        Assert.AreEqual(3, f.Requests.Count);
        Assert.IsTrue(f.Requests.All(r => r.StartsWith("chosen:", StringComparison.Ordinal)));
        Assert.AreEqual("chosen", snapshot!.Id);
        Assert.AreEqual("http://127.0.0.1:3128", snapshot.Address);
        f.Http.Verify(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
        f.Clients.Verify(c => c.Dispose(), Times.Never);
    }

    [TestMethod]
    [DataRow("manual")]
    [DataRow("source")]
    [DataRow("")]
    public async Task SourceScopesUseOnlyEnabledSubscriptionsAndExplicitManualNodes(string scope)
    {
        var f = new NetworkFixture();
        f.Sources.Add(new ProxySubscription { Id = "source", Name = "Enabled" });
        f.Sources.Add(new ProxySubscription { Id = "off", Name = "Disabled", Enabled = false });
        f.Add("subscription-node", "source"); f.Add("hand-node", "manual"); f.Add("disabled-node", "off");
        for (var i = 0; i < 20; i++)
            using (await f.Network.CreateClientAsync(new SiteConfig { Route = "pool", SubscriptionIds = scope == "" ? [] : [scope] }, default)) { }
        var expected = scope switch { "manual" => new[] { "hand-node" }, "source" => ["subscription-node"], _ => ["hand-node", "subscription-node"] };
        Assert.IsTrue(f.Selected.All(expected.Contains));
        Assert.AreEqual(20, f.Selected.Count);
    }

    [TestMethod]
    [DataRow("missing", "source", "Active")]
    [DataRow("node", "other", "Active")]
    [DataRow("node", "source", "Disabled")]
    [DataRow("node", "source", "Draining")]
    [DataRow("node", "source", "Invalid")]
    [DataRow("node", "source", "Removed")]
    [DataRow("node", "source", "Cooling")]
    public async Task MissingOrUnavailableFixedNodeNeverSelectsAnotherNode(string fixedId, string scope, string state)
    {
        var f = new NetworkFixture();
        f.Sources.Add(new ProxySubscription { Id = "source" });
        f.Sources.Add(new ProxySubscription { Id = "other" });
        var node = f.Add("node", "source", Enum.Parse<ResourceState>(state));
        node.Status.CooldownUntil = DateTimeOffset.UtcNow.AddHours(1);
        f.Add("alternative", scope);
        var exception = await Assert.ThrowsExactlyAsync<ProxySelectionException>(() =>
            f.Network.CreateClientAsync(new SiteConfig { Route = "pool", ProxyId = fixedId, SubscriptionIds = [scope] }, default));
        StringAssert.Contains(exception.Message, "无可用节点");
        Assert.AreEqual(0, f.Selected.Count);
        f.Http.Verify(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
    }

    [TestMethod]
    public async Task DisabledSubscriptionCannotBeUsedEvenWithItsFixedNode()
    {
        var f = new NetworkFixture(); f.Sources.Add(new ProxySubscription { Id = "disabled", Enabled = false });
        f.Add("node", "disabled");
        await Assert.ThrowsExactlyAsync<ProxySelectionException>(() =>
            f.Network.CreateClientAsync(new SiteConfig { Route = "pool", SubscriptionIds = ["disabled"], ProxyId = "node" }, default));
        Assert.AreEqual(0, f.Selected.Count);
    }

    [TestMethod]
    public async Task ActiveNodeWithLiveCooldownOrDisableDeadlineIsExcluded()
    {
        var f = new NetworkFixture();
        var cooldown = f.Add("cooldown", "manual");
        cooldown.Status.CooldownUntil = DateTimeOffset.UtcNow.AddHours(1);
        var disabled = f.Add("disabled", "manual");
        disabled.Status.DisabledUntil = DateTimeOffset.UtcNow.AddHours(1);
        foreach (var id in new[] { "cooldown", "disabled" })
            await Assert.ThrowsExactlyAsync<ProxySelectionException>(() =>
                f.Network.CreateClientAsync(new SiteConfig { Route = "pool", ProxyId = id }, default));
        Assert.AreEqual(0, f.Selected.Count);
    }

    [TestMethod]
    public async Task CatalogProjectsOnlySafeFieldsAndKeepsUnavailableNodesVisible()
    {
        var f = new NetworkFixture();
        f.Sources.Add(new ProxySubscription { Id = "source", Name = "Visible", Url = "https://private-subscription.invalid/token" });
        f.Sources.Add(new ProxySubscription { Id = "disabled", Enabled = false });
        f.Add("active", "source"); f.Add("off", "disabled"); f.Add("manual-node", "manual");
        f.Add("invalid", "source", ResourceState.Invalid);
        var expired = f.Add("expired-cooldown", "source");
        expired.Status.CooldownUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
        var catalog = await f.Network.CatalogAsync(default);
        Assert.AreEqual(3, catalog.Subscriptions.Count);
        Assert.AreEqual(5, catalog.Nodes.Count);
        Assert.IsTrue(catalog.Nodes.Single(p => p.Id == "active").Available);
        Assert.IsTrue(catalog.Nodes.Single(p => p.Id == "manual-node").Available);
        Assert.IsTrue(catalog.Nodes.Single(p => p.Id == "expired-cooldown").Available);
        Assert.IsFalse(catalog.Nodes.Single(p => p.Id == "off").Available);
        Assert.IsFalse(catalog.Nodes.Single(p => p.Id == "invalid").Available);
        var json = JsonSerializer.Serialize(catalog, JsonData.Options);
        foreach (var secret in new[] { "private-user", "private-password", "private-subscription", "username", "password" })
            Assert.IsFalse(json.Contains(secret, StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task DirectRouteDoesNotLoadProxyServicesAndDisablesRedirects()
    {
        var f = new NetworkFixture();
        f.Http.Setup(h => h.CreateDirectClient(It.Is<PluginHttpClientOptions>(o => !o.AllowAutoRedirect && o.RequestTimeout == Timeout.InfiniteTimeSpan)))
            .Returns(() => new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))));
        using var client = await f.Network.CreateClientAsync(new SiteConfig { Route = "direct", ProxyId = "unused" }, default);
        f.Host.Verify(h => h.GetServiceAsync<IProxyStore>(), Times.Never);
        f.Host.Verify(h => h.GetServiceAsync<IProxySubscriptionService>(), Times.Never);
        f.Host.Verify(h => h.GetServiceAsync<IProxyHttpClientFactory>(), Times.Never);
    }

    [TestMethod]
    public async Task MissingHostServicesHaveNoTypedPoolFallbackAndReturnDetachedApiError()
    {
        await using var f = new Fixture();
        await Assert.ThrowsExactlyAsync<ProxySelectionException>(() =>
            new NetworkClientFactory(f.Http.Object).CreateClientAsync(new SiteConfig { Route = "pool" }, default));
        var response = await f.Plugin.ListNetworkProxiesAsync(Fixture.Context());
        Assert.AreEqual(503, response.StatusCode);
        var json = (JsonElement)response.Body!;
        Assert.AreEqual("proxy_unavailable", json.GetProperty("code").GetString());
        f.Http.VerifyGet(h => h.Pool, Times.Never);
        f.Http.Verify(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
    }

    [TestMethod]
    public async Task ModelAndQuotaQueryUseTheSameSelectedNodeAndReturnItsSnapshot()
    {
        await using var fixture = new Fixture();
        var f = new NetworkFixture(); f.Sources.Add(new ProxySubscription { Id = "source" }); f.Add("chosen", "source");
        f.Clients.Setup(c => c.CreateClient(It.IsAny<ProxyEndpoint>(), "router-tasks"))
            .Returns((ProxyEndpoint node, string _) =>
            {
                f.Selected.Add(node.Id);
                return new HttpClient(new Handler((request, _) =>
                {
                    f.Requests.Add(request.RequestUri!.AbsolutePath);
                    var body = request.RequestUri.AbsolutePath.EndsWith("/models", StringComparison.Ordinal)
                        ? "{\"data\":[{\"id\":\"model\"}]}"
                        : request.RequestUri.AbsolutePath.EndsWith("/usage", StringComparison.Ordinal)
                            ? "{\"total_usage\":250}" : "{\"hard_limit_usd\":50}";
                    return Task.FromResult(Fixture.Response(200, body, "application/json"));
                }));
            });
        var plugin = new RouterTasksTerminal(fixture.Services.Object, fixture.Directory, fixture.Clock, serviceHost: f.Host.Object);
        try
        {
            var result = await plugin.QueryAccountAsync(Fixture.Context(new AccountConnectionInput(
                new SiteConfig { Route = "pool", SubscriptionIds = ["source"], ProxyId = "chosen" }, "test-token")));
            Assert.AreEqual(200, result.StatusCode);
            var key = ((JsonElement)result.Body!).Deserialize<KeyInfo>(JsonData.Options)!;
            Assert.AreEqual("ready", key.AuthStatus);
            Assert.AreEqual("pool", key.LastRoute);
            Assert.AreEqual("chosen", key.LastProxy!.Id);
            Assert.AreEqual(2.5m, key.UsedUsd);
            Assert.AreEqual(50m, key.LimitUsd);
            Assert.AreEqual(3, f.Requests.Count);
            CollectionAssert.AreEqual(new[] { "chosen" }, f.Selected.ToArray());
        }
        finally { await plugin.StopAsync(default); }
    }

    [TestMethod]
    [DataRow("models")]
    [DataRow("usage")]
    [DataRow("subscription")]
    public async Task QueryCancellationIsNotConvertedIntoACompletedPartialQuery(string blockedPath)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = new Fixture(handler: async (request, token) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/" + blockedPath, StringComparison.Ordinal))
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Fixture.Response(200, path.EndsWith("/models", StringComparison.Ordinal)
                ? "{\"data\":[{\"id\":\"model\"}]}" : "{\"total_usage\":10}", "application/json");
        });
        using var cancel = new CancellationTokenSource();
        var query = fixture.Plugin.QueryAccountAsync(new PluginHttpContext
        {
            PluginKey = "router-tasks", Platform = "router-tasks", CancellationToken = cancel.Token,
            Body = new AccountConnectionInput(new SiteConfig(), "test-token")
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => query);
    }

    [TestMethod]
    public void ProxySelectionInputsAreNormalizedAndBounded()
    {
        var site = new SiteConfig { SubscriptionIds = [" manual ", "MANUAL", "source"], ProxyId = " node " };
        Validation.Site(site);
        CollectionAssert.AreEqual(new[] { "manual", "source" }, site.SubscriptionIds);
        Assert.AreEqual("node", site.ProxyId);
        site.ProxyId = " "; Validation.Site(site); Assert.IsNull(site.ProxyId);
        site.ProxyId = "bad\rnode"; Assert.ThrowsExactly<FormatException>(() => Validation.Site(site));
        site.ProxyId = null; site.SubscriptionIds = ["bad\nsource"];
        Assert.ThrowsExactly<FormatException>(() => Validation.Site(site));
    }
}
