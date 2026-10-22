using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Plugins.RouterTasks;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace RouterTasks.Tests;

[TestClass]
public sealed class UsageLogTests
{
    private const string Key = "sk-log-test-only&+/?";
    private const string LogBody = """
        {"success":true,"data":[
          {"id":7,"created_at":1791590400,"type":2,"model_name":"model-a","token_name":"my-key","quota":12500,"prompt_tokens":120,"completion_tokens":30,"use_time":4,"is_stream":true,"group":"default","content":"request","other":"{\"cache_tokens\":10}"},
          {"id":8,"created_at":1791590500,"type":5,"content":"failed"}]}
        """;

    private static async Task Seed(Fixture f)
    {
        await f.SeedAsync();
        f.AccountData["key"].Credential = new CustomCredential(new Dictionary<string, string?>
        { ["type"] = "key", ["siteId"] = "site", [CredentialVault.ApiTokenField] = Key });
    }

    [TestMethod]
    public async Task QueryUsesSavedCredentialAndDoesNotChangeAccountOrTaskState()
    {
        var calls = 0;
        await using var f = new Fixture(handler: (request, _) =>
        {
            calls++;
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("/api/log/token", request.RequestUri!.AbsolutePath);
            Assert.AreEqual("?key=" + Uri.EscapeDataString(Key), request.RequestUri.Query);
            Assert.AreEqual("Bearer " + Key, request.Headers.Authorization!.ToString());
            return Task.FromResult(Fixture.Response(200, LogBody, "application/json"));
        });
        await Seed(f);
        var before = JsonSerializer.Serialize(await f.Plugin.Store.ReadAsync(), JsonData.Options);
        var result = await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")));
        Assert.AreEqual(200, result.StatusCode);
        var value = JsonData.Read<TokenUsageLogs>(result.Body);
        Assert.HasCount(2, value.Items);
        Assert.AreEqual("8", value.Items[0].Id);
        var consume = value.Items[1];
        Assert.AreEqual("model-a", consume.ModelName);
        Assert.AreEqual(12500m, consume.Quota);
        Assert.AreEqual(120m, consume.PromptTokens);
        Assert.AreEqual(30m, consume.CompletionTokens);
        Assert.IsTrue(consume.IsStream);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1791590400), consume.At);
        Assert.AreEqual(f.Clock.Now, value.QueriedAt);
        Assert.AreEqual("direct", value.LastRoute); Assert.IsNull(value.LastProxy);
        Assert.AreEqual(1, calls);
        Assert.AreEqual(before, JsonSerializer.Serialize(await f.Plugin.Store.ReadAsync(), JsonData.Options));
        Assert.IsFalse(JsonSerializer.Serialize(result.Body).Contains(Key, StringComparison.Ordinal));
        f.Http.Verify(h => h.ApproveOriginAsync("http://127.0.0.1", It.IsAny<CancellationToken>()), Times.Once);
        f.Http.Verify(h => h.CreateDirectClient(It.Is<PluginHttpClientOptions>(o => !o.AllowAutoRedirect)), Times.Once);
    }

    [TestMethod]
    [DataRow("https://example.invalid", "/api/log/token")]
    [DataRow("https://example.invalid/v1/", "/api/log/token")]
    [DataRow("https://example.invalid/gateway/v1", "/gateway/api/log/token")]
    [DataRow("https://example.invalid/gateway/", "/gateway/api/log/token")]
    public void EndpointPreservesDeploymentPrefixAndEscapesTheWholeKey(string baseUrl, string path)
    {
        var endpoint = RouterTasksTerminal.UsageLogEndpoint(baseUrl, Key);
        Assert.AreEqual("example.invalid", endpoint.Host);
        Assert.AreEqual(path, endpoint.AbsolutePath);
        Assert.AreEqual("?key=" + Uri.EscapeDataString(Key), endpoint.Query);
    }

    [TestMethod]
    public void EmptyAndWrappedResultsAreSupportedButMissingFieldsRemainUnknown()
    {
        Assert.HasCount(0, RouterTasksTerminal.ParseUsageLogs("{\"success\":true,\"data\":[]}", Key));
        var rows = RouterTasksTerminal.ParseUsageLogs("""
            {"success":true,"data":{"items":[{"id":"a","type":"2","created_at":"1791590400","quota":"2.5","prompt_tokens":"10"},{"created_at":1e20,"type":2.5}]}}
            """, Key);
        Assert.HasCount(2, rows);
        Assert.AreEqual(2, rows[0].Type); Assert.AreEqual(2.5m, rows[0].Quota);
        Assert.AreEqual(10m, rows[0].PromptTokens);
        Assert.IsNull(rows[0].CompletionTokens); Assert.IsNull(rows[0].IsStream);
        Assert.IsNull(rows[1].At); Assert.IsNull(rows[1].Type);
    }

    [TestMethod]
    [DataRow("<html>Login</html>")]
    [DataRow("{}")]
    [DataRow("{\"success\":true}")]
    [DataRow("{\"success\":true,\"data\":{}}")]
    [DataRow("{\"success\":true,\"data\":[null]}")]
    public async Task MalformedResponsesAreErrorsAndNeverMasqueradeAsEmptyLogs(string body)
    {
        await using var f = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(200, body, "application/json")));
        await Seed(f);
        var result = await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")));
        Assert.AreEqual(502, result.StatusCode);
        Assert.IsTrue(((JsonElement)result.Body!).TryGetProperty("error", out _));
    }

    [TestMethod]
    public async Task BusinessFailureAndSuccessfulLogTextRedactReflectedCredentials()
    {
        var message = Key + " " + Uri.EscapeDataString(Key) + " " + Key[3..];
        await using var f = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(200,
            JsonSerializer.Serialize(new { success = false, message }), "application/json")));
        await Seed(f);
        var result = await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")));
        Assert.AreEqual(502, result.StatusCode);
        var text = ((JsonElement)result.Body!).GetProperty("error").GetString()!;
        Assert.IsFalse(text.Contains(Key[3..], StringComparison.Ordinal));
        Assert.IsFalse(text.Contains(Uri.EscapeDataString(Key), StringComparison.Ordinal));
        var logs = RouterTasksTerminal.ParseUsageLogs(JsonSerializer.Serialize(new
        { success = true, data = new[] { new { content = message, other = message, model_name = message } } }), Key);
        Assert.IsFalse(JsonSerializer.Serialize(logs).Contains(Key[3..], StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow(404, "不支持")]
    [DataRow(403, "鉴权失败")]
    [DataRow(429, "频率")]
    [DataRow(302, "重定向")]
    [DataRow(500, "查询失败")]
    public async Task HttpFailuresReturnSafeActionableErrorsWithoutEchoingUpstreamBody(int status, string expected)
    {
        var calls = 0;
        await using var f = new Fixture(handler: (_, _) =>
        { calls++; return Task.FromResult(Fixture.Response(status, Key, "text/html")); });
        await Seed(f);
        var result = await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")));
        Assert.AreEqual(502, result.StatusCode);
        var text = ((JsonElement)result.Body!).GetProperty("error").GetString()!;
        StringAssert.Contains(text, expected); Assert.IsFalse(text.Contains(Key, StringComparison.Ordinal));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task OversizeResponseIsRejectedInsteadOfReturningTruncatedRecords()
    {
        var body = "{\"success\":true,\"data\":[],\"padding\":\"" + new string('x', 16 * 1024 * 1024) + "\"}";
        await using var f = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(200, body, "application/json")));
        await Seed(f);
        var result = await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")));
        Assert.AreEqual(502, result.StatusCode);
        StringAssert.Contains(((JsonElement)result.Body!).GetProperty("error").GetString()!, "16 MiB");
    }

    [TestMethod]
    public async Task LogsLargerThanTwoMiBAreReadInFullAndChallengeWordsInsideJsonDoNotTriggerRetries()
    {
        var calls = 0;
        var body = JsonSerializer.Serialize(new { success = true, data = new[] { new { id = 1, type = 2,
            content = "captcha acw_sc__v2 var arg1= 访问验证", other = new string('x', 2 * 1024 * 1024) } } });
        await using var f = new Fixture(handler: (_, _) => { calls++; return Task.FromResult(Fixture.Response(200, body, "application/json")); });
        await Seed(f);
        var result = await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")));
        Assert.AreEqual(200, result.StatusCode); Assert.AreEqual(1, calls);
        var log = JsonData.Read<TokenUsageLogs>(result.Body).Items.Single();
        StringAssert.Contains(log.Content, "captcha");
        Assert.AreEqual(16384, log.Other.Length);
    }

    [TestMethod]
    [DataRow(200)]
    [DataRow(403)]
    [DataRow(412)]
    public async Task StaticCookieChallengeRetriesOnceWithTheSameClientAndCredential(int challengeStatus)
    {
        var calls = 0;
        await using var f = new Fixture(handler: (request, _) =>
        {
            calls++;
            Assert.AreEqual(HttpMethod.Get, request.Method);
            Assert.AreEqual("Bearer " + Key, request.Headers.Authorization!.ToString());
            Assert.IsTrue(request.Headers.UserAgent.Count > 0);
            if (calls == 1)
            {
                Assert.IsFalse(request.Headers.Contains("Cookie"));
                return Task.FromResult(Fixture.Response(challengeStatus, CookieChallenge(), "text/html"));
            }
            Assert.AreEqual("acw_sc__v2=3000176000856006061501533003690027800375", request.Headers.GetValues("Cookie").Single());
            return Task.FromResult(Fixture.Response(200, LogBody, "application/json"));
        });
        await Seed(f);
        var before = JsonSerializer.Serialize(await f.Plugin.Store.ReadAsync(), JsonData.Options);
        var result = await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")));
        Assert.AreEqual(200, result.StatusCode); Assert.AreEqual(2, calls);
        f.Http.Verify(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Once);
        Assert.AreEqual(before, JsonSerializer.Serialize(await f.Plugin.Store.ReadAsync(), JsonData.Options));
        Assert.IsFalse(JsonSerializer.Serialize(result.Body).Contains("acw_sc__v2", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RepeatedOrUnsupportedChallengeStopsWithoutExecutingScriptsOrLooping()
    {
        foreach (var body in new[] { CookieChallenge(), "<html><script>captcha();</script></html>" })
        {
            var calls = 0;
            await using var f = new Fixture(handler: (_, _) => { calls++; return Task.FromResult(Fixture.Response(200, body, "text/html")); });
            await Seed(f);
            var result = await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")));
            Assert.AreEqual(502, result.StatusCode);
            Assert.AreEqual(body.Contains("arg1", StringComparison.Ordinal) ? 2 : 1, calls);
            StringAssert.Contains(((JsonElement)result.Body!).GetProperty("error").GetString()!, "验证");
        }
    }

    [TestMethod]
    [DataRow(200)]
    [DataRow(403)]
    public async Task WafRateLimitIsReportedWithoutAttemptingCookieRetries(int status)
    {
        var calls = 0;
        await using var f = new Fixture(handler: (_, _) =>
        { calls++; return Task.FromResult(Fixture.Response(status, "<html>http_ratelimit</html>", "text/html")); });
        await Seed(f);
        var result = await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")));
        Assert.AreEqual(502, result.StatusCode); Assert.AreEqual(1, calls);
        StringAssert.Contains(((JsonElement)result.Body!).GetProperty("error").GetString()!, "当前出口");
    }

    [TestMethod]
    public async Task ChallengeRetryCancellationPropagatesWithoutAnExtraRequest()
    {
        var calls = 0;
        using var cancel = new CancellationTokenSource();
        await using var f = new Fixture(handler: (request, _) =>
        {
            calls++;
            if (calls == 1) return Task.FromResult(Fixture.Response(200, CookieChallenge(), "text/html"));
            cancel.Cancel(); throw new OperationCanceledException(cancel.Token);
        });
        await Seed(f);
        var context = new PluginHttpContext { Body = new IdInput("task"), CancellationToken = cancel.Token };
        await Assert.ThrowsAsync<OperationCanceledException>(() => f.Plugin.QueryUsageLogsAsync(context));
        Assert.AreEqual(2, calls);
        Assert.AreEqual(200, (await f.Plugin.StateAsync(Fixture.Context())).StatusCode);
    }

    private static string CookieChallenge()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789+/";
        var encoded = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("3000176000856006061501533003690027800375"));
        var custom = string.Concat(encoded.Select(c => c == '=' ? '=' : alphabet["ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/".IndexOf(c)]));
        return "<html><script>var arg1='0000000000000000000000000000000000000000';var N=['" + custom + "'];var m=[" +
            string.Join(',', Enumerable.Range(1, 40)) + "];document.cookie='acw_sc__v2=';</script></html>";
    }

    [TestMethod]
    public async Task NetworkFailureDoesNotExposeCredentialBearingRequestUri()
    {
        await using var f = new Fixture(handler: (request, _) => throw new HttpRequestException(request.RequestUri!.ToString()));
        await Seed(f);
        var result = await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")));
        Assert.AreEqual(502, result.StatusCode);
        Assert.IsFalse(JsonSerializer.Serialize(result.Body).Contains(Key[3..], StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task UnavailablePoolDoesNotFallbackToDirect()
    {
        await using var f = new Fixture(); await Seed(f);
        await f.Plugin.Store.UpdateAsync(d => { d.Sites[0].Route = "pool"; return true; });
        var result = await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")));
        Assert.AreEqual(503, result.StatusCode);
        Assert.AreEqual("proxy_unavailable", ((JsonElement)result.Body!).GetProperty("code").GetString());
        f.Http.Verify(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
    }

    [TestMethod]
    public async Task SavedPoolSelectionUsesTheFixedNodeAndReturnsOnlyItsSafeSnapshot()
    {
        await using var f = new Fixture();
        var host = new Mock<IPluginHost>();
        var proxies = new Mock<IProxyStore>();
        var sources = new Mock<IProxySubscriptionService>();
        var clients = new Mock<IProxyHttpClientFactory>();
        host.Setup(h => h.GetServiceAsync<IProxyStore>()).ReturnsAsync(proxies.Object);
        host.Setup(h => h.GetServiceAsync<IProxySubscriptionService>()).ReturnsAsync(sources.Object);
        host.Setup(h => h.GetServiceAsync<IProxyHttpClientFactory>()).ReturnsAsync(clients.Object);
        var node = new ProxyEndpoint { Id = "fixed", SubscriptionId = "manual", Host = "127.0.0.1", Port = 3128,
            Username = "proxy-user-private", Password = "proxy-password-private" };
        proxies.Setup(p => p.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { node });
        sources.Setup(s => s.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ProxySubscription>());
        var calls = 0;
        clients.Setup(c => c.CreateClient(node, "router-tasks")).Returns(() => new HttpClient(new Handler((request, _) =>
        {
            calls++;
            Assert.AreEqual("/api/log/token", request.RequestUri!.AbsolutePath);
            Assert.AreEqual("Bearer " + Key, request.Headers.Authorization!.ToString());
            if (calls == 1) return Task.FromResult(Fixture.Response(200, CookieChallenge(), "text/html"));
            Assert.IsTrue(request.Headers.Contains("Cookie"));
            return Task.FromResult(Fixture.Response(200, LogBody, "application/json"));
        })));
        var plugin = new RouterTasksTerminal(f.Services.Object, f.Directory, f.Clock, serviceHost: host.Object);
        try
        {
            f.AccountData["key"].Credential = new CustomCredential(new Dictionary<string, string?>
            { ["type"] = "key", ["siteId"] = "site", [CredentialVault.ApiTokenField] = Key });
            await plugin.Store.UpdateAsync(d =>
            {
                d.Sites.Add(f.Site with { Route = "pool", ProxyId = "fixed", SubscriptionIds = ["manual"] });
                d.Keys.Add(new KeyInfo { Id = "key", SiteId = "site" });
                d.Tasks.Add(new TaskRecord { Config = f.Config }); return true;
            });
            var result = await plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")));
            Assert.AreEqual(200, result.StatusCode);
            var logs = JsonData.Read<TokenUsageLogs>(result.Body);
            Assert.AreEqual("pool", logs.LastRoute); Assert.AreEqual("fixed", logs.LastProxy!.Id);
            Assert.AreEqual("http://127.0.0.1:3128", logs.LastProxy.Address);
            var json = JsonSerializer.Serialize(result.Body);
            Assert.IsFalse(json.Contains("private", StringComparison.Ordinal));
            Assert.AreEqual(2, calls);
            clients.Verify(c => c.CreateClient(node, "router-tasks"), Times.Once);
            f.Http.Verify(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
        }
        finally { await plugin.StopAsync(default); }
    }

    [TestMethod]
    public async Task PluginShutdownCancelsTheQueryBeforeDisposingTheStore()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = new Fixture(handler: async (_, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Fixture.Response(200, LogBody); });
        await Seed(f);
        var pending = f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stop = f.Plugin.StopAsync(default);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        await stop.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(503, (await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("task")))).StatusCode);
    }

    [TestMethod]
    public async Task CallerCancellationIsPropagatedAndTheManagementLockIsReleased()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var f = new Fixture(handler: async (_, token) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return Fixture.Response(200, LogBody); });
        await Seed(f);
        using var cancel = new CancellationTokenSource();
        var context = new PluginHttpContext
        { Body = new IdInput("task"), CancellationToken = cancel.Token };
        var pending = f.Plugin.QueryUsageLogsAsync(context);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        Assert.AreEqual(200, (await f.Plugin.StateAsync(Fixture.Context())).StatusCode);
    }

    [TestMethod]
    public async Task UnknownAccountIsRejectedBeforeNetworkAccess()
    {
        await using var f = new Fixture(); await Seed(f);
        Assert.AreEqual(404, (await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("missing")))).StatusCode);
        Assert.AreEqual(400, (await f.Plugin.QueryUsageLogsAsync(Fixture.Context(new IdInput("invalid/id")))).StatusCode);
        f.Http.Verify(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>()), Times.Never);
    }
}
