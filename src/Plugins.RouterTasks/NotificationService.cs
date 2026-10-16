using System.Net.Http.Json;
using Router.Contracts.Host;

namespace Plugins.RouterTasks;

internal sealed class NotificationService(TaskStore store, CredentialVault vault, IPluginHttpServices http, IRuntimeClock clock) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _work;
    public string? LastError { get; private set; }
    public void Start() { lock (_gate) if (!_stop.IsCancellationRequested) _work ??= RunAsync(); }
    private async Task RunAsync()
    {
        await Task.Yield();
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var document = await store.ReadAsync(_stop.Token);
                // 两个渠道相互独立：每个渠道串行，其余渠道不会被 Retry-After 阻塞。
                await Task.WhenAll(new[] { "telegram", "bark" }.Select(async channel =>
                {
                    var due = document.Notifications.FirstOrDefault(n => n.Channel == channel && n.State is "queued" or "retrying" && n.NextAttemptAt <= clock.Now);
                    if (due is not null) await DeliverAsync(due.Id, _stop.Token);
                }));
                LastError = null;
                await clock.Delay(TimeSpan.FromSeconds(1), _stop.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                LastError = TextOutput.Summary(error.Message);
                try { await clock.Delay(TimeSpan.FromSeconds(5), _stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }
    internal async Task DeliverAsync(string id, CancellationToken token)
    {
        var document = await store.ReadAsync(token);
        var item = document.Notifications.Find(n => n.Id == id);
        if (item is null || item.State is not ("queued" or "retrying") || item.NextAttemptAt > clock.Now) return;
        var task = document.Tasks.Find(t => t.Config.Id == item.TaskId);
        var settings = task?.Config.Settings ?? document.Settings;
        if (item.Channel == "telegram" && !settings.TelegramEnabled || item.Channel == "bark" && !settings.BarkEnabled)
        {
            await store.UpdateAsync(state => { var n = state.Notifications.Find(n => n.Id == id); if (n is null) return false;
                n.State = "paused"; n.NextAttemptAt = null; n.Error = "通知渠道已关闭"; return true; }, token); return;
        }
        var secrets = await vault.NotificationAsync(token, item.ProfileId ??
            (task?.Config.Settings is not null ? CredentialVault.AccountProfile(item.TaskId) : CredentialVault.ProfileId));
        var send = await store.UpdateAsync(state =>
        {
            var n = state.Notifications.Find(n => n.Id == id);
            if (n is null || n.State is not ("queued" or "retrying")) return false;
            n.State = "sending"; n.NextAttemptAt = null; n.Attempts++; return true;
        }, token);
        if (!send) return;
        var success = false; var retry = false; double retryAfter = 0; string? error = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var client = http.CreateDirectClient(new PluginHttpClientOptions { AllowAutoRedirect = false, RequestTimeout = System.Threading.Timeout.InfiniteTimeSpan });
            using var request = new HttpRequestMessage();
            if (item.Channel == "telegram")
            {
                var secret = secrets.GetValueOrDefault("telegramToken"); var chat = secrets.GetValueOrDefault("telegramChatId");
                if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(chat)) throw new FormatException("Telegram 凭据不完整");
                request.Method = HttpMethod.Post;
                request.RequestUri = new Uri("https://api.telegram.org/bot" + Uri.EscapeDataString(secret) + "/sendMessage");
                // 默认 JSON 元数据缓存会持有匿名插件类型，导致发送过通知的旧代际无法卸载。
                request.Content = JsonContent.Create(new { chat_id = chat, text = item.Title + "\n" + item.Message }, options: JsonData.Options);
            }
            else
            {
                var key = secrets.GetValueOrDefault("barkKey");
                if (string.IsNullOrEmpty(key)) throw new FormatException("Bark 凭据不完整");
                request.Method = HttpMethod.Get;
                request.RequestUri = new Uri(settings.BarkServer.TrimEnd('/') + "/" + string.Join('/', new[] { key, item.Title, item.Message }.Select(Uri.EscapeDataString)) + "?group=RouterTasks");
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var text = await HttpProbeClient.ReadLimitedAsync(response.Content, 4096, timeout.Token);
            success = response.IsSuccessStatusCode;
            retry = (int)response.StatusCode == 429 || (int)response.StatusCode >= 500;
            retryAfter = HttpProbeClient.RetryAfter(response.Headers, clock.Now);
            try
            {
                var payload = System.Text.Json.Nodes.JsonNode.Parse(text);
                if (item.Channel == "telegram" && payload?["ok"]?.GetValue<bool>() != true)
                {
                    success = false; var code = payload?["error_code"]?.GetValue<int>() ?? 0; retry |= code == 429 || code >= 500;
                    retryAfter = Math.Max(retryAfter, payload?["parameters"]?["retry_after"]?.GetValue<double>() ?? 0);
                }
                if (item.Channel == "bark" && payload?["code"]?.ToString() != "200")
                { success = false; var code = payload?["code"]?.ToString(); retry |= code is "429" or "500" or "502" or "503" or "504"; }
            }
            catch (Exception parse) when (parse is System.Text.Json.JsonException or InvalidOperationException) { success = false; }
            if (!success) error = TextOutput.Summary($"HTTP {(int)response.StatusCode} · {text}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await store.UpdateAsync(state => { var n = state.Notifications.Find(n => n.Id == id)!; n.State = "paused"; n.Error = "投递中断，可能已送达；待人工恢复"; return true; }); throw;
        }
        catch (Exception exception)
        { retry = exception is HttpRequestException or IOException or OperationCanceledException; error = TextOutput.Summary(exception.Message); }
        await store.UpdateAsync(state =>
        {
            var n = state.Notifications.Find(n => n.Id == id)!;
            n.Error = error;
            n.State = success ? "sent" : retry && n.Attempts < settings.NotificationMaxAttempts ? "retrying" : "dead";
            n.NextAttemptAt = n.State == "retrying" ? clock.Now.AddSeconds(Math.Max(retryAfter,
                Math.Min(settings.NotificationMaxRetrySeconds, settings.NotificationRetrySeconds * Math.Pow(2, Math.Min(30, n.Attempts - 1))))) : null;
            return true;
        });
    }
    public async ValueTask DisposeAsync()
    {
        Task? work; lock (_gate) { _stop.Cancel(); work = _work; }
        if (work is not null) await work;
    }
}
