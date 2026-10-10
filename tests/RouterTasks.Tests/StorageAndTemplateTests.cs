using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugins.RouterTasks;

namespace RouterTasks.Tests;

[TestClass]
public sealed class StorageAndTemplateTests
{
    [TestMethod]
    public void ClaudeContextDefaultsOnAndDoesNotAffectResponses()
    {
        var config = JsonSerializer.Deserialize<TaskConfig>("{}", JsonData.Options)!;
        Assert.IsTrue(config.Claude1MContext);
        var template = TemplateRenderer.Presets().Single(t => t.Protocol == "responses");
        var rendered = TemplateRenderer.Render(new RunSnapshot(config, new(), template, "TOKEN", new(), "g"), "P", DateTimeOffset.UtcNow);
        Assert.IsFalse(rendered.Headers.ContainsKey("anthropic-beta"));
    }

    [TestMethod]
    [DataRow(true, "claude-opus-4-6")]
    [DataRow(false, "claude-opus-4-6")]
    [DataRow(true, "claude-opus-4-6[1m]")]
    [DataRow(false, "claude-opus-4-6[1M]")]
    public void ClaudeContextToggleOverridesTemplateAndLegacySuffix(bool enabled, string model)
    {
        var template = TemplateRenderer.Presets().Single(t => t.Protocol == "messages");
        template.Headers["anthropic-beta"] = "claude-code-20250219, context-1m-2025-08-07,CONTEXT-1M-2025-08-07";
        var config = new TaskConfig { Model = model, Claude1MContext = enabled };
        var rendered = TemplateRenderer.Render(new RunSnapshot(config, new(), template, "TOKEN", new(), "g"), "P", DateTimeOffset.UtcNow);
        Assert.AreEqual("claude-opus-4-6", rendered.Body["model"]!.GetValue<string>());
        var flags = rendered.Headers["anthropic-beta"].Split(',');
        Assert.AreEqual(enabled ? 1 : 0, flags.Count(flag => flag.Equals("context-1m-2025-08-07", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(flags.Contains("claude-code-20250219"));
        Assert.AreEqual("https://anyrouter.top/v1/messages", rendered.Url.AbsoluteUri);
    }

    [TestMethod]
    public void BothFullPresetsAreEmbeddedAndKeepTheirContracts()
    {
        var presets = TemplateRenderer.Presets(); Assert.HasCount(2, presets);
        var responses = presets.Single(t => t.Protocol == "responses");
        Assert.AreEqual("additional_tools", responses.Body["input"]![0]!["type"]!.ToString());
        Assert.IsTrue(responses.Body["input"]!.AsArray().Count >= 7);
        Assert.IsTrue(presets.Single(t => t.Protocol == "messages").Body["tools"]!.AsArray().Count > 5);
        foreach (var template in presets) Validation.Template(template);
    }
    [TestMethod]
    public void IdentitiesAreFreshAndHeadBodyMetadataAgree()
    {
        var config = new TaskConfig { Model = "MODEL", Prompt = "literal {{TOKEN}} \" escaped" };
        var template = TemplateRenderer.Presets()[0];
        var snapshot = new RunSnapshot(config, new SiteConfig(), template, "TOKEN-secret", new(), "generation");
        var at = DateTimeOffset.Parse("2026-10-10T12:00:00Z");
        var first = TemplateRenderer.Render(snapshot, config.Prompt, at);
        var second = TemplateRenderer.Render(snapshot, config.Prompt, at);
        Assert.AreNotEqual(first.Headers["session-id"], second.Headers["session-id"]);
        Assert.AreEqual(first.Headers["x-codex-turn-metadata"], first.Body["client_metadata"]!["x-codex-turn-metadata"]!.ToString());
        Assert.AreEqual(at.ToUnixTimeMilliseconds(), JsonNode.Parse(first.Headers["x-codex-turn-metadata"])!["turn_started_at_unix_ms"]!.GetValue<long>());
        StringAssert.Contains(first.Body.ToJsonString(), "{{TOKEN}}");
        Assert.IsFalse(first.Body.ToJsonString().Contains("TOKEN-secret"));
        var preview = TemplateRenderer.Render(snapshot, config.Prompt, at);
        StringAssert.Contains(JsonSerializer.Serialize(preview), "TOKEN-secret");
    }
    [TestMethod]
    public void UnknownBodyFieldsAndLargeIntegerArePreserved()
    {
        var template = new RequestTemplate { Headers = new() { ["Authorization"] = "Bearer {{TOKEN}}" },
            Body = JsonNode.Parse("{\"stream\":true,\"model\":\"{{MODEL}}\",\"unknown\":{\"id\":9007199254740993123456789}}")!.AsObject() };
        var rendered = TemplateRenderer.Render(new RunSnapshot(new TaskConfig { Model = "MODEL" }, new(), template, "TOKEN", new(), "g"), "P", DateTimeOffset.UtcNow);
        StringAssert.Contains(rendered.Body.ToJsonString(), "9007199254740993123456789");
    }
    [TestMethod]
    [DataRow("https://example.com", "responses", "https://example.com/v1/responses")]
    [DataRow("https://example.com/v1", "responses", "https://example.com/v1/responses")]
    [DataRow("https://example.com/prefix", "messages?beta=true", "https://example.com/prefix/v1/messages?beta=true")]
    [DataRow("https://example.com/prefix/v1", "/custom/responses", "https://example.com/custom/responses")]
    public void UrlNormalization(string root, string path, string expected) => Assert.AreEqual(expected, Validation.Endpoint(root, path).AbsoluteUri);
    [TestMethod]
    public void UnsafeUrlsHeadersAndInvalidSettingsAreRejected()
    {
        Assert.ThrowsExactly<FormatException>(() => Validation.BaseUri("https://user:pass@example.com"));
        Assert.ThrowsExactly<FormatException>(() => Validation.Endpoint("https://example.com", "https://elsewhere.com/v1/responses"));
        Assert.ThrowsExactly<FormatException>(() => Validation.Endpoint("https://example.com", "//elsewhere.com"));
        var template = TemplateRenderer.Presets()[0]; template.Headers["Authorization"] = "Bearer real-secret";
        Assert.ThrowsExactly<FormatException>(() => Validation.Template(template));
        template.Headers["Authorization"] = "Bearer {{TOKEN}}"; template.Headers["Content-Length"] = "42";
        Assert.ThrowsExactly<FormatException>(() => Validation.Template(template));
    }
    [TestMethod]
    public async Task AtomicChangesAndRecoveryPauseWithoutAutomaticWork()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await f.Plugin.Store.UpdateAsync(d => { var t = d.Tasks[0]; t.Runtime.Status = "running"; t.Runtime.Generation = "old"; t.Runtime.TotalAttempts = 13;
            t.Runtime.NextAttemptAt = f.Clock.Now; d.Notifications.Add(new NotificationItem { Id = "n", State = "sending", CreatedAt = f.Clock.Now }); return true; });
        var original = await File.ReadAllTextAsync(Path.Combine(f.Directory, "state.json"));
        await Assert.ThrowsExactlyAsync<FormatException>(() => f.Plugin.Store.UpdateAsync<bool>(d => { d.Tasks.Clear(); throw new FormatException("no commit"); }));
        Assert.AreEqual(original, await File.ReadAllTextAsync(Path.Combine(f.Directory, "state.json")));
        await f.Plugin.Store.DisposeAsync();
        await using var restored = new TaskStore(f.Directory, TemplateRenderer.Presets, f.Clock);
        var document = await restored.ReadAsync();
        Assert.AreEqual("paused", document.Tasks[0].Runtime.Status); Assert.AreEqual(13L, document.Tasks[0].Runtime.TotalAttempts);
        Assert.IsNull(document.Tasks[0].Runtime.Generation); Assert.IsNull(document.Tasks[0].Runtime.NextAttemptAt);
        Assert.AreEqual("paused", document.Notifications[0].State); Assert.IsTrue(File.Exists(Path.Combine(f.Directory, "state.json.bak")));
    }
    [TestMethod]
    public async Task CorruptStateFailsClosedWithoutOverwriting()
    {
        await using var f = new Fixture(); await f.SeedAsync(); await f.Plugin.Store.DisposeAsync();
        var path = Path.Combine(f.Directory, "state.json"); await File.WriteAllTextAsync(path, "{broken");
        await using var store = new TaskStore(f.Directory, TemplateRenderer.Presets, f.Clock);
        await Assert.ThrowsExactlyAsync<JsonException>(() => store.ReadAsync()); Assert.AreEqual("{broken", await File.ReadAllTextAsync(path));
    }
    [TestMethod]
    public async Task IndependentWriterCannotTakeExistingLease()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await using var second = new TaskStore(f.Directory, TemplateRenderer.Presets, f.Clock);
        await Assert.ThrowsExactlyAsync<IOException>(() => second.ReadAsync());
    }
    [TestMethod]
    public async Task CredentialCasAndExportNeverContainSecret()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var saved = await f.Plugin.SaveKeyAsync(Fixture.Context(new SaveKeyInput("key", "site", "Alias", "NEW-TOKEN-secret", 1)));
        Assert.AreEqual(200, saved.StatusCode);
        var conflict = await f.Plugin.SaveKeyAsync(Fixture.Context(new SaveKeyInput("key", "site", "Alias", "STALE", 1)));
        Assert.AreEqual(409, conflict.StatusCode);
        var export = await f.Plugin.ExportAsync(Fixture.Context());
        Assert.IsFalse(JsonSerializer.Serialize(export).Contains("NEW-TOKEN-secret"));
        Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(f.Directory, "state.json"))).Contains("NEW-TOKEN-secret"));
    }
}
