using Router.Contracts.Domain;
using Router.Contracts.Host;

namespace Plugins.RouterTasks;

public sealed record SaveKeyInput(string? Id, string SiteId, string Alias, string? Token, long? CredentialVersion = null);
public sealed record NotificationSecrets(string? TelegramToken = null, string? TelegramChatId = null, string? BarkKey = null,
    long? CredentialVersion = null, bool ClearTelegram = false, bool ClearBark = false);

/// <summary>在宿主账号服务中保存 API Key 和账号通知凭据。</summary>
internal sealed class CredentialVault(IPluginAccounts accounts)
{
    internal const string ProfileId = "routertasks-notifications";
    // 宿主旧版 CredentialCodec 会把名为 token 的字段推断成 Bearer 凭据。
    // 使用插件专用字段，保留 Custom 类型、账号类别与站点绑定。
    internal const string ApiTokenField = "routerTasksApiToken";
    private static string? ReadToken(IReadOnlyDictionary<string, string?> fields)
        => fields.GetValueOrDefault(ApiTokenField) ?? fields.GetValueOrDefault("token");
    internal static string AccountProfile(string taskId) => "routertasks-notifications-" +
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(taskId)))[..32].ToLowerInvariant();

    public async Task<(string Token, long Version)> KeyDetailAsync(string id, string siteId, CancellationToken token)
    {
        var account = await accounts.GetAsync(id, token) ?? throw new KeyNotFoundException("账号不存在");
        if (account.Credential is not CustomCredential credential || credential.Fields.GetValueOrDefault("type") != "key"
            || credential.Fields.GetValueOrDefault("siteId") != siteId) throw new FormatException("账号站点配置无效");
        return (ReadToken(credential.Fields) ?? "", account.CredentialVersion);
    }
    public async Task<string> KeyAsync(string id, string siteId, CancellationToken token)
    {
        var account = await accounts.GetAsync(id, token) ?? throw new FormatException("Key 已删除");
        if (account.Credential is not CustomCredential credential || credential.Fields.GetValueOrDefault("type") != "key"
            || credential.Fields.GetValueOrDefault("siteId") != siteId || account.Status.State is ResourceState.Disabled or ResourceState.Invalid or ResourceState.Removed)
            throw new FormatException("Key 类型、站点或状态无效");
        return ReadToken(credential.Fields) is { Length: > 0 } secret ? secret : throw new FormatException("Key 为空");
    }
    public async Task<KeyInfo> SaveKeyAsync(SaveKeyInput input, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(input.Alias) || input.Alias.Length > 200) throw new FormatException("Key 别名不能为空或超过 200 字");
        Validation.Id(input.SiteId);
        if (input.Id is { Length: > 0 }) Validation.Id(input.Id);
        Account? existing = input.Id is { Length: > 0 } id ? await accounts.GetAsync(id, token) : null;
        if (input.Id is { Length: > 0 } && existing is null) throw new KeyNotFoundException("Key 不存在");
        var oldFields = existing?.Credential is CustomCredential old ? old.Fields : new Dictionary<string, string?>();
        if (existing is not null && oldFields.GetValueOrDefault("type") != "key") throw new FormatException("不是 Key 账号");
        var value = string.IsNullOrEmpty(input.Token) ? ReadToken(oldFields) : input.Token.Trim();
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) throw new FormatException("Key 无效");
        var credential = new CustomCredential(new Dictionary<string, string?> { ["type"] = "key", ["siteId"] = input.SiteId, [ApiTokenField] = value });
        Account saved;
        if (existing is null)
            saved = await accounts.SaveAsync(new Account { PluginKey = "router-tasks", Platform = "router-tasks", Label = input.Alias, Credential = credential }, token);
        else
        {
            if (input.CredentialVersion != existing.CredentialVersion) throw new ConflictException("Key 已被修改，请重新读取后再保存");
            saved = await accounts.CompareExchangeCredentialAsync(existing.Id, existing.CredentialVersion, credential, cancellationToken: token)
                ?? throw new ConflictException("Key 修订冲突");
            await accounts.PatchAsync(new Account { Id = existing.Id, PluginKey = "router-tasks", Platform = "router-tasks", Label = input.Alias }, ["label"], token);
        }
        return new KeyInfo { Id = saved.Id, SiteId = input.SiteId, Alias = input.Alias, CredentialVersion = saved.CredentialVersion };
    }
    public Task DeleteKeyAsync(string id, CancellationToken token) => accounts.DeleteAsync(id, token);

    // 账号事务与 JSON 文件不能原子提交。显式对账可恢复写账号成功、写元数据失败的孤立 Key。
    public async Task<IReadOnlyList<KeyInfo>> ListKeysAsync(CancellationToken token)
        => (await accounts.ListAsync("router-tasks", token))
            .Where(a => a.Credential is CustomCredential c && c.Fields.GetValueOrDefault("type") == "key")
            .Select(a =>
            {
                var c = (CustomCredential)a.Credential;
                return new KeyInfo { Id = a.Id, SiteId = c.Fields.GetValueOrDefault("siteId") ?? "", Alias = a.Label ?? a.Id,
                    CredentialVersion = a.CredentialVersion };
            }).ToArray();

    public async Task<Dictionary<string, string?>> NotificationAsync(CancellationToken token, string? profileId = null)
    {
        var account = await accounts.GetAsync(profileId ?? ProfileId, token);
        return account?.Credential is CustomCredential custom && custom.Fields.GetValueOrDefault("type") == "notifications"
            ? new(custom.Fields, StringComparer.Ordinal) : [];
    }
    public async Task<NotificationSecrets> NotificationSecretsAsync(CancellationToken token, string? profileId = null)
    {
        var account = await accounts.GetAsync(profileId ?? ProfileId, token);
        IReadOnlyDictionary<string, string?> fields = account?.Credential is CustomCredential custom && custom.Fields.GetValueOrDefault("type") == "notifications" ? custom.Fields : new Dictionary<string, string?>();
        return new NotificationSecrets(fields.GetValueOrDefault("telegramToken"), fields.GetValueOrDefault("telegramChatId"),
            fields.GetValueOrDefault("barkKey"), account?.CredentialVersion ?? 0);
    }
    public async Task<object> NotificationStatusAsync(CancellationToken token)
    {
        var secrets = await NotificationSecretsAsync(token);
        return new { secrets.CredentialVersion, secrets.TelegramToken, secrets.TelegramChatId, secrets.BarkKey,
            telegramConfigured = !string.IsNullOrEmpty(secrets.TelegramToken) && !string.IsNullOrEmpty(secrets.TelegramChatId),
            barkConfigured = !string.IsNullOrEmpty(secrets.BarkKey) };
    }
    public async Task SaveNotificationsAsync(NotificationSecrets input, CancellationToken token, string? profileId = null)
    {
        var id = profileId ?? ProfileId;
        var old = await accounts.GetAsync(id, token);
        var fields = await NotificationAsync(token, id);
        fields["type"] = "notifications";
        if (input.ClearTelegram) { fields.Remove("telegramToken"); fields.Remove("telegramChatId"); }
        if (input.ClearBark) fields.Remove("barkKey");
        if (!string.IsNullOrWhiteSpace(input.TelegramToken)) fields["telegramToken"] = input.TelegramToken.Trim();
        if (!string.IsNullOrWhiteSpace(input.TelegramChatId)) fields["telegramChatId"] = input.TelegramChatId.Trim();
        if (!string.IsNullOrWhiteSpace(input.BarkKey)) fields["barkKey"] = input.BarkKey.Trim();
        if (fields.Values.Any(v => v?.Any(char.IsControl) == true)) throw new FormatException("通知凭据含控制字符");
        var credential = new CustomCredential(fields);
        if (old is null)
            await accounts.SaveAsync(new Account { Id = id, PluginKey = "router-tasks", Platform = "router-tasks", Label = "RouterTasks 通知凭据", Credential = credential }, token);
        else
        {
            if (input.CredentialVersion != old.CredentialVersion) throw new ConflictException("通知凭据已修改，请刷新设置");
            if (await accounts.CompareExchangeCredentialAsync(id, old.CredentialVersion, credential, cancellationToken: token) is null)
                throw new ConflictException("通知凭据修订冲突");
        }
    }
    public Task DeleteNotificationProfileAsync(string taskId, CancellationToken token) => accounts.DeleteAsync(AccountProfile(taskId), token);
}
