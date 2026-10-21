using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Plugins.RouterTasks;

internal static class JsonData
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        // 每次插件加载独立持有类型元数据，避免不同版本共享序列化缓存。
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };
    public static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
    public static T Read<T>(object? body) => body switch
    {
        JsonElement element => element.Deserialize<T>(Options) ?? throw new FormatException("请求正文不能为空"),
        T value => Clone(value),
        _ => throw new FormatException("请求正文必须是 JSON")
    };
}

public sealed record SiteConfig
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public int Revision { get; set; }
    public string Name { get; set; } = "AnyRouter";
    public string BaseUrl { get; set; } = "https://anyrouter.top";
    public string Route { get; set; } = "direct";
    public string[] SubscriptionIds { get; set; } = [];
    public string? ProxyId { get; set; }
    public int[] QueueHttpStatuses { get; set; } = [500];
}

public sealed record RequestTemplate
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "自定义模板";
    public string Protocol { get; set; } = "responses";
    public string Path { get; set; } = "responses";
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // JSON 字符串跨浏览器桥传输，避免 JS Number 把未知 Int64/decimal 四舍五入。
    // 兼容导入旧的对象形式；运行时只有这个 JsonObject 是唯一正文数据源。
    [JsonConverter(typeof(RawBodyConverter))]
    public JsonObject Body { get; set; } = [];
}

public sealed record QueueSettings
{
    public int Concurrency { get; set; } = 1;
    public int? MaxAttemptsPerLane { get; set; } = 300;
    public double IntervalSeconds { get; set; } = 10;
    public double? IntervalMaxSeconds { get; set; }
    public double FirstEventTimeoutSeconds { get; set; } = 60;
    public bool RetryNetworkErrors { get; set; } = true;
    public bool RetryTimeouts { get; set; } = true;
    public bool ReadFullResponse { get; set; }
    public double ResponseTimeoutSeconds { get; set; } = 600;
    public double IdleTimeoutSeconds { get; set; } = 60;
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;
}

public sealed record KeepaliveSettings
{
    public int Concurrency { get; set; } = 1;
    public int? MaxAttemptsPerLane { get; set; }
    public double HeartbeatSeconds { get; set; } = 120;
    public double? HeartbeatMaxSeconds { get; set; }
    public double FailureRetrySeconds { get; set; } = 15;
    public double FirstEventTimeoutSeconds { get; set; } = 60;
    public double IdleEvictionSeconds { get; set; } = 300;
    public double DropConfirmSeconds { get; set; } = 120;
    public bool RetryNetworkErrors { get; set; } = true;
    public bool RetryTimeouts { get; set; } = true;
    public int MaxResponseBytes { get; set; } = 2 * 1024 * 1024;
}

public sealed record TaskConfig
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "新任务";
    public string Mode { get; set; } = "queue_then_keepalive";
    public bool AutoRequeue { get; set; } = true;
    public string SiteId { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string Model { get; set; } = "";
    public bool Claude1MContext { get; set; } = true;
    public string Prompt { get; set; } = "Reply only OK. Do not call tools.";
    public string TemplateId { get; set; } = "responses";
    public RequestTemplate? RequestOverride { get; set; }
    public int[]? QueueHttpStatuses { get; set; }
    public QueueSettings Queue { get; set; } = new();
    public KeepaliveSettings Keepalive { get; set; } = new();
    public bool UseSourcePrompt { get; set; }
    // Linux 源码提示词缓存时长；旧配置缺少该字段时保持 6 小时默认值。
    public double SourcePromptCacheMinutes { get; set; } = 360;
    public string[] NotificationEvents { get; set; } = ["entered", "dropped", "stopped", "paused", "exhausted"];
    public PluginSettings? Settings { get; set; }
}

public sealed record LaneState
{
    public int Index { get; set; }
    public long Attempts { get; set; }
    public string Status { get; set; } = "waiting";
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }
    public string? LastRoute { get; set; }
    public ProxyPoolNode? LastProxy { get; set; }
}

