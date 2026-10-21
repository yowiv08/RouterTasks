using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Plugins.RouterTasks;

// 仅公开节点标识及地址；代理认证始终留在宿主服务内。
public sealed record ProxyPoolSubscription(string Id, string Name, bool Enabled);
public sealed record ProxyPoolNode(string Id, string SubscriptionId, string Address, string State, bool Available);
internal sealed record ProxyPoolCatalog(IReadOnlyList<ProxyPoolSubscription> Subscriptions, IReadOnlyList<ProxyPoolNode> Nodes);

internal sealed class ProxySelectionException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>每次操作固定一个节点，复用宿主管理的物理连接池；不隐式重试模型 POST。</summary>
internal sealed class NetworkClientFactory(IPluginHttpServices http, IPluginHost? host = null)
{
    internal const string UnavailableMessage = "指定代理范围内无可用节点，请检查来源和固定节点；未回退直连";
    internal const string ServicesUnavailableMessage = "宿主未提供公开代理目录服务，无法选择代理节点";
    private sealed record Services(IProxyStore Store, IProxySubscriptionService Subscriptions, IProxyHttpClientFactory Clients);
    private readonly Lazy<Task<Services>> _services = new(() => LoadServicesAsync(host));

    private static async Task<Services> LoadServicesAsync(IPluginHost? host)
    {
        try
        {
            if (host is null) throw new NotSupportedException();
            var store = host.GetServiceAsync<IProxyStore>();
            var subscriptions = host.GetServiceAsync<IProxySubscriptionService>();
            var clients = host.GetServiceAsync<IProxyHttpClientFactory>();
            await Task.WhenAll(store, subscriptions, clients);
            return new Services(await store ?? throw new NotSupportedException(),
                await subscriptions ?? throw new NotSupportedException(), await clients ?? throw new NotSupportedException());
        }
        catch (Exception error) when (error is NotSupportedException or InvalidOperationException)
        { throw new ProxySelectionException(ServicesUnavailableMessage, error); }
    }

    public async Task<ProxyPoolCatalog> CatalogAsync(CancellationToken token)
    {
        var services = await _services.Value.WaitAsync(token);
        var subscriptions = await services.Subscriptions.ListAsync(token);
        var nodes = await services.Store.ListAsync(token);
        var enabled = EnabledSources(subscriptions, []);
        var now = DateTimeOffset.UtcNow;
        return new ProxyPoolCatalog(subscriptions.Where(s => !s.Id.Equals("manual", StringComparison.OrdinalIgnoreCase))
            .Select(s => new ProxyPoolSubscription(s.Id, s.Name, s.Enabled))
            .Append(new ProxyPoolSubscription("manual", "手动节点", true)).ToArray(),
            nodes.Select(p => Snapshot(p, Available(p, enabled, now))).ToArray());
    }

    public async Task<HttpClient> CreateClientAsync(SiteConfig site, CancellationToken token, Action<ProxyPoolNode?>? selected = null)
    {
        token.ThrowIfCancellationRequested();
        Validation.Site(site);
        if (site.Route == "direct")
        {
            var client = http.CreateDirectClient(new PluginHttpClientOptions
            { AllowAutoRedirect = false, RequestTimeout = Timeout.InfiniteTimeSpan });
            try { selected?.Invoke(null); return client; }
            catch { client.Dispose(); throw; }
        }
        var services = await _services.Value.WaitAsync(token);
        var subscriptions = await services.Subscriptions.ListAsync(token);
        var enabled = EnabledSources(subscriptions, site.SubscriptionIds);
        var nodes = await services.Store.ListAsync(token);
        token.ThrowIfCancellationRequested();
        var now = DateTimeOffset.UtcNow;
        var candidates = nodes.Where(p => Available(p, enabled, now)
            && (site.ProxyId is null || p.Id.Equals(site.ProxyId, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (candidates.Length == 0) throw new ProxySelectionException(UnavailableMessage);
        var node = candidates[Random.Shared.Next(candidates.Length)];
        // 工厂由宿主拥有；调用方只释放这个轻量 HttpClient 包装器。
        var pooled = services.Clients.CreateClient(node, "router-tasks");
        try
        {
            pooled.Timeout = Timeout.InfiniteTimeSpan;
            selected?.Invoke(Snapshot(node, true));
            return pooled;
        }
        catch { pooled.Dispose(); throw; }
    }

    private static HashSet<string> EnabledSources(IEnumerable<ProxySubscription> subscriptions, string[] requested)
    {
        var allowed = requested.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var enabled = subscriptions.Where(s => s.Enabled && (allowed.Count == 0 || allowed.Contains(s.Id)))
            .Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowed.Count == 0 || allowed.Contains("manual")) enabled.Add("manual");
        return enabled;
    }

    private static bool Available(ProxyEndpoint proxy, HashSet<string> enabled, DateTimeOffset now)
        => enabled.Contains(proxy.SubscriptionId) && ResourceAvailability.IsAvailable(proxy, now)
            && !string.IsNullOrWhiteSpace(proxy.Host) && proxy.Port is > 0 and <= 65535;

    private static ProxyPoolNode Snapshot(ProxyEndpoint proxy, bool available)
    {
        var hostname = proxy.Host.Contains(':') && !proxy.Host.StartsWith('[') ? $"[{proxy.Host}]" : proxy.Host;
        var scheme = proxy.Scheme switch { ProxyScheme.Https => "https", ProxyScheme.Socks5 => "socks5", _ => "http" };
        return new ProxyPoolNode(proxy.Id, proxy.SubscriptionId, $"{scheme}://{hostname}:{proxy.Port}", proxy.Status.State.ToString(), available);
    }
}
