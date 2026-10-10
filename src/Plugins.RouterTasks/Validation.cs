using System.Text.RegularExpressions;

namespace Plugins.RouterTasks;

internal sealed class ConflictException(string message) : Exception(message);

internal static partial class Validation
{
    [GeneratedRegex("^[A-Za-z0-9_-]{1,100}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
    public static void Id(string id) { if (id is null || !Identifier().IsMatch(id)) throw new FormatException("ID 无效"); }
    public static void Positive(double value, string field, bool zero = false)
    {
        if (!double.IsFinite(value) || (zero ? value < 0 : value <= 0) || value > 2_000_000)
            throw new FormatException($"{field} 必须是{(zero ? "非负" : "正")}有限数，且不超过 2000000 秒");
    }
    public static void Codes(int[] codes)
    {
        if (codes is null || codes.Any(code => code is < 100 or > 599)) throw new FormatException("HTTP 状态码必须在 100–599 之间");
    }
    public static Uri BaseUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new FormatException("Base URL 必须是无凭据、查询参数或片段的 HTTP(S) 地址");
        return uri;
    }
    public static Uri Endpoint(string baseUrl, string path)
    {
        var root = BaseUri(baseUrl);
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.Contains('#') || path.Contains("..", StringComparison.Ordinal)
            || path.StartsWith("//", StringComparison.Ordinal) || Uri.TryCreate(path, UriKind.Absolute, out _))
            throw new FormatException("接口路径必须是同站点相对路径，不能跳转到另一地址");
        string target;
        if (path.StartsWith('/')) target = root.GetLeftPart(UriPartial.Authority) + path;
        else
        {
            var prefix = root.AbsoluteUri.TrimEnd('/');
            target = prefix + (prefix.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "/" : "/v1/") + path;
        }
        var result = new Uri(target);
        if (result.GetLeftPart(UriPartial.Authority) != root.GetLeftPart(UriPartial.Authority)) throw new FormatException("接口不能改变 origin");
        return result;
    }
    public static void Site(SiteConfig site)
    {
        if (site is null) throw new FormatException("站点不能为空");
        Id(site.Id); BaseUri(site.BaseUrl); Codes(site.QueueHttpStatuses);
        if (site.SubscriptionIds is null || site.SubscriptionIds.Any(string.IsNullOrWhiteSpace)) throw new FormatException("代理订阅 ID 列表无效");
        if (string.IsNullOrWhiteSpace(site.Name) || site.Name.Length > 200 || site.Route is not ("direct" or "pool"))
            throw new FormatException("站点名称或网络路线无效");
    }
    public static void Template(RequestTemplate template)
    {
        if (template is null) throw new FormatException("模板不能为空");
        Id(template.Id);
        if (string.IsNullOrWhiteSpace(template.Name) || template.Name.Length > 200) throw new FormatException("模板名称无效");
        if (template.Protocol is not ("responses" or "messages") || template.Body is null || template.Headers is null)
            throw new FormatException("模板协议或 JSON 无效");
        Endpoint("https://example.invalid", template.Path);
        if (template.Body["stream"] is not System.Text.Json.Nodes.JsonValue stream || !stream.TryGetValue<bool>(out var enabled) || !enabled)
            throw new FormatException("Body.stream 必须为 true（SSE）");
        var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in template.Headers)
        {
            if (!headers.Add(key) || string.IsNullOrWhiteSpace(key) || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                || value is null || value.Any(char.IsControl)) throw new FormatException("Header 名称重复、无效或包含控制字符");
            if (new[] { "host", "content-length", "connection", "transfer-encoding", "upgrade", "proxy-authorization", "accept-encoding" }.Contains(key.ToLowerInvariant()))
                throw new FormatException($"{key} 由传输层维护");
            if (new[] { "authorization", "x-api-key", "api-key", "cookie" }.Contains(key.ToLowerInvariant())
                && value.Length > 0 && !value.Contains("{{TOKEN}}", StringComparison.Ordinal))
                throw new FormatException($"{key} 必须使用 {{{{TOKEN}}}}，不能将真实凭据写入模板");
        }
    }
    public static void Config(TaskConfig config)
    {
        if (config is null) throw new FormatException("任务配置不能为空");
        Id(config.Id); Id(config.SiteId); Id(config.KeyId); Id(config.TemplateId);
        if (string.IsNullOrWhiteSpace(config.Name) || config.Name.Length > 200 || string.IsNullOrWhiteSpace(config.Model) || config.Prompt is null)
            throw new FormatException("任务名称和模型不能为空");
        if (config.Mode is not ("queue" or "keepalive" or "queue_then_keepalive")) throw new FormatException("未知任务模式");
        if (config.QueueHttpStatuses is { } codes) Codes(codes);
        if (config.Settings is { } settings) Settings(settings);
        if (config.RequestOverride is { } template) Template(template);
        var q = config.Queue ?? throw new FormatException("缺少排队参数");
        var k = config.Keepalive ?? throw new FormatException("缺少保活参数");
        if (q.Concurrency < 1 || k.Concurrency < 1 || q.MaxAttemptsPerLane is <= 0 || k.MaxAttemptsPerLane is <= 0) throw new FormatException("并发、次数必须为正整数；不限次数使用 null");
        Positive(q.IntervalSeconds, "排队间隔"); Positive(q.FirstEventTimeoutSeconds, "首事件超时");
        Positive(q.ResponseTimeoutSeconds, "响应总超时"); Positive(q.IdleTimeoutSeconds, "空闲超时");
        Positive(k.HeartbeatSeconds, "心跳间隔"); Positive(k.FailureRetrySeconds, "失败重试间隔");
        Positive(k.FirstEventTimeoutSeconds, "保活超时"); Positive(k.IdleEvictionSeconds, "失联窗口"); Positive(k.DropConfirmSeconds, "掉线确认窗口", true);
        if (q.IntervalMaxSeconds is { } qm && (!double.IsFinite(qm) || qm < q.IntervalSeconds || qm > 2_000_000)
            || k.HeartbeatMaxSeconds is { } km && (!double.IsFinite(km) || km < k.HeartbeatSeconds || km > 2_000_000))
            throw new FormatException("随机间隔上限必须不小于下限");
        if (q.MaxResponseBytes < 1 || k.MaxResponseBytes < 1) throw new FormatException("响应大小限制必须为正整数");
        if (!double.IsFinite(config.SourcePromptCacheMinutes) || config.SourcePromptCacheMinutes is < 0 or > 2_000_000)
            throw new FormatException("Linux 源码缓存时长必须是 0–2000000 分钟的有限数");
        if (config.NotificationEvents is null || config.NotificationEvents.Any(e => !new[] { "entered", "dropped", "recovered", "stopped", "paused", "exhausted" }.Contains(e)))
            throw new FormatException("未知通知事件");
    }
    public static void Settings(PluginSettings settings)
    {
        if (settings is null) throw new FormatException("设置不能为空");
        if (settings.MaxEventsPerTask is < 1 or > 10000 || settings.SummaryBytes is < 1 or > 2_097_152
            || settings.HistoryDays is < 1 or > 3650 || settings.MaxNotificationRecords is < 1 or > 100000
            || settings.NotificationMaxAttempts is < 1 or > 1000) throw new FormatException("保留限制或通知次数无效");
        BaseUri(settings.BarkServer); Positive(settings.NotificationRetrySeconds, "通知重试间隔");
        Positive(settings.NotificationMaxRetrySeconds, "通知最大退避");
        if (settings.NotificationMaxRetrySeconds < settings.NotificationRetrySeconds) throw new FormatException("通知最大退避不能小于初始间隔");
    }
}
