using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace Plugins.RouterTasks;

public sealed record TokenUsageLog(string Id, DateTimeOffset? At, int? Type, string ModelName, string TokenName,
    decimal? Quota, decimal? PromptTokens, decimal? CompletionTokens, decimal? UseTime, bool? IsStream,
    string Group, string Content, string Other);
public sealed record TokenUsageLogs(TokenUsageLog[] Items, DateTimeOffset QueriedAt, string LastRoute, ProxyPoolNode? LastProxy);

public sealed partial class RouterTasksTerminal
{
    private const int UsageLogMaxBytes = 2 * 1024 * 1024;

    // 管理桥只传账号 ID；Key 从宿主凭据服务读取，不进入浏览器 URL 或插件状态。
    [PluginEndpoint("POST", "accounts/usage-logs")]
    public Task<PluginResult> QueryUsageLogsAsync(PluginHttpContext context) => Api(context, async () =>
    {
        var id = JsonData.Read<IdInput>(context.Body).Id;
        Validation.Id(id);
        var token = context.CancellationToken;
        var document = await Store.ReadAsync(token);
        var account = TaskStore.Find(document, id);
        var site = document.Sites.Find(s => s.Id == account.Config.SiteId) ?? throw new FormatException("账号站点不存在");
        Validation.Site(site);
        var secret = (await _vault.KeyAsync(account.Config.KeyId, site.Id, token)).Trim();
        if (string.IsNullOrEmpty(secret) || secret.Any(char.IsControl)) throw new FormatException("请填写有效的 API Key");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _httpShutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await _host.Http.ApproveOriginAsync(Validation.BaseUri(site.BaseUrl).GetLeftPart(UriPartial.Authority), timeout.Token);
            ProxyPoolNode? proxy = null;
            using var client = await _network.CreateClientAsync(site, timeout.Token, selected => proxy = selected);
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageLogEndpoint(site.BaseUrl, secret));
            // 旧站点读取 key 查询参数；新版本 New API 使用只读 Bearer 认证。
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + secret);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                var message = status switch
                {
                    404 or 405 or 501 => "此站点不支持 Token 使用日志接口",
                    401 or 403 => "使用日志查询鉴权失败，请检查 API Key 和站点权限",
                    429 => "站点限制使用日志查询频率，请稍后重试",
                    >= 300 and < 400 => "使用日志接口返回重定向，请检查站点地址",
                    _ => "站点使用日志查询失败"
                };
                return context.Json(502, new { error = $"{message}（HTTP {status}）" });
            }
            var body = await HttpProbeClient.ReadLimitedAsync(response.Content, UsageLogMaxBytes + 1, timeout.Token);
            if (System.Text.Encoding.UTF8.GetByteCount(body) > UsageLogMaxBytes)
                return context.Json(502, new { error = "使用日志响应超过 2 MiB，请在站点缩小返回范围后重试" });
            var items = ParseUsageLogs(body, secret);
            timeout.Token.ThrowIfCancellationRequested();
            return context.Ok(new TokenUsageLogs(items, _clock.Now, site.Route, proxy));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || _httpShutdown.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { return context.Json(504, new { error = "使用日志查询超时，请稍后重试" }); }
        catch (HttpRequestException)
        { return context.Json(502, new { error = "无法连接站点使用日志接口，请检查网络或代理" }); }
        catch (IOException)
        { return context.Json(502, new { error = "读取站点使用日志失败，请稍后重试" }); }
        catch (ProxySelectionException) { throw; }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException)
        { return context.Json(502, new { error = UsageLogText(error.Message, secret) }); }
    });

    internal static Uri UsageLogEndpoint(string baseUrl, string secret)
    {
        var builder = new UriBuilder(Validation.BaseUri(baseUrl));
        var prefix = builder.Path.TrimEnd('/');
        if (prefix.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) prefix = prefix[..^3];
        builder.Path = prefix + "/api/log/token";
        builder.Query = "key=" + Uri.EscapeDataString(secret);
        return builder.Uri;
    }

    internal static TokenUsageLog[] ParseUsageLogs(string body, string secret)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { throw new FormatException("站点未返回有效的使用日志 JSON"); }
        if (root is not JsonObject envelope || envelope["success"] is not JsonValue success || !success.TryGetValue<bool>(out var ok))
            throw new FormatException("站点使用日志响应格式不兼容：缺少 success");
        if (!ok)
            throw new FormatException("站点使用日志查询失败：" + UsageLogText(envelope["message"]?.ToString() ?? "未知错误", secret));
        // 常见接口直接返回 data 数组；部分兼容站点用 data.items 包装。
        var data = envelope["data"];
        var rows = data as JsonArray ?? (data as JsonObject)?["items"] as JsonArray;
        if (rows is null) throw new FormatException("站点使用日志响应格式不兼容：缺少日志数组");
        return rows.Select(row =>
        {
            if (row is not JsonObject item) throw new FormatException("站点使用日志响应包含无效记录");
            string Text(string field, int bytes = 2048) => UsageLogText(item[field]?.ToString() ?? "", secret, bytes);
            DateTimeOffset? at = null;
            if (UsageLogNumber(item["created_at"]) is { } seconds && seconds == decimal.Truncate(seconds)
                && seconds is >= -62135596800m and <= 253402300799m)
                at = DateTimeOffset.FromUnixTimeSeconds((long)seconds);
            var type = UsageLogNumber(item["type"]);
            return new TokenUsageLog(Text("id"), at,
                type is >= int.MinValue and <= int.MaxValue && type == decimal.Truncate(type.Value) ? (int)type.Value : null,
                Text("model_name"), Text("token_name"), UsageLogNumber(item["quota"]),
                UsageLogNumber(item["prompt_tokens"]), UsageLogNumber(item["completion_tokens"]),
                UsageLogNumber(item["use_time"]),
                item["is_stream"] is JsonValue stream && stream.TryGetValue<bool>(out var enabled) ? enabled : null,
                Text("group"), Text("content", 8192), Text("other", 16384));
        }).OrderByDescending(item => item.At).ToArray();
    }

    private static decimal? UsageLogNumber(JsonNode? value)
        => value is JsonValue && decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;

    private static string UsageLogText(string value, string secret, int bytes = 2048)
    {
        if (!string.IsNullOrEmpty(secret))
        {
            var variants = new[] { secret, Uri.EscapeDataString(secret), secret.StartsWith("sk-", StringComparison.Ordinal) ? secret[3..] : secret };
            foreach (var variant in variants.Where(value => value.Length > 0).Distinct())
                value = value.Replace(variant, "[已隐藏 API Key]", StringComparison.Ordinal);
        }
        return TextOutput.LimitUtf8(value, bytes);
    }
}
