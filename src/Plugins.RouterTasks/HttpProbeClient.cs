using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Router.Contracts.Host;

namespace Plugins.RouterTasks;

internal enum ProbeKind { Accepted, Busy, Network, Timeout, Permanent, Protocol, Lost, Transient }
internal sealed record ProbeResult(ProbeKind Kind, int? Status = null, string? Error = null,
    double RetryAfterSeconds = 0, string Summary = "", bool Completed = false,
    ProxyPoolNode? Proxy = null, string? Route = null);
internal interface IProbeClient
{
    Task<ProbeResult> ProbeAsync(RunSnapshot run, bool heartbeat, string prompt,
        Func<int, Task<bool>> accepted, CancellationToken token, Func<ProxyPoolNode?, Task>? selected = null);
}

/// <summary>管理型探针，不进入模型路由 attempt；独立 HTTP 客户端不隐式重试 POST。</summary>
internal sealed partial class HttpProbeClient(NetworkClientFactory network, IRuntimeClock clock) : IProbeClient
{
    internal HttpProbeClient(IPluginHttpServices http, IRuntimeClock clock) : this(new NetworkClientFactory(http), clock) { }

    [GeneratedRegex(@"(?i)insufficient_quota|quota[ _]exceeded|insufficient[ _]credit|credit[ _]balance|billing[ _]limit|billing_hard_limit_reached|额度不足|配额不足|余额不足|invalid[ _](?:token|api[ _]key)|authentication_error|permission_error|unauthorized|令牌无效|无效的令牌|model_not_found", RegexOptions.CultureInvariant)]
    private static partial Regex FatalError();

