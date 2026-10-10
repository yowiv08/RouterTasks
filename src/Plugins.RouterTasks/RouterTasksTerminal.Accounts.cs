using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Plugins.RouterTasks;

public sealed record SaveAccountInput(TaskConfig Config, SiteConfig Site, string? Token,
    long? CredentialVersion = null, int? ExpectedRevision = null, NotificationSecrets? Secrets = null);
public sealed record AccountDetail(TaskConfig Config, SiteConfig Site, string Token, long CredentialVersion,
    int ConfigRevision, NotificationSecrets Secrets);
public sealed record AccountConnectionInput(SiteConfig Site, string Token);

public sealed partial class RouterTasksTerminal
{
    [PluginEndpoint("POST", "accounts/query")]
    public Task<PluginResult> QueryAccountAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var input = JsonData.Read<AccountConnectionInput>(context.Body);
        Validation.Site(input.Site);
        if (string.IsNullOrWhiteSpace(input.Token) || input.Token.Any(char.IsControl)) throw new FormatException("请填写有效的 API Key");
        await _host.Http.ApproveOriginAsync(Validation.BaseUri(input.Site.BaseUrl).GetLeftPart(UriPartial.Authority), context.CancellationToken);
        return context.Ok(await QueryKeyAsync(new KeyInfo(), input.Site, input.Token.Trim(), context.CancellationToken));
    });

    [PluginEndpoint("GET", "accounts/detail")]
    public Task<PluginResult> AccountDetailAsync(PluginHttpContext context) => Api(context, async () =>
        context.Ok(await ReadAccountAsync(QueryId(context), context.CancellationToken)));

    private async Task<AccountDetail> ReadAccountAsync(string id, CancellationToken token)
    {
        var document = await Store.ReadAsync(token);
        var task = TaskStore.Find(document, id);
        var site = document.Sites.Find(s => s.Id == task.Config.SiteId) ?? throw new FormatException("账号站点不存在");
        var key = await _vault.KeyDetailAsync(task.Config.KeyId, task.Config.SiteId, token);
        var secrets = await _vault.NotificationSecretsAsync(token,
            task.Config.Settings is null ? CredentialVault.ProfileId : CredentialVault.AccountProfile(id));
        if (task.Config.Settings is null) secrets = secrets with { CredentialVersion = 0 };
        return new AccountDetail(task.Config with { Settings = JsonData.Clone(task.Config.Settings ?? document.Settings) },
            site, key.Token, key.Version, task.ConfigRevision, secrets);
    }

    [PluginEndpoint("POST", "accounts/preview")]
    public Task<PluginResult> PreviewAccountAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var input = JsonData.Read<SaveAccountInput>(context.Body);
        var run = await AccountSnapshotAsync(input, context.CancellationToken);
        var prompt = await _sources.GetAsync(run, context.CancellationToken);
        var request = TemplateRenderer.Render(run, prompt, _clock.Now);
        return context.Ok(new { method = "POST", url = request.Url.AbsoluteUri, headers = request.Headers, body = request.Body.ToJsonString() });
    });

    private async Task<RunSnapshot> AccountSnapshotAsync(SaveAccountInput input, CancellationToken token)
    {
        if (input.Config is null) throw new FormatException("账号配置不能为空");
        Validation.Site(input.Site);
        var config = input.Config with { SiteId = input.Site.Id,
            KeyId = string.IsNullOrEmpty(input.Config.KeyId) ? "new-account" : input.Config.KeyId,
            Settings = input.Config.Settings ?? new PluginSettings() };
        Validation.Config(config);
        var document = await Store.ReadAsync(token);
        var template = config.RequestOverride ?? document.Templates.Find(t => t.Id == config.TemplateId)
            ?? throw new FormatException("请求模板不存在");
        Validation.Template(template);
        var secret = input.Token?.Trim();
        if (string.IsNullOrEmpty(secret) || secret.Any(char.IsControl)) throw new FormatException("API Key 不能为空或包含控制字符");
        return new RunSnapshot(config, input.Site, template, secret, config.Settings!, "");
    }

    [PluginEndpoint("POST", "accounts/save")]
    public Task<PluginResult> SaveAccountAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var token = context.CancellationToken;
        var input = JsonData.Read<SaveAccountInput>(context.Body);
        var run = await AccountSnapshotAsync(input, token);
        var document = await Store.ReadAsync(token);
        var old = document.Tasks.Find(t => t.Config.Id == run.Config.Id);
        if (old is null && input.ExpectedRevision is not null) throw new ConflictException("账号已被删除，请重新读取");
        if (old is not null && input.ExpectedRevision != old.ConfigRevision) throw new ConflictException("账号配置已更新，请重新读取");
        if (Engine.IsRunning(run.Config.Id)) throw new ConflictException("请先暂停账号任务再修改配置");
        var oldSite = old is null ? null : document.Sites.Find(s => s.Id == old.Config.SiteId);
        if (oldSite is not null && input.Site.Revision != oldSite.Revision) throw new ConflictException("账号站点配置已更新，请重新读取");
        string? previousToken = null;
        if (old is not null)
        {
            var current = await _vault.KeyDetailAsync(old.Config.KeyId, old.Config.SiteId, token);
            if (input.CredentialVersion != current.Version) throw new ConflictException("API Key 已更新，请重新读取");
            previousToken = current.Token;
        }
        var settings = run.Settings;
        if (document.Notifications.Count(n => n.TaskId == run.Config.Id && n.State is not ("sent" or "dead")) > settings.MaxNotificationRecords)
            throw new FormatException("通知记录上限不能小于此账号的待投递记录数");
        var secrets = input.Secrets ?? new NotificationSecrets(CredentialVersion: 0);
        if (new[] { secrets.TelegramToken, secrets.TelegramChatId, secrets.BarkKey }.Any(v => v?.Any(char.IsControl) == true))
            throw new FormatException("通知凭据含控制字符");
        if (settings.TelegramEnabled && (string.IsNullOrWhiteSpace(secrets.TelegramToken) || string.IsNullOrWhiteSpace(secrets.TelegramChatId)))
            throw new FormatException("请填写此账号的 Telegram Bot Token 和 Chat ID");
        if (settings.BarkEnabled && string.IsNullOrWhiteSpace(secrets.BarkKey)) throw new FormatException("请填写此账号的 Bark Key");
        var profile = CredentialVault.AccountProfile(run.Config.Id);
        var currentSecrets = await _vault.NotificationSecretsAsync(token, profile);
        if ((secrets.CredentialVersion ?? 0) != currentSecrets.CredentialVersion) throw new ConflictException("账号通知配置已更新，请重新读取");
        await _host.Http.ApproveOriginAsync(Validation.BaseUri(input.Site.BaseUrl).GetLeftPart(UriPartial.Authority), token);

        // 每个账号独立保存连接信息；共享的旧站点或 Key 在编辑时分离。
        var shared = old is not null && (document.Tasks.Any(t => t.Config.Id != old.Config.Id &&
            (t.Config.SiteId == old.Config.SiteId || t.Config.KeyId == old.Config.KeyId)) ||
            document.Keys.Any(k => k.Id != old.Config.KeyId && k.SiteId == old.Config.SiteId));
        var site = input.Site with { Id = oldSite is null || shared ? Guid.NewGuid().ToString("N") : oldSite.Id,
            Revision = oldSite is null || shared ? 1 : oldSite.Revision + 1, Name = run.Config.Name };
        var savedKey = await _vault.SaveKeyAsync(new SaveKeyInput(old is null || shared ? null : old.Config.KeyId,
            site.Id, run.Config.Name, run.ApiKey, input.CredentialVersion), token);
        var config = run.Config with { SiteId = site.Id, KeyId = savedKey.Id };
        string? retiredKey = null;
        try
        {
            await _vault.SaveNotificationsAsync(secrets with { ClearTelegram = true, ClearBark = true }, token, profile);
            await Store.UpdateAsync(state =>
            {
                state.Sites.RemoveAll(s => s.Id == site.Id); state.Sites.Add(site);
                var previousKey = state.Keys.Find(k => k.Id == savedKey.Id);
                if (previousKey is not null && oldSite?.BaseUrl == site.BaseUrl && previousToken == run.ApiKey)
                    savedKey = previousKey with { Alias = savedKey.Alias, CredentialVersion = savedKey.CredentialVersion };
                state.Keys.RemoveAll(k => k.Id == savedKey.Id); state.Keys.Add(savedKey);
                var task = state.Tasks.Find(t => t.Config.Id == config.Id);
                if (task is null)
                    state.Tasks.Add(new TaskRecord { Config = config, ConfigRevision = 1,
                        Runtime = new TaskRuntime { Phase = config.Mode == "keepalive" ? "keepalive" : "queue" } });
                else
                {
                    if (task.Config.Mode != config.Mode) { task.Runtime.Phase = config.Mode == "keepalive" ? "keepalive" : "queue"; task.Runtime.Lanes.Clear(); }
                    task.Config = config; task.ConfigRevision++;
                }
                foreach (var notice in state.Notifications.Where(n => n.TaskId == config.Id && n.State is "queued" or "retrying" or "paused"))
                    notice.ProfileId = profile;
                if (old is not null && old.Config.KeyId != savedKey.Id && !state.Tasks.Any(t => t.Config.KeyId == old.Config.KeyId))
                { retiredKey = old.Config.KeyId; state.Keys.RemoveAll(k => k.Id == retiredKey); }
                if (oldSite is not null && oldSite.Id != site.Id && !state.Tasks.Any(t => t.Config.SiteId == oldSite.Id) && !state.Keys.Any(k => k.SiteId == oldSite.Id))
                    state.Sites.RemoveAll(s => s.Id == oldSite.Id);
                return true;
            }, token);
        }
        catch
        {
            if (old is null || shared) await _vault.DeleteKeyAsync(savedKey.Id, CancellationToken.None);
            if (currentSecrets.CredentialVersion == 0) await _vault.DeleteNotificationProfileAsync(config.Id, CancellationToken.None);
            throw;
        }
        if (retiredKey is not null) await _vault.DeleteKeyAsync(retiredKey, token);
        return context.Ok(await ReadAccountAsync(config.Id, token));
    }, true);

    [PluginEndpoint("POST", "accounts/delete")]
    public Task<PluginResult> DeleteAccountAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var id = JsonData.Read<IdInput>(context.Body).Id; Validation.Id(id);
        if (Engine.IsRunning(id)) throw new ConflictException("请先暂停账号任务");
        var task = TaskStore.Find(await Store.ReadAsync(context.CancellationToken), id);
        var removeKey = await Store.UpdateAsync(document =>
        {
            if (document.Notifications.Any(n => n.TaskId == id && n.State == "sending")) throw new ConflictException("请等待此账号的通知投递结束");
            document.Tasks.RemoveAll(t => t.Config.Id == id);
            document.Notifications.RemoveAll(n => n.TaskId == id); document.Stats.RemoveAll(s => s.TaskId == id);
            var unused = !document.Tasks.Any(t => t.Config.KeyId == task.Config.KeyId);
            if (unused) document.Keys.RemoveAll(k => k.Id == task.Config.KeyId);
            if (!document.Tasks.Any(t => t.Config.SiteId == task.Config.SiteId) && !document.Keys.Any(k => k.SiteId == task.Config.SiteId))
                document.Sites.RemoveAll(s => s.Id == task.Config.SiteId);
            return unused;
        }, context.CancellationToken);
        if (removeKey) await _vault.DeleteKeyAsync(task.Config.KeyId, context.CancellationToken);
        await _vault.DeleteNotificationProfileAsync(id, context.CancellationToken);
        return context.Ok(new { success = true });
    }, true);

    [PluginEndpoint("GET", "accounts/export")]
    public Task<PluginResult> ExportAccountAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var account = await ReadAccountAsync(QueryId(context), context.CancellationToken);
        var config = account.Config;
        config.RequestOverride ??= (await Store.ReadAsync(context.CancellationToken)).Templates.Find(t => t.Id == config.TemplateId);
        return context.Ok(new { schemaVersion = 1, account = new SaveAccountInput(config, account.Site, account.Token,
            Secrets: account.Secrets with { CredentialVersion = 0 }) });
    });
}
