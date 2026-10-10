using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Plugins.RouterTasks;
using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace RouterTasks.Tests;

internal sealed class TestClock : IRuntimeClock
{
    private long _ticks = new DateTimeOffset(2026, 10, 10, 0, 0, 0, TimeSpan.Zero).Ticks;
    public DateTimeOffset Now => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(double seconds) => Interlocked.Add(ref _ticks, TimeSpan.FromSeconds(seconds).Ticks);
    public async Task Delay(TimeSpan delay, CancellationToken token) { token.ThrowIfCancellationRequested(); Advance(delay.TotalSeconds); await Task.Yield(); }
}
internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
}
internal sealed class ScriptProbe(Func<int, bool, CancellationToken, Task<ProbeResult>> next) : IProbeClient
{
    private int _calls;
    public int Calls => Volatile.Read(ref _calls);
    public ConcurrentQueue<bool> Phases { get; } = [];
    public async Task<ProbeResult> ProbeAsync(RunSnapshot run, bool heartbeat, string prompt, Func<int, Task<bool>> accepted, CancellationToken token)
    {
        var index = Interlocked.Increment(ref _calls); Phases.Enqueue(heartbeat);
        var result = await next(index, heartbeat, token);
        if (result.Kind == ProbeKind.Accepted && !await accepted(result.Status ?? 200)) return new ProbeResult(ProbeKind.Lost);
        return result;
    }
    public static async Task<ProbeResult> Block(CancellationToken token) { await Task.Delay(Timeout.Infinite, token); return new ProbeResult(ProbeKind.Network); }
}
internal sealed class Fixture : IAsyncDisposable
{
    public static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "RouterTasks-tests"));
    public string Directory { get; } = Path.Combine(Root, Guid.NewGuid().ToString("N"));
    public TestClock Clock { get; } = new();
    public Mock<IPluginServices> Services { get; } = new();
    public Mock<IPluginAccounts> Accounts { get; } = new();
    public Mock<IPluginHttpServices> Http { get; } = new();
    public ConcurrentDictionary<string, Account> AccountData { get; } = new();
    public SiteConfig Site { get; } = new() { Id = "site", BaseUrl = "http://127.0.0.1", Name = "Loopback" };
    public RequestTemplate Template { get; } = new() { Id = "test", Headers = new() { ["Authorization"] = "Bearer {{TOKEN}}" },
        Body = new JsonObject { ["model"] = "{{MODEL}}", ["input"] = "{{PROMPT}}", ["stream"] = true } };
    public TaskConfig Config { get; } = new() { Id = "task", SiteId = "site", KeyId = "key", Model = "MODEL", TemplateId = "test" };
    public RouterTasksTerminal Plugin { get; }
    public Fixture(IProbeClient? probe = null, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? handler = null, string? directory = null,
        IRuntimeClock? runtimeClock = null)
    {
        if (directory is not null) Directory = directory;
        AccountData["key"] = new Account { Id = "key", PluginKey = "router-tasks", Platform = "router-tasks", CredentialVersion = 1,
            Credential = new CustomCredential(new Dictionary<string, string?> { ["type"] = "key", ["siteId"] = "site", ["token"] = "TOKEN-secret-test-only" }) };
        Accounts.Setup(a => a.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((string id, CancellationToken _) => AccountData.GetValueOrDefault(id));
        Accounts.Setup(a => a.ListAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => AccountData.Values.ToArray());
        Accounts.Setup(a => a.SaveAsync(It.IsAny<Account>(), It.IsAny<CancellationToken>())).ReturnsAsync((Account a, CancellationToken _) => { a.CredentialVersion = 1; AccountData[a.Id] = a; return a; });
        Accounts.Setup(a => a.CompareExchangeCredentialAsync(It.IsAny<string>(), It.IsAny<long>(), It.IsAny<Credential>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, long version, Credential credential, CancellationToken _) =>
            {
                if (!AccountData.TryGetValue(id, out var account) || account.CredentialVersion != version) return null;
                var updated = new Account { Id = id, PluginKey = account.PluginKey, Platform = account.Platform, Label = account.Label,
                    Credential = credential, CredentialVersion = version + 1, Status = account.Status }; AccountData[id] = updated; return updated;
            });
        Accounts.Setup(a => a.PatchAsync(It.IsAny<Account>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Account a, IReadOnlyList<string> _, CancellationToken _) => { AccountData[a.Id].Label = a.Label; return AccountData[a.Id]; });
        Accounts.Setup(a => a.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns((string id, CancellationToken token) => { AccountData.TryRemove(id, out _); return Task.CompletedTask; });
        Http.Setup(h => h.ApproveOriginAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        Http.Setup(h => h.CreateDirectClient(It.IsAny<PluginHttpClientOptions>())).Returns(() => handler is null
            ? new HttpClient(new Handler((_, _) => throw new InvalidOperationException("离线测试未配置 HTTP，禁止访问真实网络")))
            : new HttpClient(new Handler(handler)));
        Services.SetupGet(s => s.PluginKey).Returns("router-tasks");
        Services.SetupGet(s => s.Accounts).Returns(Accounts.Object); Services.SetupGet(s => s.Http).Returns(Http.Object);
        Services.SetupGet(s => s.Log).Returns(Mock.Of<IPluginLogSink>());
        Plugin = new RouterTasksTerminal(Services.Object, Directory, runtimeClock ?? Clock, probe);
    }
    public async Task SeedAsync()
    {
        await Plugin.Store.UpdateAsync(d => { d.Sites.Add(Site); d.Templates.Add(Template); d.Keys.Add(new KeyInfo { Id = "key", SiteId = "site" });
            d.Tasks.Add(new TaskRecord { Config = Config, ConfigRevision = 1, Runtime = new TaskRuntime { Phase = Config.Mode == "keepalive" ? "keepalive" : "queue" } }); return true; });
    }
    public async Task<TaskRecord> TaskAsync() => TaskStore.Find(await Plugin.Store.ReadAsync(), Config.Id);
    public static async Task Until(Func<Task<bool>> condition, int milliseconds = 10000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!await condition()) { if (DateTime.UtcNow >= until) Assert.Fail("等待测试状态超时"); await Task.Delay(10); }
    }
    public static PluginHttpContext Context(object? body = null, string? id = null) => new() { PluginKey = "router-tasks", Platform = "router-tasks", Body = body,
        Query = id is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["id"] = id } };
    public async ValueTask DisposeAsync()
    {
        await Plugin.StopAsync(CancellationToken.None);
        var resolved = Path.GetFullPath(Directory);
        if (resolved.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && System.IO.Directory.Exists(resolved)) System.IO.Directory.Delete(resolved, true);
    }
    public static HttpResponseMessage Response(int status, string content, string type = "text/event-stream") => new((HttpStatusCode)status)
    { Content = new StringContent(content, System.Text.Encoding.UTF8, type) };
}

internal sealed class ManualClock : IRuntimeClock
{
    private readonly object _gate = new();
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-10T00:00:00Z");
    private readonly List<(DateTimeOffset At, TaskCompletionSource Completion)> _pending = [];
    public DateTimeOffset Now { get { lock (_gate) return _now; } }
    public int Pending { get { lock (_gate) return _pending.Count; } }
    public async Task Delay(TimeSpan delay, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _pending.Add((_now + delay, completion));
        try { await completion.Task.WaitAsync(token); }
        finally { lock (_gate) _pending.RemoveAll(item => item.Completion == completion); }
    }
    public void Advance(double seconds)
    {
        lock (_gate)
        {
            _now = _now.AddSeconds(seconds);
            foreach (var pending in _pending.Where(item => item.At <= _now)) pending.Completion.TrySetResult();
        }
    }
}
