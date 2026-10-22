using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Domain;
using Router.Contracts.Host;
using Router.Contracts.Pipeline;
using Router.Contracts.Plugins;

[assembly: PluginContract(MinVersion = "2.0", MaxVersion = "2.0")]

namespace Plugins.RouterTasks;

[PipelinePlugin(PipelineStage.Terminal, 100, "RouterTasks")]
[PlatformAdapter("router-tasks", PluginKey = "router-tasks", DisplayName = "RouterTasks")]
[CredentialSchema(CredentialKind.Custom)]
[ModelCache(Disabled = true)]
public sealed partial class RouterTasksTerminal : IPlatformTerminal, IPluginModule, IPluginMainPageProvider
{
    public const string Version = "0.3.3";
    private readonly IPluginServices _host;
    private readonly NetworkClientFactory _network;
    private readonly CredentialVault _vault;
    private readonly SourcePromptService _sources;
    private readonly NotificationService _notifications;
    private readonly IRuntimeClock _clock;
    private readonly SemaphoreSlim _management = new(1, 1);
    private readonly CancellationTokenSource _httpShutdown = new();
    private bool _stopped;
    internal TaskStore Store { get; }
    internal TaskEngine Engine { get; }

    public RouterTasksTerminal(IPluginHost host) : this(host.Services,
        Environment.GetEnvironmentVariable("ROUTER2API_ROUTERTASKS_DATA_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "data", "plugins", "router-tasks"), new RuntimeClock(), serviceHost: host) { }

    internal RouterTasksTerminal(IPluginServices services, string directory, IRuntimeClock clock, IProbeClient? probe = null, IPluginHost? serviceHost = null)
    {
        _host = services; _clock = clock;
        _network = new NetworkClientFactory(services.Http, serviceHost);
        Store = new TaskStore(directory, TemplateRenderer.Presets, clock);
        _vault = new CredentialVault(services.Accounts);
        _sources = new SourcePromptService(Store, _network, clock);
        Engine = new TaskEngine(Store, probe ?? new HttpProbeClient(_network, clock), clock, ResolveAsync, _sources.GetAsync);
        _notifications = new NotificationService(Store, _vault, services.Http, clock);
    }
    public void Configure(IPluginBuilder builder) => builder.ProxyPolicy(policy => policy.MaxAttempts(1));
    // candidate.StartAsync 发生在旧插件排空之前。这里不能打开存储、暂停旧任务或发送探针。
    public ValueTask StartAsync(PluginStartContext context, CancellationToken token) => ValueTask.CompletedTask;
    public async ValueTask StopAsync(CancellationToken token)
    {
        await _httpShutdown.CancelAsync();
        await _management.WaitAsync();
        try
        {
            if (_stopped) return; _stopped = true;
            await Engine.DisposeAsync();
            await _notifications.DisposeAsync();
            await Store.DisposeAsync();
        }
        finally { _management.Release(); }
    }
    public Task<IReadOnlyList<ModelDescriptor>> GetModelsAsync(ModelQueryContext context, CancellationToken token)
        => Task.FromResult<IReadOnlyList<ModelDescriptor>>([]);
    public Task<PluginInvocationResult> InvokeAsync(PluginAttemptContext context)
        => Task.FromException<PluginInvocationResult>(new NotSupportedException("请通过账号任务执行模型请求"));
    public Task<CredentialValidationResult> ValidateCredentialAsync(Credential credential, CancellationToken token)
        => Task.FromResult(new CredentialValidationResult(credential is CustomCredential custom && custom.Fields.GetValueOrDefault("type") is "key" or "notifications"));
    public PluginMainPage GetMainPage() => new("RouterTasks · 请求排队与保活", Asset("index.html")
        .Replace("/*__STYLE__*/", Asset("styles.css"), StringComparison.Ordinal)
        .Replace("/*__CORE__*/", Asset("core.js"), StringComparison.Ordinal)
        .Replace("/*__SCRIPT__*/", Asset("app.js"), StringComparison.Ordinal), Version);
    private static string Asset(string name)
    {
        using var stream = typeof(RouterTasksTerminal).Assembly.GetManifestResourceStream($"Plugins.RouterTasks.Web.{name}")
            ?? throw new InvalidOperationException("缺少页面资源：" + name);
        using var reader = new StreamReader(stream); return reader.ReadToEnd();
    }

    [PluginEndpoint("GET", "state")]
    public Task<PluginResult> StateAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var data = await Store.ReadAsync(context.CancellationToken);
        return context.Ok(new { serverNow = _clock.Now, version = Version, data.Revision, data.Sites, data.Keys, data.Settings, data.Stats,
            templates = data.Templates.Select(t => new { t.Id, t.Name, t.Protocol, t.Path }),
            tasks = data.Tasks.Select(t => new { config = t.Config with { RequestOverride = null }, t.ConfigRevision,
                hasRequestOverride = t.Config.RequestOverride is not null, runtime = t.Runtime with { Lanes = t.Runtime.Lanes.Take(8).ToList() }, totalLanes = t.Runtime.Lanes.Count }),
            notifications = data.Notifications.TakeLast(100).Reverse(), activeCount = Engine.ActiveCount, engineErrors = Engine.Failures,
            notificationError = _notifications.LastError, data.NotificationWarning, data.NotificationOverflowCount,
            notificationCredentials = await _vault.NotificationStatusAsync(context.CancellationToken) });
    });
    [PluginEndpoint("GET", "tasks/detail")]
    public Task<PluginResult> TaskDetailAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var document = await Store.ReadAsync(context.CancellationToken);
        var task = TaskStore.Find(document, QueryId(context));
        var offset = int.TryParse(context.Query.GetValueOrDefault("laneOffset"), out var n) ? Math.Max(0, n) : 0;
        return context.Ok(new { serverNow = _clock.Now, task.Config, task.ConfigRevision, task.Events, task.Snapshot,
            stats = document.Stats.Where(s => s.TaskId == task.Config.Id),
            notifications = document.Notifications.Where(n => n.TaskId == task.Config.Id).Reverse(),
            runtime = task.Runtime with { Lanes = task.Runtime.Lanes.Skip(offset).Take(100).ToList() }, totalLanes = task.Runtime.Lanes.Count });
    });
    [PluginEndpoint("GET", "templates/detail")]
    public Task<PluginResult> TemplateDetailAsync(PluginHttpContext context) => Api(context, async () => context.Ok(
        (await Store.ReadAsync(context.CancellationToken)).Templates.Find(t => t.Id == QueryId(context)) ?? throw new KeyNotFoundException("模板不存在")));

    [PluginEndpoint("POST", "sites/save")]
    public Task<PluginResult> SaveSiteAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var site = JsonData.Read<SiteConfig>(context.Body); Validation.Site(site);
        var old = (await Store.ReadAsync(context.CancellationToken)).Sites.Find(s => s.Id == site.Id);
        if (old is not null && old.Revision != site.Revision) throw new ConflictException("站点配置已更新");
        await RejectActiveReferencesAsync(t => t.Config.SiteId == site.Id, context.CancellationToken);
        await _host.Http.ApproveOriginAsync(Validation.BaseUri(site.BaseUrl).GetLeftPart(UriPartial.Authority), context.CancellationToken);
        return context.Ok(await Store.UpdateAsync(document =>
        {
            site.Revision = (old?.Revision ?? 0) + 1;
            document.Sites.RemoveAll(s => s.Id == site.Id); document.Sites.Add(site);
            if (old?.BaseUrl != site.BaseUrl)
                foreach (var key in document.Keys.Where(k => k.SiteId == site.Id))
                { key.AuthStatus = "unchecked"; key.Models = []; key.UsageAt = null; key.UsedUsd = key.LimitUsd = null; key.Unlimited = false; }
            return site;
        }, context.CancellationToken));
    }, true);
    [PluginEndpoint("POST", "sites/delete")]
    public Task<PluginResult> DeleteSiteAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var input = JsonData.Read<IdInput>(context.Body);
        Validation.Id(input.Id);
        await Store.UpdateAsync(document =>
        {
            if (document.Tasks.Any(t => t.Config.SiteId == input.Id) || document.Keys.Any(k => k.SiteId == input.Id)) throw new ConflictException("先删除引用站点的任务和 Key");
            document.Sites.RemoveAll(s => s.Id == input.Id); return true;
        }, context.CancellationToken); return context.Ok(new { success = true });
    }, true);
    [PluginEndpoint("POST", "keys/save")]
    public Task<PluginResult> SaveKeyAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var input = JsonData.Read<SaveKeyInput>(context.Body);
        if (!(await Store.ReadAsync(context.CancellationToken)).Sites.Any(s => s.Id == input.SiteId)) throw new FormatException("站点不存在");
        if (input.Id is { } id) await RejectActiveReferencesAsync(t => t.Config.KeyId == id, context.CancellationToken);
        var saved = await _vault.SaveKeyAsync(input, context.CancellationToken);
        await Store.UpdateAsync(document => { document.Keys.RemoveAll(k => k.Id == saved.Id); document.Keys.Add(saved); return true; }, context.CancellationToken);
        return context.Ok(saved);
    }, true);
    [PluginEndpoint("POST", "keys/delete")]
    public Task<PluginResult> DeleteKeyAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var input = JsonData.Read<IdInput>(context.Body);
        Validation.Id(input.Id);
        if ((await Store.ReadAsync(context.CancellationToken)).Tasks.Any(t => t.Config.KeyId == input.Id)) throw new ConflictException("先删除或修改引用该 Key 的任务");
        await _vault.DeleteKeyAsync(input.Id, context.CancellationToken);
        await Store.UpdateAsync(document => { document.Keys.RemoveAll(k => k.Id == input.Id); return true; }, context.CancellationToken);
        return context.Ok(new { success = true });
    }, true);

    [PluginEndpoint("POST", "keys/reconcile")]
    public Task<PluginResult> ReconcileKeysAsync(PluginHttpContext context) => Api(context, async () =>
    {
        if (Engine.ActiveCount > 0) throw new ConflictException("请先暂停任务再对账 Key 元数据");
        var keys = await _vault.ListKeysAsync(context.CancellationToken);
        var restored = await Store.UpdateAsync(document =>
        {
            var count = 0;
            foreach (var key in keys)
            {
                var old = document.Keys.Find(k => k.Id == key.Id);
                if (old is not null && old.CredentialVersion == key.CredentialVersion) continue;
                document.Keys.RemoveAll(k => k.Id == key.Id); document.Keys.Add(key); count++;
            }
            document.Keys.RemoveAll(k => !keys.Any(a => a.Id == k.Id));
            return count;
        }, context.CancellationToken);
        return context.Ok(new { restored });
    }, true);

    [PluginEndpoint("POST", "keys/refresh")]
    public Task<PluginResult> RefreshKeyAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var id = JsonData.Read<IdInput>(context.Body).Id;
        Validation.Id(id);
        var document = await Store.ReadAsync(context.CancellationToken);
        var key = document.Keys.Find(k => k.Id == id) ?? throw new KeyNotFoundException("Key 不存在");
        var site = document.Sites.Find(s => s.Id == key.SiteId) ?? throw new FormatException("站点不存在");
        var secret = await _vault.KeyAsync(id, site.Id, context.CancellationToken);
        key = await QueryKeyAsync(key, site, secret, context.CancellationToken);
        await Store.UpdateAsync(state => { state.Keys.RemoveAll(k => k.Id == id); state.Keys.Add(key); return true; });
        return context.Ok(key);
    }, true);

    private async Task<KeyInfo> QueryKeyAsync(KeyInfo key, SiteConfig site, string secret, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _httpShutdown.Token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        key.LastRoute = site.Route; key.LastProxy = null;
        using var client = await _network.CreateClientAsync(site, timeout.Token, proxy => key.LastProxy = proxy);
        async Task<JsonNode?> Get(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, Validation.Endpoint(site.BaseUrl, path));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var body = await HttpProbeClient.ReadLimitedAsync(response.Content, 1024 * 1024, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new IOException(TextOutput.Summary($"HTTP {(int)response.StatusCode} · {body}"));
            return JsonNode.Parse(body);
        }
        try
        {
            var models = await Get("models");
            key.Models = models?["data"]?.AsArray().Select(model => model?["id"]?.ToString()).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct().ToArray() ?? [];
            key.AuthStatus = "ready"; key.Error = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _httpShutdown.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException)
        { key.AuthStatus = "error"; key.Error = TextOutput.Summary(error.Message); }
        if (key.AuthStatus == "ready")
        {
            try
            {
                var usage = await Get("dashboard/billing/usage");
                var used = usage?["total_usage"]?.GetValue<decimal>() ?? throw new FormatException("额度响应缺少 total_usage；保留旧读数");
                key.UsedUsd = used / 100m;
                key.UsageAt = _clock.Now; key.UsageError = null;
                try
                {
                    var subscription = await Get("dashboard/billing/subscription");
                    key.LimitUsd = subscription?["hard_limit_usd"]?.GetValue<decimal>() ?? throw new FormatException("额度响应缺少 hard_limit_usd；保留旧读数");
                    key.Unlimited = key.LimitUsd == 100_000_000m;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested || _httpShutdown.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException or FormatException)
                { key.UsageError = TextOutput.Summary(error.Message); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || _httpShutdown.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException or FormatException)
            { key.UsageError = TextOutput.Summary(error.Message); }
        }
        return key;
    }

    [PluginEndpoint("POST", "templates/save")]
    public Task<PluginResult> SaveTemplateAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var template = JsonData.Read<RequestTemplate>(context.Body); Validation.Template(template);
        return context.Ok(await Store.UpdateAsync(document => { document.Templates.RemoveAll(t => t.Id == template.Id); document.Templates.Add(template); return template; }, context.CancellationToken));
    }, true);
    [PluginEndpoint("POST", "tasks/save")]
    public Task<PluginResult> SaveTaskAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var input = JsonData.Read<SaveTaskInput>(context.Body); Validation.Config(input.Config);
        if (Engine.IsRunning(input.Config.Id)) throw new ConflictException("请先暂停任务再修改配置");
        _ = await ResolveAsync(input.Config, context.CancellationToken);
        return context.Ok(await Store.UpdateAsync(document =>
        {
            var task = document.Tasks.Find(t => t.Config.Id == input.Config.Id);
            if (task is null)
            {
                task = new TaskRecord { Config = input.Config, ConfigRevision = 1,
                    Runtime = TaskStore.CreateRuntime(input.Config.Mode, _clock.Now) }; document.Tasks.Add(task);
            }
            else
            {
                if (input.ExpectedRevision != task.ConfigRevision) throw new ConflictException("任务配置已变化，请重新读取");
                TaskStore.ApplyConfig(document, task, input.Config, _clock.Now); task.ConfigRevision++;
            }
            return task;
        }, context.CancellationToken));
    }, true);
    [PluginEndpoint("POST", "tasks/delete")]
    public Task<PluginResult> DeleteTaskAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var id = JsonData.Read<IdInput>(context.Body).Id;
        Validation.Id(id);
        if (Engine.IsRunning(id)) throw new ConflictException("先暂停或取消任务");
        await Store.UpdateAsync(document => { document.Tasks.RemoveAll(t => t.Config.Id == id); return true; }, context.CancellationToken);
        return context.Ok(new { success = true });
    }, true);
    [PluginEndpoint("POST", "tasks/action")]
    public Task<PluginResult> TaskActionAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var input = JsonData.Read<TaskActionInput>(context.Body);
        if (input.Ids is null || input.Ids.Length == 0) throw new FormatException("请选择任务");
        var results = new List<object>();
        foreach (var id in input.Ids.Distinct())
        {
            try { await Engine.ActionAsync(id, input.Action, context.CancellationToken); results.Add(new { id, success = true }); }
            catch (Exception error) when (error is FormatException or ConflictException or KeyNotFoundException)
            { results.Add(new { id, success = false, error = error.Message }); }
        }
        _notifications.Start();
        // 管理操作摘要交给宿主日志服务，不把 Key、Headers、Body 或完整响应写入宿主日志。
        if (_host.Log is { } log)
        {
            try { await log.WriteAsync(new PluginLog { PluginKey = "router-tasks", Platform = "router-tasks", EventType = "router-tasks.action",
                Message = $"任务操作 {TextOutput.Summary(input.Action)}，共 {input.Ids.Distinct().Count()} 项" }, context.CancellationToken); }
            catch (Exception error) when (error is not OperationCanceledException) { /* 本插件事件记录仍然可用，不重放已执行的操作。 */ }
        }
        return context.Ok(new { results });
    }, true);
    [PluginEndpoint("POST", "requests/preview")]
    public Task<PluginResult> PreviewAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var config = JsonData.Read<TaskConfig>(context.Body); Validation.Config(config);
        var snapshot = await ResolveAsync(config, context.CancellationToken);
        var prompt = await _sources.GetAsync(snapshot, context.CancellationToken);
        var request = TemplateRenderer.Render(snapshot, prompt, _clock.Now);
        return context.Ok(new { method = "POST", url = request.Url.AbsoluteUri, headers = request.Headers, body = request.Body.ToJsonString() });
    });
    [PluginEndpoint("POST", "settings/save")]
    public Task<PluginResult> SaveSettingsAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var input = JsonData.Read<SaveSettingsInput>(context.Body); Validation.Settings(input.Settings);
        ValidateNotificationCapacity(await Store.ReadAsync(context.CancellationToken), input.Settings);
        if (input.Secrets is { } secrets) await _vault.SaveNotificationsAsync(secrets, context.CancellationToken);
        await Store.UpdateAsync(document => { document.Settings = input.Settings; return true; }, context.CancellationToken);
        return context.Ok(await _vault.NotificationStatusAsync(context.CancellationToken));
    }, true);
    [PluginEndpoint("POST", "notifications/resume")]
    public Task<PluginResult> ResumeNotificationsAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var id = context.Body is null ? null : JsonData.Read<IdInput>(context.Body).Id;
        if (id is not null) Validation.Id(id);
        await Store.UpdateAsync(document =>
        {
            foreach (var item in document.Notifications.Where(n => n.State == "paused" && (id is null || n.TaskId == id))) { item.State = "queued"; item.NextAttemptAt = _clock.Now; }
            return true;
        }, context.CancellationToken);
        _notifications.Start(); return context.Ok(new { success = true });
    }, true);
    [PluginEndpoint("GET", "config/export")]
    public Task<PluginResult> ExportAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var document = await Store.ReadAsync(context.CancellationToken);
        return context.Ok(new ConfigExport(1, document.Sites, document.Templates, document.Tasks.Select(t => t.Config).ToList(), document.Settings));
    });
    [PluginEndpoint("POST", "config/import")]
    public Task<PluginResult> ImportAsync(PluginHttpContext context) => Api(context, async () =>
    {
        if (Engine.ActiveCount > 0) throw new ConflictException("导入前请暂停全部任务");
        var input = JsonData.Read<ConfigExport>(context.Body);
        if (input.SchemaVersion != 1) throw new FormatException("不支持的配置版本");
        if (input.Sites is null || input.Templates is null || input.Tasks is null) throw new FormatException("导入缺少站点、模板或任务数组");
        Validation.Settings(input.Settings);
        foreach (var site in input.Sites) Validation.Site(site);
        foreach (var template in input.Templates) Validation.Template(template);
        foreach (var config in input.Tasks) Validation.Config(config);
        if (input.Sites.Select(s => s.Id).Distinct().Count() != input.Sites.Count || input.Tasks.Select(t => t.Id).Distinct().Count() != input.Tasks.Count
            || input.Templates.Select(t => t.Id).Distinct().Count() != input.Templates.Count) throw new FormatException("导入包含重复 ID");
        foreach (var site in input.Sites) await _host.Http.ApproveOriginAsync(Validation.BaseUri(site.BaseUrl).GetLeftPart(UriPartial.Authority), context.CancellationToken);
        await Store.UpdateAsync(document =>
        {
            ValidateNotificationCapacity(document, input.Settings);
            document.Settings = input.Settings;
            foreach (var site in input.Sites) { document.Sites.RemoveAll(s => s.Id == site.Id); document.Sites.Add(site); }
            foreach (var template in input.Templates) { document.Templates.RemoveAll(t => t.Id == template.Id); document.Templates.Add(template); }
            foreach (var config in input.Tasks)
            {
                if (!document.Sites.Any(s => s.Id == config.SiteId) || config.RequestOverride is null && !document.Templates.Any(t => t.Id == config.TemplateId))
                    throw new FormatException("任务引用的站点或模板不存在");
                var old = document.Tasks.Find(t => t.Config.Id == config.Id);
                if (old is not null)
                {
                    TaskStore.ApplyConfig(document, old, config, _clock.Now); old.ConfigRevision++; old.Runtime.Status = "paused"; old.Runtime.Generation = null;
                    old.Runtime.NextAttemptAt = null; old.Runtime.SessionConfirmed = false; old.Runtime.PauseReason = "导入后暂停；请确认 Key 绑定";
                }
                else document.Tasks.Add(new TaskRecord { Config = config, ConfigRevision = 1,
                    Runtime = TaskStore.CreateRuntime(config.Mode, _clock.Now) with { PauseReason = "导入后暂停；请确认 Key 绑定" } });
            }
            return true;
        }, context.CancellationToken);
        return context.Ok(new { success = true, warning = "不导入凭据；跨宿主导入后请重新选择 Key。" });
    }, true);

    internal async Task<RunSnapshot> ResolveAsync(TaskConfig config, CancellationToken token)
    {
        Validation.Config(config);
        var document = await Store.ReadAsync(token);
        var site = document.Sites.Find(s => s.Id == config.SiteId) ?? throw new FormatException("站点不存在");
        var template = config.RequestOverride ?? document.Templates.Find(t => t.Id == config.TemplateId) ?? throw new FormatException("模板不存在");
        Validation.Site(site); Validation.Template(template);
        var secret = await _vault.KeyAsync(config.KeyId, config.SiteId, token);
        return new RunSnapshot(JsonData.Clone(config), site, JsonData.Clone(template), secret, JsonData.Clone(config.Settings ?? document.Settings), "");
    }
    private async Task RejectActiveReferencesAsync(Func<TaskRecord, bool> filter, CancellationToken token)
    { if ((await Store.ReadAsync(token)).Tasks.Any(t => filter(t) && Engine.IsRunning(t.Config.Id))) throw new ConflictException("请先暂停引用此配置的运行任务"); }
    private static string QueryId(PluginHttpContext context) { var id = context.Query.GetValueOrDefault("id") ?? ""; Validation.Id(id); return id; }
    private static void ValidateNotificationCapacity(StoreDocument document, PluginSettings settings)
    {
        if (document.Notifications.Count(n => n.State is not ("sent" or "dead")) > settings.MaxNotificationRecords)
            throw new FormatException("记录上限不能小于现有待处理通知数；先恢复投递再减少上限");
    }
    private async Task<PluginResult> Api(PluginHttpContext context, Func<Task<PluginResult>> action, bool mutation = false)
    {
        // 读写管理调用均参加排空；关闭时不允许 GET 与存储释放发生竞争。
        await _management.WaitAsync(context.CancellationToken);
        try
        {
            if (_stopped) return DetachResponse(context.Json(503, new { error = "插件已停止" }));
            return DetachResponse(await action());
        }
        catch (ConflictException error) { return DetachResponse(context.Json(409, new { error = error.Message })); }
        catch (KeyNotFoundException error) { return DetachResponse(context.Json(404, new { error = error.Message })); }
        catch (ProxySelectionException error) { return DetachResponse(context.Json(503, new { error = error.Message, code = "proxy_unavailable" })); }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException)
        { return DetachResponse(context.BadRequest(TextOutput.Summary(error.Message))); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return DetachResponse(context.Json(503, new { error = TextOutput.Summary(error.Message), storageUnavailable = true })); }
        finally { _management.Release(); }
    }

    // 宿主只接收独立 JSON 值，不在宿主序列化缓存中保留可回收的插件类型。
    private static PluginResult DetachResponse(PluginResult result) => new()
    {
        StatusCode = result.StatusCode,
        ContentType = result.ContentType,
        Body = result.Body is null ? null : JsonSerializer.SerializeToElement(result.Body, JsonData.Options)
    };
}

public sealed record IdInput(string Id);
public sealed record SaveTaskInput(TaskConfig Config, int? ExpectedRevision = null);
public sealed record TaskActionInput(string[] Ids, string Action);
public sealed record SaveSettingsInput(PluginSettings Settings, NotificationSecrets? Secrets = null);
public sealed record ConfigExport(int SchemaVersion, List<SiteConfig> Sites, List<RequestTemplate> Templates, List<TaskConfig> Tasks, PluginSettings Settings);