public sealed record TaskRuntime
{
    public string RunId { get; set; } = Guid.NewGuid().ToString("N");
    public string? Generation { get; set; }
    public string Status { get; set; } = "paused";
    public string Phase { get; set; } = "queue";
    public int QueueRound { get; set; }
    public long TotalAttempts { get; set; }
    public long Heartbeats { get; set; }
    public long Recoveries { get; set; }
    public bool Entered { get; set; }
    public bool SessionConfirmed { get; set; }
    public DateTimeOffset? MonitoringSince { get; set; }
    public long OutageSequence { get; set; }
    // 本次故障的确认状态，与通知投递状态独立。
    public bool OutageNotified { get; set; }
    // 失联观察开始时间。
    public DateTimeOffset? DropPendingSince { get; set; }
    public string? PauseReason { get; set; }
    public string? LastError { get; set; }
    public string? LastRoute { get; set; }
    public ProxyPoolNode? LastProxy { get; set; }
    public int? LastHttpStatus { get; set; }
    public string? MatchedRule { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? FailureSince { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public string ResponseState { get; set; } = "none";
    public string ResponseSummary { get; set; } = "";
    public List<LaneState> Lanes { get; set; } = [];
    public Dictionary<int, long> QueueLaneAttempts { get; set; } = [];
    public Dictionary<int, long> KeepaliveLaneAttempts { get; set; } = [];
}

public sealed record TaskEvent(DateTimeOffset At, string Kind, string Message, int? Lane = null);
public sealed record TaskRecord
{
    public TaskConfig Config { get; set; } = new();
    public int ConfigRevision { get; set; }
    public TaskRuntime Runtime { get; set; } = new();
    public List<TaskEvent> Events { get; set; } = [];
    public SavedRunSnapshot? Snapshot { get; set; }
}

public sealed record SavedRunSnapshot(TaskConfig Config, SiteConfig Site, RequestTemplate Template, PluginSettings Settings, DateTimeOffset At);

public sealed record KeyInfo
{
    public string Id { get; set; } = "";
    public string SiteId { get; set; } = "";
    public string Alias { get; set; } = "";
    public long CredentialVersion { get; set; }
    public string AuthStatus { get; set; } = "unchecked";
    public string[] Models { get; set; } = [];
    public string? Error { get; set; }
    public decimal? UsedUsd { get; set; }
    public decimal? LimitUsd { get; set; }
    public bool Unlimited { get; set; }
    public DateTimeOffset? UsageAt { get; set; }
    public string? UsageError { get; set; }
    public string? LastRoute { get; set; }
    public ProxyPoolNode? LastProxy { get; set; }
}

public sealed record PluginSettings
{
    public int MaxEventsPerTask { get; set; } = 200;
    public int SummaryBytes { get; set; } = 8192;
    public int HistoryDays { get; set; } = 30;
    public bool TelegramEnabled { get; set; }
    public bool BarkEnabled { get; set; }
    public string BarkServer { get; set; } = "https://api.day.app";
    public int NotificationMaxAttempts { get; set; } = 10;
    public double NotificationRetrySeconds { get; set; } = 5;
    public double NotificationMaxRetrySeconds { get; set; } = 3600;
    public int MaxNotificationRecords { get; set; } = 1000;
}

public sealed record NotificationItem
{
    public string Id { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string? ProfileId { get; set; }
    public string Kind { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public string State { get; set; } = "queued";
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? Error { get; set; }
}

public sealed record DailyStat
{
    public string TaskId { get; set; } = "";
    public string Day { get; set; } = "";
    public string Protocol { get; set; } = "";
    public long Entered { get; set; }
    public long Recoveries { get; set; }
    public long Heartbeats { get; set; }
}

public sealed record StoreDocument
{
    public int SchemaVersion { get; set; } = 1;
    public long Revision { get; set; }
    public PluginSettings Settings { get; set; } = new();
    public List<SiteConfig> Sites { get; set; } = [];
    public List<RequestTemplate> Templates { get; set; } = [];
    public List<KeyInfo> Keys { get; set; } = [];
    public List<TaskRecord> Tasks { get; set; } = [];
    public List<NotificationItem> Notifications { get; set; } = [];
    public List<DailyStat> Stats { get; set; } = [];
    public long NotificationOverflowCount { get; set; }
    public string? NotificationWarning { get; set; }
    public string? SourceSnippet { get; set; }
    public DateTimeOffset? SourceUpdatedAt { get; set; }
    // 旧版本成功后也设置 SourceNextTryAt；仅有失败标记时才应用退避，兼容短缓存时长。
    public DateTimeOffset? SourceLastFailureAt { get; set; }
    public DateTimeOffset? SourceNextTryAt { get; set; }
}

internal sealed record RunSnapshot(TaskConfig Config, SiteConfig Site, RequestTemplate Template, string ApiKey,
    PluginSettings Settings, string Generation);

internal interface IRuntimeClock
{
    DateTimeOffset Now { get; }
    Task Delay(TimeSpan delay, CancellationToken token);
}

internal sealed class RuntimeClock : IRuntimeClock
{
    public DateTimeOffset Now => DateTimeOffset.UtcNow;
    public Task Delay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
}

internal sealed class RawBodyConverter : JsonConverter<JsonObject>
{
    public override JsonObject Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? JsonNode.Parse(reader.GetString()!) : JsonNode.Parse(ref reader);
        return value as JsonObject ?? throw new JsonException("Body 必须是 JSON 对象或对象的 JSON 文本");
    }
    public override void Write(Utf8JsonWriter writer, JsonObject value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToJsonString());
}
