using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Plugins.RouterTasks;

internal sealed record RenderedRequest(Uri Url, Dictionary<string, string> Headers, JsonObject Body);

internal static partial class TemplateRenderer
{
    [GeneratedRegex(@"\{\{([A-Z_]+)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();

    public static List<RequestTemplate> Presets() => new[] { "responses", "messages" }.Select(name =>
    {
        using var stream = typeof(TemplateRenderer).Assembly.GetManifestResourceStream($"Plugins.RouterTasks.Contracts.{name}.json")
            ?? throw new InvalidOperationException("内置模板缺失");
        return JsonSerializer.Deserialize<RequestTemplate>(stream, JsonData.Options)!;
    }).ToList();

    public static RenderedRequest Render(RunSnapshot snapshot, string prompt, DateTimeOffset now)
    {
        var slots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TOKEN"] = snapshot.ApiKey,
            ["MODEL"] = snapshot.Config.Model,
            ["PROMPT"] = prompt,
            ["CURRENT_DATE"] = now.ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd"),
            ["DEVICE_ID"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
        };
        foreach (var name in new[] { "INSTALLATION_ID", "SESSION_ID", "TURN_ID", "CONTEXT_WINDOW_ID", "TOOLS_ITEM_ID", "BASE_MESSAGE_ID", "CONTEXT_MESSAGE_ID", "COLLABORATION_MESSAGE_ID", "MODE_MESSAGE_ID", "ENVIRONMENT_MESSAGE_ID", "USER_MESSAGE_ID" })
            slots[name] = Guid.NewGuid().ToString();

        // 仅遍历模板原字符串一次；用户提示词内的 {{TOKEN}} 不会二次展开。
        string Expand(string value) => Variable().Replace(value, match => slots.GetValueOrDefault(match.Groups[1].Value, match.Value));
        JsonNode? ExpandNode(JsonNode? node) => node switch
        {
            JsonObject obj => new JsonObject(obj.Select(pair => KeyValuePair.Create(pair.Key, ExpandNode(pair.Value)))),
            JsonArray array => new JsonArray(array.Select(ExpandNode).ToArray()),
            JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Expand(text)),
            _ => node?.DeepClone()
        };
        var headers = snapshot.Template.Headers.ToDictionary(pair => pair.Key, pair => Expand(pair.Value), StringComparer.OrdinalIgnoreCase);
        // Linux 模式也覆盖旧模板里已写成字面量的固定提示词，不依赖其仍保留 {{PROMPT}}。
        var templateBody = snapshot.Config.UseSourcePrompt ? SourcePromptBody(snapshot.Template) : snapshot.Template.Body;
        var body = (JsonObject)ExpandNode(templateBody)!;
        if (headers.TryGetValue("x-codex-turn-metadata", out var metadataText))
        {
            var metadata = JsonNode.Parse(metadataText)?.AsObject() ?? throw new FormatException("turn metadata 必须是 JSON 对象");
            metadata["turn_started_at_unix_ms"] = now.ToUnixTimeMilliseconds();
            var serialized = metadata.ToJsonString();
            headers["x-codex-turn-metadata"] = serialized;
            if (body["client_metadata"] is JsonObject client) client["x-codex-turn-metadata"] = serialized;
        }
        if (snapshot.Template.Protocol == "messages")
        {
            // 兼容旧账号模型后缀；是否启用由账号开关决定，不让模板中的旧标记覆盖关闭操作。
            if (snapshot.Config.Model.EndsWith("[1m]", StringComparison.OrdinalIgnoreCase)) body["model"] = snapshot.Config.Model[..^4];
            const string contextFlag = "context-1m-2025-08-07";
            var beta = headers.GetValueOrDefault("anthropic-beta", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Where(value => !value.Equals(contextFlag, StringComparison.OrdinalIgnoreCase)).ToList();
            if (snapshot.Config.Claude1MContext) beta.Add(contextFlag);
            if (beta.Count > 0) headers["anthropic-beta"] = string.Join(',', beta);
            else headers.Remove("anthropic-beta");
        }
        return new RenderedRequest(Validation.Endpoint(snapshot.Site.BaseUrl, snapshot.Template.Path), headers, body);
    }

    private static JsonObject SourcePromptBody(RequestTemplate template)
    {
        var body = (JsonObject)template.Body.DeepClone();
        var key = template.Protocol == "responses" ? "input" : "messages";
        if (key == "input" && TextValue(body[key]) is not null)
        {
            body[key] = "{{PROMPT}}";
            return body;
        }
        var user = (body[key] as JsonArray)?.OfType<JsonObject>().LastOrDefault(message =>
            TextValue(message["role"]) == "user" && (key != "input" || message["type"] is null || TextValue(message["type"]) == "message"));
        if (user is null) throw new FormatException("Linux 源码模式需要 Body.input 文本或可映射的 user 消息");
        if (TextValue(user["content"]) is not null) user["content"] = "{{PROMPT}}";
        else
        {
            var block = (user["content"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(item => TextValue(item["type"]) is "input_text" or "text");
            if (block is null) throw new FormatException("Linux 源码模式需要最后一条 user 消息包含文本块");
            block["text"] = "{{PROMPT}}";
        }
        return body;
    }

    private static string? TextValue(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}
