using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Plugins.RouterTasks;

public sealed partial class RouterTasksTerminal
{
    [PluginEndpoint("GET", "network/proxies")]
    public Task<PluginResult> ListNetworkProxiesAsync(PluginHttpContext context) => Api(context, async () =>
        context.Ok(await _network.CatalogAsync(context.CancellationToken)));
}
