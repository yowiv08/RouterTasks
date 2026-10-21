using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Host.Configuration;
using Router.Host.Plugins;
using Router.Host.Security;
using Router.Infrastructure.Services;
using Router.Infrastructure.Persistence;

if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] != "--storage-only")
    throw new ArgumentException("参数：插件 ZIP 路径、隔离运行目录、可选 --storage-only");
var storageOnly = args.Length == 3;
var packageZip = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
if (Directory.Exists(root)) throw new IOException("隔离运行目录必须不存在");
Directory.CreateDirectory(root);
var pluginRoot = Path.Combine(root, "plugins");
ZipFile.ExtractToDirectory(packageZip, pluginRoot);
var dataPath = Path.Combine(root, "data");
Environment.SetEnvironmentVariable("ROUTER2API_ROUTERTASKS_DATA_DIR", dataPath);

var accountData = new ConcurrentDictionary<string, Account>();
var accounts = new Mock<IPluginAccounts>();
accounts.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string id, CancellationToken _) => accountData.GetValueOrDefault(id));
accounts.Setup(a => a.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => accountData.Values.ToArray());
accounts.Setup(a => a.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>())).ReturnsAsync((Account account, CancellationToken _) =>
{ account.CredentialVersion = 1; accountData[account.Id] = account; return account; });
accounts.Setup(a => a.CompareExchangeCredentialAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
    .ReturnsAsync((string id, long version, Credential credential, CancellationToken _) =>
    {
        if (!accountData.TryGetValue(id, out var account) || account.CredentialVersion != version) return null;
        account.Credential = credential; account.CredentialVersion++; return account;
    });
AccountService? persistentAccounts = null;
if (storageOnly)
{
    var database = new SqlSugarDatabase(Options.Create(new DatabaseOptions { Path = Path.Combine(root, "accounts.db") }), NullLogger<SqlSugarDatabase>.Instance);
    using (var db = database.CreateClient()) db.CodeFirst.InitTables<AccountEntity>();
    persistentAccounts = new AccountService(database, Mock.Of<ISharedKeyValueStore>());
    accounts.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .Returns((string id, CancellationToken token) => persistentAccounts.GetAsync("router-tasks", id, token));
    accounts.Setup(a => a.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
        .Returns((string? platform, CancellationToken token) => persistentAccounts.ListAsync("router-tasks", platform, token));
    accounts.Setup(a => a.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>()))
        .Returns((Account account, CancellationToken token) => persistentAccounts.SaveAsync(account, token));
    accounts.Setup(a => a.CompareExchangeCredentialAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
        .Returns((string id, long version, Credential credential, CancellationToken token) => persistentAccounts.CompareExchangeCredentialAsync("router-tasks", id, version, credential, token));
    accounts.Setup(a => a.PatchAsync(It.IsAny<Account>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
        .Returns((Account account, IReadOnlyList<string> fields, CancellationToken token) => persistentAccounts.PatchAsync(account, fields, token));
    accounts.Setup(a => a.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
        .Returns((string id, CancellationToken token) => persistentAccounts.DeleteAsync("router-tasks", id, token));
}
Uri? telegramEndpoint = null;
var directClientCount = 0;
var http = new Mock<IPluginHttpServices>();
http.Setup(h => h.ApproveOriginAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
http.Setup(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>())).Returns((PluginHttpClientOptions options) =>
{
    Interlocked.Increment(ref directClientCount);
    return new HttpClient(new LoopbackOnlyHandler(new SocketsHttpHandler { AllowAutoRedirect = options.AllowAutoRedirect, UseProxy = false, UseCookies = false }, () => telegramEndpoint))
    { Timeout = options.RequestTimeout };
});
var services = new Mock<IPluginServices>();
services.SetupGet(s => s.PluginKey).Returns("router-tasks"); services.SetupGet(s => s.Accounts).Returns(accounts.Object);
services.SetupGet(s => s.Http).Returns(http.Object); services.SetupGet(s => s.Log).Returns(Mock.Of<IPluginLogSink>());
services.SetupGet(s => s.Jobs).Returns(Mock.Of<IPluginJobs>());
var host = new Mock<IPluginHost>(); host.SetupGet(h => h.PluginKey).Returns("router-tasks"); host.SetupGet(h => h.Services).Returns(services.Object);
using var proxyTransport = new ProxyTransportFactory();
var proxyClients = new ProxyHttpClientFactory(proxyTransport);
ProxyEndpoint[] proxyNodes = [];
var proxyStore = new Mock<IProxyStore>();
proxyStore.Setup(p => p.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => proxyNodes);
var proxySubscriptions = new Mock<IProxySubscriptionService>();
proxySubscriptions.Setup(p => p.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync([
    new ProxySubscription { Id = "sub-a", Name = "Enabled source", Enabled = true, Url = "https://subscription.invalid/?token=SUBSCRIPTION-secret" },
    new ProxySubscription { Id = "sub-disabled", Name = "Disabled source", Enabled = false }
]);
host.Setup(h => h.GetServiceAsync<IProxyStore>()).ReturnsAsync(proxyStore.Object);
host.Setup(h => h.GetServiceAsync<IProxySubscriptionService>()).ReturnsAsync(proxySubscriptions.Object);
host.Setup(h => h.GetServiceAsync<IProxyHttpClientFactory>()).ReturnsAsync(proxyClients);
var factory = new Mock<IPluginHostFactory>(); factory.Setup(f => f.Create(It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>())).Returns(host.Object);
var registry = new PlatformRegistry();
var authOptions = new AuthOptions { Admin = new AdminOptions { Users = [new AdminUserOptions { Username = "local-test", Password = Guid.NewGuid().ToString("N") }] }, ApiKey = new ApiKeyOptions { Key = "local-test-api-key" } };
var options = new Mock<IOptionsMonitor<AuthOptions>>(); options.SetupGet(o => o.CurrentValue).Returns(authOptions);
var auth = new AdminAuthService(options.Object, new PasswordHasher(), NullLogger<AdminAuthService>.Instance);
using var keys = new ApiKeyService(options.Object);
await using var catalog = new PluginCatalog(registry, Mock.Of<IModelCatalog>(), factory.Object, new PluginPolicyRegistry(), auth, keys,
    Mock.Of<IPluginLogSink>(), NullLogger<PluginCatalog>.Instance, Options.Create(new PluginExecutionOptions()));
typeof(PluginCatalog).GetField("_pluginRoot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(catalog, pluginRoot);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [], ContentRootPath = root });
builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
await using var app = builder.Build();
var upstreamCalls = 0; var upstreamActive = 0; var upstreamCancelled = 0;
var notificationDeliveries = new ConcurrentQueue<string>();
app.MapPost("/notifications/telegram", async (HttpContext context) =>
{
    var payload = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
    if (payload?["chat_id"]?.GetValue<string>() != "CHAT-loopback" ||
        payload["text"]?.GetValue<string>().Contains("首次进入", StringComparison.Ordinal) != true)
        return Results.BadRequest(new { ok = false });
    notificationDeliveries.Enqueue("telegram");
    return Results.Json(new { ok = true });
});
app.MapGet("/notifications/bark/{key}/{title}/{message}", (HttpContext context, string key, string title, string message) =>
{
    if (key != "BARK-loopback" || !title.Contains("首次进入", StringComparison.Ordinal) ||
        !message.Contains("发生时间", StringComparison.Ordinal) || context.Request.Query["group"] != "RouterTasks")
        return Results.BadRequest(new { code = 400 });
    notificationDeliveries.Enqueue("bark");
    return Results.Json(new { code = 200 });
});
app.MapPost("/upstream/v1/responses", async (HttpContext context) =>
{
    Interlocked.Increment(ref upstreamActive);
    try
    {
        var n = Interlocked.Increment(ref upstreamCalls);
        if (n == 1) { context.Response.StatusCode = 500; await context.Response.WriteAsync("queued"); return; }
        context.Response.ContentType = "text/event-stream";
        await context.Response.StartAsync(context.RequestAborted);
        if (n == 2) await context.Response.WriteAsync("event: response.created\ndata: {\"type\":\"response.created\"}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
        await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { Interlocked.Increment(ref upstreamCancelled); }
    finally { Interlocked.Decrement(ref upstreamActive); }
});
app.MapGet("/proxy-upstream/v1/models", () => Results.Json(new { data = new[] { new { id = "MODEL" } } }));
app.MapGet("/proxy-upstream/v1/dashboard/billing/usage", () => Results.Json(new { total_usage = 125 }));
app.MapGet("/proxy-upstream/v1/dashboard/billing/subscription", () => Results.Json(new { hard_limit_usd = 25 }));
catalog.MapEndpoints(app);
await app.StartAsync();
var origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
await using var manualProxy = await LoopbackHttpProxy.StartAsync(origin);
await using var subscriptionProxy = await LoopbackHttpProxy.StartAsync(origin);
proxyNodes = [
    new ProxyEndpoint { Id = "chosen", SubscriptionId = "manual", Host = "127.0.0.1", Port = manualProxy.Port, Username = "PROXY-user", Password = "PROXY-secret" },
    new ProxyEndpoint { Id = "subscribed", SubscriptionId = "sub-a", Host = "127.0.0.1", Port = subscriptionProxy.Port },
    new ProxyEndpoint { Id = "cooling", SubscriptionId = "manual", Host = "127.0.0.1", Port = subscriptionProxy.Port,
        Status = new ResourceStatus { State = ResourceState.Cooling, CooldownUntil = DateTimeOffset.UtcNow.AddMinutes(5) } },
    new ProxyEndpoint { Id = "disabled-source", SubscriptionId = "sub-disabled", Host = "127.0.0.1", Port = subscriptionProxy.Port }
];
telegramEndpoint = new Uri(origin + "/notifications/telegram");
using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(origin) };
var checks = new List<string>();
var weakContexts = new List<WeakReference>();
try
{
    await catalog.ReloadAsync("router-tasks");
    Check(catalog.Get("router-tasks")?.State == "Active", "加载插件 ZIP");
    Check(!Directory.Exists(dataPath), "候选加载不打开业务存储");
    Check(catalog.GetMainPage("router-tasks")?.Html.Contains("请求排队与持续保活", StringComparison.Ordinal) == true, "嵌入式管理页面");
    Check((await client.GetAsync("/api/plugins/router-tasks/state")).StatusCode == HttpStatusCode.Unauthorized, "未登录请求被拒绝");
    var login = auth.Login("local-test", authOptions.Admin.Users[0].Password!);
    var csrf = Guid.NewGuid().ToString("N");
    client.DefaultRequestHeaders.Add("Cookie", $"router_admin_session={login.SessionId}; router_admin_csrf={csrf}");
    Check((await client.PostAsJsonAsync("/api/plugins/router-tasks/tasks/action", new { ids = new[] { "task" }, action = "start" })).StatusCode == HttpStatusCode.Forbidden, "缺少 CSRF 被拒绝");
    client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf); client.DefaultRequestHeaders.Add("Origin", origin);
    if (storageOnly)
    {
        var saved = await Post("accounts/save", new
        {
            config = new { id = "storage-account", name = "Storage test", model = "MODEL", mode = "queue", settings = new { telegramEnabled = false, barkEnabled = false } },
            site = new { id = "draft-site", name = "Loopback", baseUrl = origin + "/upstream", route = "direct" },
            token = "TOKEN-storage-test", secrets = new { telegramToken = "BOT-storage", telegramChatId = "CHAT-storage", credentialVersion = 0 }
        });
        Check(saved!["token"]!.GetValue<string>() == "TOKEN-storage-test", "实际 SQLite 保存并读回完整 Key");
        var keyId = saved["config"]!["keyId"]!.GetValue<string>();
        var account = await persistentAccounts!.GetAsync("router-tasks", keyId);
        Check(account!.Credential is CustomCredential custom && custom.Fields.GetValueOrDefault("type") == "key", "实际宿主 CredentialCodec 保持 Custom 类型与站点字段");
        var input = new JsonObject
        {
            ["config"] = saved["config"]!.DeepClone(), ["site"] = saved["site"]!.DeepClone(), ["token"] = "TOKEN-storage-updated",
            ["credentialVersion"] = saved["credentialVersion"]!.DeepClone(), ["expectedRevision"] = saved["configRevision"]!.DeepClone(),
            ["secrets"] = saved["secrets"]!.DeepClone()
        };
        var updated = await Post("accounts/save", input);
        Check(updated!["token"]!.GetValue<string>() == "TOKEN-storage-updated" && updated["credentialVersion"]!.GetValue<long>() == 2, "实际数据库 Key CAS 更新保留类型与版本");
        using (var conflict = await client.PostAsJsonAsync("/api/plugins/router-tasks/accounts/save", input))
            Check(conflict.StatusCode == HttpStatusCode.Conflict, "数据库更新拒绝旧修订号");
        var preview = await Post("requests/preview", updated["config"]!);
        Check(preview!["headers"]!["authorization"]!.GetValue<string>() == "Bearer TOKEN-storage-updated", "请求构造从持久化账号读取最新 Key");
        await catalog.ReloadAsync("router-tasks");
        var restored = await Get("accounts/detail?id=storage-account");
        Check(restored["token"]!.GetValue<string>() == "TOKEN-storage-updated", "插件重载后 Key 可读");
        Check(restored["secrets"]!["telegramToken"]!.GetValue<string>() == "BOT-storage" && restored["secrets"]!["telegramChatId"]!.GetValue<string>() == "CHAT-storage", "账号通知凭据独立持久化");
        Check((await State())["activeCount"]!.GetValue<int>() == 0 && upstreamCalls == 0, "持久化验收没有模型请求或通知");
        await Post("accounts/delete", new { id = "storage-account" });
        Check((await persistentAccounts.ListAsync("router-tasks")).Count == 0 && (await State())["tasks"]!.AsArray().Count == 0, "删除账号清理 SQLite Key、通知与任务数据");
        await catalog.RemovePackageAsync("router-tasks", default);
        var report = Path.Combine(root, "storage-report.json");
        await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { checks, upstreamCalls, database = "isolated SQLite", scope = "实际账号服务与序列化；不验证可回收程序集上下文 GC" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { passed = checks.Count, upstreamCalls, report }));
        return;
    }
    var proxyCatalog = await Get("network/proxies");
    Check(proxyCatalog["subscriptions"]!.AsArray().Any(s => s!["id"]!.GetValue<string>() == "manual"), "代理目录包含手动来源");
    Check(proxyCatalog["nodes"]!.AsArray().Single(n => n!["id"]!.GetValue<string>() == "chosen")!["available"]!.GetValue<bool>(), "公开宿主服务列出可用节点");
    Check(proxyCatalog["nodes"]!.AsArray().Where(n => n!["id"]!.GetValue<string>() is "cooling" or "disabled-source")
        .All(n => !n!["available"]!.GetValue<bool>()), "目录遵守节点冷却和订阅禁用状态");
    var catalogJson = proxyCatalog.ToJsonString();
    Check(!catalogJson.Contains("PROXY-user", StringComparison.Ordinal) && !catalogJson.Contains("PROXY-secret", StringComparison.Ordinal)
        && !catalogJson.Contains("SUBSCRIPTION-secret", StringComparison.Ordinal), "代理目录不返回节点认证或订阅 URL 凭据");
    object ProxyQuery(string[] sources, string? proxyId) => new
    {
        site = new { id = "draft-proxy", name = "Proxy query", baseUrl = "http://upstream.invalid/proxy-upstream", route = "pool", subscriptionIds = sources, proxyId },
        token = "TOKEN-loopback-only"
    };
    var beforeDirect = directClientCount;
    for (var i = 0; i < 2; i++)
    {
        var queried = await Post("accounts/query", ProxyQuery([], "chosen"));
        Check(queried!["authStatus"]!.GetValue<string>() == "ready" && queried["models"]![0]!.GetValue<string>() == "MODEL"
            && queried["usedUsd"]!.GetValue<decimal>() == 1.25m, "固定节点完成模型与额度查询 " + (i + 1));
        Check(queried["lastRoute"]!.GetValue<string>() == "pool" && queried["lastProxy"]!["id"]!.GetValue<string>() == "chosen", "查询返回实际固定节点 " + (i + 1));
    }
    Check(manualProxy.Requests == 6 && subscriptionProxy.Requests == 0 && manualProxy.ConnectionCount == 1,
        "跨客户端的六次查询复用官方宿主物理连接池且只使用固定节点");
    var manual = await Post("accounts/query", ProxyQuery(["manual"], null));
    Check(manual!["lastProxy"]!["id"]!.GetValue<string>() == "chosen" && manualProxy.Requests == 9, "仅手动来源自动选择手动节点");
    var subscribed = await Post("accounts/query", ProxyQuery(["sub-a"], null));
    Check(subscribed!["lastProxy"]!["id"]!.GetValue<string>() == "subscribed" && subscriptionProxy.Requests == 3, "限定订阅只选择该订阅的节点");
    var beforeRejected = manualProxy.Requests + subscriptionProxy.Requests;
    foreach (var selection in new[] { (Sources: Array.Empty<string>(), Id: "missing"), (Sources: new[] { "sub-a" }, Id: "chosen"),
        (Sources: new[] { "manual" }, Id: "cooling"), (Sources: new[] { "sub-disabled" }, Id: "disabled-source") })
    {
        using var rejected = await client.PostAsJsonAsync("/api/plugins/router-tasks/accounts/query", ProxyQuery(selection.Sources, selection.Id));
        Check(rejected.StatusCode == HttpStatusCode.ServiceUnavailable, "失效或越界固定节点明确返回 503：" + selection.Id);
    }
    Check(manualProxy.Requests + subscriptionProxy.Requests == beforeRejected && directClientCount == beforeDirect,
        "代理选择失败没有请求其他节点或回退直连");
    await Post("sites/save", new { id = "site", name = "Loopback", baseUrl = "http://upstream.invalid/upstream", route = "pool",
        subscriptionIds = new[] { "manual" }, proxyId = "chosen" });
    var key = await Post("keys/save", new { siteId = "site", alias = "Loopback", token = "TOKEN-loopback-only" });
    await Post("templates/save", new { id = "simple", name = "Loopback", protocol = "responses", path = "responses", headers = new { Authorization = "Bearer {{TOKEN}}" },
        body = "{\"model\":\"{{MODEL}}\",\"input\":\"{{PROMPT}}\",\"stream\":true,\"unknown\":9007199254740993123456789}" });
    await Post("tasks/save", new { config = new { id = "task", name = "Loopback", siteId = "site", keyId = key!["id"]!.GetValue<string>(),
        templateId = "simple", model = "MODEL", mode = "queue_then_keepalive", queue = new { intervalSeconds = .02 }, keepalive = new { heartbeatSeconds = .03 } } });
    await Post("settings/save", new
    {
        settings = new { telegramEnabled = true, barkEnabled = true, barkServer = origin + "/notifications/bark" },
        secrets = new { telegramToken = "123456789:LOCAL-test-token", telegramChatId = "CHAT-loopback", barkKey = "BARK-loopback" }
    });
    using (var invalid = await client.PostAsJsonAsync("/api/plugins/router-tasks/sites/save", new { id = "invalid", baseUrl = "invalid" }))
        Check(invalid.StatusCode == HttpStatusCode.BadRequest, "无效配置返回 JSON 错误");
    using (var missing = await client.GetAsync("/api/plugins/router-tasks/tasks/detail?id=missing"))
        Check(missing.StatusCode == HttpStatusCode.NotFound, "不存在的任务返回 JSON 错误");
    using (var conflict = await client.PostAsJsonAsync("/api/plugins/router-tasks/sites/save", new { id = "site", revision = 0, name = "Loopback", baseUrl = origin + "/upstream", route = "direct" }))
        Check(conflict.StatusCode == HttpStatusCode.Conflict, "旧修订号返回 JSON 错误");
    await Post("tasks/action", new { ids = new[] { "task" }, action = "start" });
    await Until(async () => upstreamCalls >= 3 && (await State())["activeCount"]!.GetValue<int>() == 1);
    var state = await State();
    Check(state["tasks"]![0]!["runtime"]!["phase"]!.GetValue<string>() == "keepalive", "模型请求与三阶段状态");
    Check(state["tasks"]![0]!["runtime"]!["lastRoute"]!.GetValue<string>() == "pool"
        && state["tasks"]![0]!["runtime"]!["lastProxy"]!["id"]!.GetValue<string>() == "chosen", "后台探针记录实际固定代理节点");
    Check(!state.ToJsonString().Contains("TOKEN-loopback-only", StringComparison.Ordinal), "管理员状态不返回凭据");
    await Until(async () => (await State())["notifications"]!.AsArray().Count(n => n!["state"]!.GetValue<string>() == "sent") == 2);
    Check(notificationDeliveries.Count == 2 && notificationDeliveries.Distinct().Count() == 2,
        "实际插件首次进入后通过回环 HTTP 投递 Telegram 与 Bark，分别保存成功回执");
    weakContexts.AddRange(Contexts());
    var previous = catalog.Get("router-tasks")!.DirectoryPath;
    await catalog.ReloadAsync("router-tasks");
    await Until(() => Task.FromResult(upstreamActive == 0));
    state = await State();
    Check(catalog.Get("router-tasks")!.DirectoryPath != previous, "热更新更换程序集快照");
    Check(state["activeCount"]!.GetValue<int>() == 0 && state["tasks"]![0]!["runtime"]!["status"]!.GetValue<string>() == "paused", "热更新暂停任务并释放请求");
    await Until(() => Task.FromResult(manualProxy.Active == 0));
    Check(manualProxy.Cancelled > 0, "热更新取消经真实宿主代理连接发送的 SSE 请求");
    Check(state["tasks"]![0]!["runtime"]!["totalAttempts"]!.GetValue<long>() == 3 && !state["tasks"]![0]!["runtime"]!["sessionConfirmed"]!.GetValue<bool>(), "计数保留且历史成功不冒充保活");
    Check(state["notifications"]!.AsArray().All(n => n!["state"]!.GetValue<string>() == "sent") && notificationDeliveries.Count == 2,
        "插件重载保留通知回执，不重复发送");
    var reloadGcAttempts = await CollectContextsAsync(weakContexts);
    Check(weakContexts.All(w => !w.IsAlive), "热更新后旧上下文释放，新版本保持加载");
    await Post("tasks/action", new { ids = new[] { "task" }, action = "resume" });
    await Until(() => Task.FromResult(upstreamCalls == 4));
    weakContexts.AddRange(Contexts());
    await catalog.SetEnabledAsync("router-tasks", false, default);
    await Until(() => Task.FromResult(upstreamActive == 0));
    await Until(() => Task.FromResult(manualProxy.Active == 0));
    Check(catalog.Get("router-tasks")?.State == "Disabled", "禁用插件取消后台请求");
    var disableGcAttempts = await CollectContextsAsync(weakContexts);
    Check(weakContexts.All(w => !w.IsAlive), "禁用插件释放上下文");
    await catalog.SetEnabledAsync("router-tasks", true, default);
    state = await State(); Check(state["activeCount"]!.GetValue<int>() == 0 && upstreamCalls == 4, "重新启用不自动重放");
    var template = await Get("templates/detail?id=simple");
    Check(template["body"]!.GetValue<string>().Contains("9007199254740993123456789", StringComparison.Ordinal), "宿主 JSON 桥保持正文精度");
    weakContexts.AddRange(Contexts());
    await catalog.RemovePackageAsync("router-tasks", default);
    Check(catalog.Get("router-tasks") is null && registry.Get("router-tasks") is null, "删除插件注销目录与平台");
    Check(File.Exists(Path.Combine(dataPath, "state.json")) && accountData.Count == 2, "插件卸载保留独立业务数据、账号与通知凭据");
    await app.StopAsync();
    var gcAttempts = await CollectContextsAsync(weakContexts);
    Check(weakContexts.Count == 3, "记录首次加载、热更新和重新启用的三个上下文");
    Check(weakContexts.All(w => !w.IsAlive), "可回收程序集上下文释放");
    await File.WriteAllTextAsync(Path.Combine(root, "host-report.json"), JsonSerializer.Serialize(new { checks, upstreamCalls, upstreamCancelled, upstreamActive,
        proxyRequests = manualProxy.Requests + subscriptionProxy.Requests, proxyCancelled = manualProxy.Cancelled,
        proxyConnections = manualProxy.ConnectionCount + subscriptionProxy.ConnectionCount,
        notificationDeliveries = notificationDeliveries.ToArray(),
        unloadContexts = weakContexts.Count, collectedContexts = weakContexts.Count(w => !w.IsAlive), reloadGcAttempts, disableGcAttempts, gcAttempts,
        hostVersion = typeof(PluginCatalog).Assembly.GetName().Version!.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine(JsonSerializer.Serialize(new { passed = checks.Count, upstreamCalls, upstreamCancelled, upstreamActive, report = Path.Combine(root, "host-report.json") }));
}
finally { await app.StopAsync(); Environment.SetEnvironmentVariable("ROUTER2API_ROUTERTASKS_DATA_DIR", null); }

void Check(bool success, string name) { if (!success) throw new InvalidOperationException(name); checks.Add(name); }
Task<JsonNode> State() => Get("state");
async Task<JsonNode> Get(string route)
{
    using var response = await client.GetAsync("/api/plugins/router-tasks/" + route); var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new IOException($"GET {route} HTTP {(int)response.StatusCode}"); return JsonNode.Parse(text)!;
}
async Task<JsonNode?> Post(string route, object body)
{
    using var response = await client.PostAsJsonAsync("/api/plugins/router-tasks/" + route, body); var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new IOException($"POST {route} HTTP {(int)response.StatusCode}: {text}"); return JsonNode.Parse(text);
}
static async Task Until(Func<Task<bool>> condition)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    while (!await condition()) await Task.Delay(20, deadline.Token);
}
[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference[] Contexts() => AssemblyLoadContext.All.Where(c => c.Name?.StartsWith("plugin:Plugins.RouterTasks:", StringComparison.Ordinal) == true).Select(c => new WeakReference(c)).ToArray();

[MethodImpl(MethodImplOptions.NoInlining)]
static void AdvanceSerializerMetadata()
    => _ = new DefaultJsonTypeInfoResolver().GetTypeInfo(typeof(HostSerializationProbe), new JsonSerializerOptions());

static async Task<int> CollectContextsAsync(IReadOnlyList<WeakReference> contexts)
{
    // .NET JSON 反射访问器有一秒滑动缓存；下一次元数据访问触发到期清理。
    await Task.Delay(1200);
    AdvanceSerializerMetadata();
    var attempts = 0;
    for (; attempts < 40 && contexts.Any(w => w.IsAlive); attempts++)
    { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); await Task.Delay(100); }
    return attempts;
}

internal sealed class LoopbackOnlyHandler(HttpMessageHandler inner, Func<Uri?> telegramEndpoint) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        // 只把本验收的虚构 Bot 路由到本地模拟接口，其他非回环请求始终拒绝。
        if (request.Method == HttpMethod.Post && request.RequestUri is { Scheme: "https", Host: "api.telegram.org" } uri &&
            Uri.UnescapeDataString(uri.AbsolutePath) == "/bot123456789:LOCAL-test-token/sendMessage")
            request.RequestUri = telegramEndpoint() ?? throw new InvalidOperationException("本地通知接口尚未启动");
        return request.RequestUri?.IsLoopback == true ? base.SendAsync(request, token) : throw new InvalidOperationException("隔离宿主只允许回环 HTTP");
    }
}

/// <summary>接受 HTTP 代理请求并只转到本验收的回环上游，使用宿主真实 WebProxy 传输。</summary>
internal sealed class LoopbackHttpProxy : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HttpClient _forward = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private readonly ConcurrentDictionary<string, byte> _connections = new();
    private int _requests, _active, _cancelled;
    public int Port { get; private set; }
    public int Requests => Volatile.Read(ref _requests);
    public int Active => Volatile.Read(ref _active);
    public int Cancelled => Volatile.Read(ref _cancelled);
    public int ConnectionCount => _connections.Count;

    private LoopbackHttpProxy(string upstreamOrigin)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        _app = builder.Build();
        _app.Run(async context =>
        {
            if (context.Request.Host.Host != "upstream.invalid" || context.Request.Method is not ("GET" or "POST")
                || !(context.Request.Path.StartsWithSegments("/upstream") || context.Request.Path.StartsWithSegments("/proxy-upstream")))
            { context.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            _connections.TryAdd(context.Connection.Id, 0);
            Interlocked.Increment(ref _requests);
            Interlocked.Increment(ref _active);
            try
            {
                using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), upstreamOrigin + context.Request.Path + context.Request.QueryString);
                if (context.Request.Method == "POST")
                {
                    using var body = new MemoryStream();
                    await context.Request.Body.CopyToAsync(body, context.RequestAborted);
                    request.Content = new ByteArrayContent(body.ToArray());
                    if (context.Request.ContentType is { } contentType) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                }
                if (context.Request.Headers.Authorization.Count > 0)
                    request.Headers.TryAddWithoutValidation("Authorization", context.Request.Headers.Authorization.ToArray());
                using var response = await _forward.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                context.Response.StatusCode = (int)response.StatusCode;
                context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
                context.Response.ContentLength = response.Content.Headers.ContentLength;
                await context.Response.StartAsync(context.RequestAborted);
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            { Interlocked.Increment(ref _cancelled); }
            catch (IOException) when (context.RequestAborted.IsCancellationRequested)
            { Interlocked.Increment(ref _cancelled); }
            finally { Interlocked.Decrement(ref _active); }
        });
    }

    public static async Task<LoopbackHttpProxy> StartAsync(string upstreamOrigin)
    {
        var proxy = new LoopbackHttpProxy(upstreamOrigin);
        try
        {
            await proxy._app.StartAsync();
            proxy.Port = new Uri(proxy._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()).Port;
            return proxy;
        }
        catch { await proxy.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        _forward.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

internal sealed record HostSerializationProbe(int Value);
