using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using Plugins.RouterTasks;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace RouterTasks.Tests;

[TestClass]
public sealed class ManagementTests
{
    private static PluginHttpContext Json(string text) => Fixture.Context(JsonDocument.Parse(text).RootElement.Clone());

    [TestMethod]
    public void EveryEndpointRequiresAdminAndPageIsSelfContained()
    {
        var endpoints = typeof(RouterTasksTerminal).GetMethods().Select(m => m.GetCustomAttribute<PluginEndpointAttribute>()).OfType<PluginEndpointAttribute>().ToArray();
        Assert.IsGreaterThanOrEqualTo(18, endpoints.Length);
        Assert.IsTrue(endpoints.All(e => e.Auth == PluginAuthPolicy.AdminSession));
        Assert.HasCount(1, endpoints.Where(e => e.Path == "tasks/action" && e.Method == "POST").ToList());
    }

    [TestMethod]
    public async Task MalformedNestedInputsFailWith400NotServerExceptions()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var cases = new (Func<PluginHttpContext, Task<PluginResult>> Call, string Body)[]
        {
            (f.Plugin.SaveTaskAsync, "{\"config\":null}"),
            (f.Plugin.SaveSettingsAsync, "{\"settings\":null}"),
            (f.Plugin.SaveSiteAsync, "{\"subscriptionIds\":null}"),
            (f.Plugin.SaveTemplateAsync, "{\"headers\":null}"),
            (f.Plugin.SaveTemplateAsync, "{\"body\":[]}"),
            (f.Plugin.SaveTemplateAsync, "{\"body\":\"{broken\"}"),
            (f.Plugin.ImportAsync, "{\"schemaVersion\":1,\"sites\":null,\"templates\":[],\"tasks\":[]}"),
            (f.Plugin.ImportAsync, "{\"schemaVersion\":1,\"sites\":[null],\"templates\":[],\"tasks\":[],\"settings\":{}}"),
            (f.Plugin.DeleteTaskAsync, "{\"id\":null}"),
            (f.Plugin.TaskActionAsync, "{\"ids\":null,\"action\":\"start\"}")
        };
        foreach (var (call, body) in cases) Assert.AreEqual(400, (await call(Json(body))).StatusCode, body);
    }

    [TestMethod]
    public async Task TemplateBodyIsLosslessAcrossTheManagementBridge()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        const string number = "9007199254740993123456789";
        var result = await f.Plugin.SaveTemplateAsync(Json("{\"id\":\"big\",\"name\":\"Big\",\"body\":{\"stream\":true,\"unknown\":" + number + "}}"));
        Assert.AreEqual(200, result.StatusCode);
        var json = JsonSerializer.SerializeToNode(result.Body, JsonData.Options)!;
        var raw = json["body"]!.GetValue<string>(); StringAssert.Contains(raw, number);
        Assert.AreEqual(200, (await f.Plugin.SaveTemplateAsync(Fixture.Context(JsonSerializer.SerializeToElement(result.Body, JsonData.Options)))).StatusCode);
        StringAssert.Contains((await f.Plugin.Store.ReadAsync()).Templates.Single(t => t.Id == "big").Body.ToJsonString(), number);
        var invalid = new RequestTemplate { Body = new JsonObject { ["stream"] = "true" } };
        Assert.AreEqual(400, (await f.Plugin.SaveTemplateAsync(Fixture.Context(invalid))).StatusCode);
    }

    [TestMethod]
    public async Task ImmutableSnapshotsAndEditConflictsAreEnforced()
    {
        var probe = new ScriptProbe((_, _, token) => ScriptProbe.Block(token));
        await using var f = new Fixture(probe); await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(probe.Calls == 1));
        var changed = f.Config with { Model = "NEW" };
        Assert.AreEqual(409, (await f.Plugin.SaveTaskAsync(Fixture.Context(new SaveTaskInput(changed, 1)))).StatusCode);
        Assert.AreEqual(409, (await f.Plugin.SaveSiteAsync(Fixture.Context(f.Site))).StatusCode);
        Assert.AreEqual(200, (await f.Plugin.SaveTemplateAsync(Fixture.Context(f.Template with { Path = "other" }))).StatusCode);
        var saved = await f.TaskAsync(); Assert.AreEqual("responses", saved.Snapshot!.Template.Path); Assert.AreEqual("MODEL", saved.Snapshot.Config.Model);
        await f.Plugin.Engine.ActionAsync("task", "pause");
        Assert.AreEqual(409, (await f.Plugin.SaveTaskAsync(Fixture.Context(new SaveTaskInput(changed, 0)))).StatusCode);
        Assert.AreEqual(200, (await f.Plugin.SaveTaskAsync(Fixture.Context(new SaveTaskInput(changed, 1)))).StatusCode);
        var preview = await f.Plugin.PreviewAsync(Fixture.Context(changed));
        Assert.AreEqual(200, preview.StatusCode); StringAssert.Contains(JsonSerializer.Serialize(preview.Body), "TOKEN-secret-test-only");
        Assert.AreEqual(1, probe.Calls, "预览不能发送模型请求");
    }

    [TestMethod]
    public async Task QuotaFailureKeepsOldValueAndDoesNotUseQueueCodes()
    {
        var calls = 0;
        await using var f = new Fixture(handler: (request, _) =>
        {
            calls++;
            return Task.FromResult(Fixture.Response(request.RequestUri!.AbsolutePath.EndsWith("models", StringComparison.Ordinal) ? 200 : 500,
                request.RequestUri.AbsolutePath.EndsWith("models", StringComparison.Ordinal) ? "{\"data\":[{\"id\":\"MODEL\"}]}" : "TOKEN-secret-test-only", "application/json"));
        });
        await f.SeedAsync(); await f.Plugin.Store.UpdateAsync(d => { d.Keys[0].UsedUsd = 4.5m; d.Keys[0].LimitUsd = 10; return true; });
        Assert.AreEqual(200, (await f.Plugin.RefreshKeyAsync(Fixture.Context(new IdInput("key")))).StatusCode);
        var key = (await f.Plugin.Store.ReadAsync()).Keys.Single();
        Assert.AreEqual(4.5m, key.UsedUsd); Assert.AreEqual(10m, key.LimitUsd); Assert.AreEqual(2, calls);
        StringAssert.Contains(key.UsageError!, "TOKEN-secret-test-only"); Assert.AreEqual(0L, (await f.TaskAsync()).Runtime.TotalAttempts);
    }

    [TestMethod]
    public async Task ReconciliationRecoversMetadataWithoutExportingSecrets()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await f.Plugin.Store.UpdateAsync(d => { d.Keys.Clear(); return true; });
        Assert.AreEqual(200, (await f.Plugin.ReconcileKeysAsync(Fixture.Context())).StatusCode);
        Assert.HasCount(1, (await f.Plugin.Store.ReadAsync()).Keys);
        Assert.IsFalse(JsonSerializer.Serialize((await f.Plugin.StateAsync(Fixture.Context())).Body).Contains("TOKEN-secret-test-only"));
    }

    [TestMethod]
    public async Task BatchControlsReportPerTaskFailuresAndStopDrainsThem()
    {
        var clock = new ManualClock(); var probe = new ScriptProbe((_, _, token) => ScriptProbe.Block(token));
        await using var f = new Fixture(probe, runtimeClock: clock); await f.SeedAsync();
        await f.Plugin.Store.UpdateAsync(d => { d.Tasks.Add(new TaskRecord { Config = f.Config with { Id = "task2" } }); return true; });
        var response = await f.Plugin.TaskActionAsync(Fixture.Context(new TaskActionInput(["task", "missing", "task2", "task"], "start")));
        var results = JsonSerializer.SerializeToNode(response.Body, JsonData.Options)!["results"]!.AsArray();
        Assert.HasCount(3, results); Assert.AreEqual(1, results.Count(r => !r!["success"]!.GetValue<bool>()));
        await Fixture.Until(() => Task.FromResult(probe.Calls == 2));
        Assert.AreEqual(2, f.Plugin.Engine.ActiveCount);
        await f.Plugin.StopAsync(default); Assert.AreEqual(0, f.Plugin.Engine.ActiveCount);
        Assert.AreEqual(503, (await f.Plugin.StateAsync(Fixture.Context())).StatusCode);
        await using var restored = new TaskStore(f.Directory, TemplateRenderer.Presets, clock);
        Assert.IsTrue((await restored.ReadAsync()).Tasks.All(t => t.Runtime.Status == "paused"));
    }

    [TestMethod]
    public async Task CandidateStartAndRejectedCandidateStopDoNotTouchTheActiveStore()
    {
        var probe = new ScriptProbe((_, _, token) => ScriptProbe.Block(token));
        await using var f = new Fixture(probe); await f.SeedAsync(); await f.Plugin.Engine.ActionAsync("task", "start");
        await Fixture.Until(() => Task.FromResult(probe.Calls == 1));
        var original = await File.ReadAllTextAsync(Path.Combine(f.Directory, "state.json"));
        var candidate = new RouterTasksTerminal(f.Services.Object, f.Directory, f.Clock, probe);
        await candidate.StartAsync(new PluginStartContext("router-tasks", Mock.Of<IPluginHost>()), default);
        Assert.IsTrue(candidate.GetMainPage().Html.Contains("window.Router2API"));
        await candidate.StopAsync(default);
        Assert.AreEqual(original, await File.ReadAllTextAsync(Path.Combine(f.Directory, "state.json")));
        Assert.AreEqual(1, f.Plugin.Engine.ActiveCount);
    }
}