    public static bool IsPermanent(int status, string text) => status is 401 or 402 or 403 || FatalError().IsMatch(text);
    public static double RetryAfter(HttpResponseHeaders headers, DateTimeOffset now)
    {
        if (headers.RetryAfter?.Delta is { } delta) return Math.Max(0, delta.TotalSeconds);
        if (headers.RetryAfter?.Date is { } date) return Math.Max(0, (date - now).TotalSeconds);
        return 0;
    }
    public async Task<ProbeResult> ProbeAsync(RunSnapshot run, bool heartbeat, string prompt, Func<int, Task<bool>> accepted,
        CancellationToken token, Func<ProxyPoolNode?, Task>? selected = null)
    {
        var q = run.Config.Queue; var k = run.Config.Keepalive;
        var readFull = !heartbeat && run.Config.Mode == "queue" && q.ReadFullResponse;
        var firstSeconds = heartbeat ? k.FirstEventTimeoutSeconds : q.FirstEventTimeoutSeconds;
        var totalSeconds = readFull ? q.ResponseTimeoutSeconds : firstSeconds;
        using var first = new CancellationTokenSource(TimeSpan.FromSeconds(firstSeconds));
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(totalSeconds));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, first.Token, total.Token);
        var didAccept = false; int? status = null;
        ProxyPoolNode? selectedProxy = null; string? selectedRoute = null;
        var summary = new StringBuilder(); var summaryBytes = 0;
        try
        {
            token.ThrowIfCancellationRequested();
            var rendered = TemplateRenderer.Render(run, prompt, clock.Now);
            using var request = BuildMessage(rendered);
            using var client = await network.CreateClientAsync(run.Site, operation.Token, proxy => selectedProxy = proxy);
            selectedRoute = run.Site.Route;
            if (selected is not null) await selected(selectedProxy);
            operation.Token.ThrowIfCancellationRequested();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            status = (int)response.StatusCode;
            var retry = RetryAfter(response.Headers, clock.Now);
            if (!response.IsSuccessStatusCode)
            {
                var text = await ReadLimitedAsync(response.Content, 4096, operation.Token);
                var clean = TextOutput.Summary($"HTTP {status} · {text}");
                if (IsPermanent(status.Value, text)) return Result(ProbeKind.Permanent, clean);
                var codes = run.Config.QueueHttpStatuses ?? run.Site.QueueHttpStatuses;
                var kind = codes.Contains(status.Value) ? ProbeKind.Busy
                    : status.Value is 408 or 425 or 429 or >= 500 ? ProbeKind.Transient : ProbeKind.Permanent;
                return Result(kind, clean, retry);
            }
            if ((run.Config.QueueHttpStatuses ?? run.Site.QueueHttpStatuses).Contains(status.Value))
                return Result(ProbeKind.Busy, $"HTTP {status} 命中配置的排队规则", retry);
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                var text = await ReadLimitedAsync(response.Content, 4096, operation.Token);
                return Result(IsPermanent(status.Value, text) ? ProbeKind.Permanent : ProbeKind.Protocol,
                    TextOutput.Summary($"HTTP {status} 不是 SSE：{text}"));
            }
            await using var stream = await response.Content.ReadAsStreamAsync(operation.Token);
            await using var limited = new TimedReadStream(stream, heartbeat ? k.MaxResponseBytes : q.MaxResponseBytes,
                TimeSpan.FromSeconds(readFull ? q.IdleTimeoutSeconds : firstSeconds));
            await foreach (var frame in SseReader.ReadAsync(limited, operation.Token))
            {
                operation.Token.ThrowIfCancellationRequested();
                if (frame.Data == "[DONE]")
                {
                    if (didAccept) return Done(false, "SSE 已结束，但未收到协议完成事件，未重放");
                    return Result(ProbeKind.Protocol, "SSE 在接受事件之前结束");
                }
                JsonObject payload;
                try { payload = JsonNode.Parse(frame.Data)?.AsObject() ?? throw new FormatException(); }
                catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or FormatException)
                { return didAccept ? Done(false, "SSE 含非法 JSON") : Result(ProbeKind.Protocol, "SSE 含非法 JSON"); }
                var type = frame.Event is { Length: > 0 } e && e != "message" ? e : payload["type"]?.ToString() ?? "";
                if (payload["type"] is { } payloadType && frame.Event is { Length: > 0 } namedEvent && namedEvent != "message" && payloadType.ToString() != namedEvent)
                    return didAccept ? Done(false, "SSE event 与 data.type 不一致") : Result(ProbeKind.Protocol, "SSE event 与 data.type 不一致");
                var responseBody = payload["response"] as JsonObject;
                var responseStatus = responseBody?["status"]?.ToString();
                if (payload["error"] is not null || responseBody?["error"] is not null
                    || type is "error" or "response.failed" or "response.incomplete" or "response.cancelled" or "response.canceled"
                    || responseStatus is "failed" or "incomplete" or "cancelled" or "canceled")
                {
                    var detail = TextOutput.Summary(payload.ToJsonString());
                    return didAccept ? Done(false, detail) : Result(IsPermanent(status.Value, detail) ? ProbeKind.Permanent : ProbeKind.Protocol, detail);
                }
                var success = run.Template.Protocol == "responses" ? type is "response.created" or "response.in_progress" : type == "message_start";
                if (!didAccept && success)
                {
                    didAccept = await accepted(status.Value);
                    first.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
                    operation.Token.ThrowIfCancellationRequested();
                    if (!didAccept) return Result(ProbeKind.Lost);
                    if (!readFull) return Done(false);
                }
                if (didAccept)
                {
                    var delta = type == "response.output_text.delta" ? payload["delta"]?.ToString()
                        : type == "content_block_delta" ? payload["delta"]?["text"]?.ToString() : null;
                    if (delta is not null)
                    {
                        var remaining = run.Settings.SummaryBytes - summaryBytes;
                        if (remaining > 0)
                        {
                            var text = TextOutput.LimitUtf8(delta, remaining);
                            summary.Append(text); summaryBytes += Encoding.UTF8.GetByteCount(text);
                        }
                    }
                    if (run.Template.Protocol == "responses" && type is "response.completed" or "response.done")
                    {
                        if (payload["response"] is not null && responseBody is null
                            || responseStatus is not null && responseStatus != "completed"
                            || type == "response.done" && responseStatus != "completed")
                            return Done(false, "Responses 结束事件未确认完成状态，未重放");
                        operation.Token.ThrowIfCancellationRequested();
                        return Done(true);
                    }
                    if (run.Template.Protocol == "messages" && type == "message_stop")
                    { operation.Token.ThrowIfCancellationRequested(); return Done(true); }
                }
            }
            return didAccept ? Done(false, "接受后 SSE 提前 EOF，未重放") : Result(ProbeKind.Protocol, "SSE 提前 EOF，未收到接受事件");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        { return didAccept ? Done(false, "完整响应超时，未重放") : Result(ProbeKind.Timeout, "等待响应头或首事件超时"); }
        catch (TimeoutException error)
        { return didAccept ? Done(false, error.Message) : Result(ProbeKind.Timeout, TextOutput.Summary(error.Message)); }
        catch (ResponseLimitException error)
        { return didAccept ? Done(false, error.Message) : Result(ProbeKind.Protocol, error.Message); }
        // 尚未收到响应时的客户端/代理配置异常应交给引擎停止，不能当成 SSE 格式错误重试。
        catch (Exception error) when (error is DecoderFallbackException || error is InvalidOperationException && status is not null)
        { return didAccept ? Done(false, "SSE 数据结构或 UTF-8 无效") : Result(ProbeKind.Protocol, "SSE 数据结构或 UTF-8 无效"); }
        catch (Exception error) when (error is HttpRequestException or IOException)
        { return didAccept ? Done(false, TextOutput.Summary(error.Message)) : Result(ProbeKind.Network, TextOutput.Summary(error.Message)); }
        ProbeResult Result(ProbeKind kind, string? error = null, double retry = 0)
        {
            token.ThrowIfCancellationRequested();
            return new ProbeResult(kind, status, error, retry, Proxy: selectedProxy, Route: selectedRoute);
        }
        ProbeResult Done(bool completed, string? error = null) => Result(ProbeKind.Accepted, error) with
        { Summary = summary.ToString(), Completed = completed };
    }

    internal static HttpRequestMessage BuildMessage(RenderedRequest rendered)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, rendered.Url) { Content = new StringContent(rendered.Body.ToJsonString(), Encoding.UTF8, "application/json") };
        foreach (var (name, value) in rendered.Headers)
        {
            if (value.Any(char.IsControl)) { request.Dispose(); throw new FormatException("展开后的 Header 含控制字符"); }
            if (!request.Headers.TryAddWithoutValidation(name, value))
            { request.Content.Headers.Remove(name); if (!request.Content.Headers.TryAddWithoutValidation(name, value)) { request.Dispose(); throw new FormatException("无效 Header"); } }
        }
        return request;
    }
    internal static async Task<string> ReadLimitedAsync(HttpContent content, int maxBytes, CancellationToken token)
    {
        await using var stream = await content.ReadAsStreamAsync(token);
        var bytes = new byte[maxBytes]; var length = 0;
        while (length < bytes.Length)
        { var read = await stream.ReadAsync(bytes.AsMemory(length), token); if (read == 0) break; length += read; }
        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}
