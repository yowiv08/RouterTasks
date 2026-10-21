using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugins.RouterTasks;
using Router.Contracts.Host;

namespace RouterTasks.Tests;

[TestClass]
public sealed class ResponseBoundaryTests
{
    [TestMethod]
    public async Task SuccessfulManagementResponsesContainOnlyDetachedJson()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        var state = AssertJson(await fixture.Plugin.StateAsync(Fixture.Context()), 200);
        Assert.AreEqual("task", state.GetProperty("tasks")[0].GetProperty("config").GetProperty("id").GetString());
        AssertJson(await fixture.Plugin.TaskDetailAsync(Fixture.Context(id: "task")), 200);
        AssertJson(await fixture.Plugin.TemplateDetailAsync(Fixture.Context(id: "test")), 200);
        var account = AssertJson(await fixture.Plugin.AccountDetailAsync(Fixture.Context(id: "task")), 200);
        Assert.AreEqual("TOKEN-secret-test-only", account.GetProperty("token").GetString());
        await fixture.Plugin.StopAsync(default);
        Assert.AreEqual("task", state.GetProperty("tasks")[0].GetProperty("config").GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task ManagementErrorsAlsoContainOnlyDetachedJson()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        AssertJson(await fixture.Plugin.TaskDetailAsync(Fixture.Context(id: "missing")), 404);
        AssertJson(await fixture.Plugin.SaveSiteAsync(Fixture.Context(new SiteConfig { Id = "bad", BaseUrl = "invalid" })), 400);
        AssertJson(await fixture.Plugin.SaveTaskAsync(Fixture.Context(new SaveTaskInput(fixture.Config, 0))), 409);
        await fixture.Plugin.StopAsync(default);
        var stopped = AssertJson(await fixture.Plugin.StateAsync(Fixture.Context()), 503);
        Assert.AreEqual("插件已停止", stopped.GetProperty("error").GetString());
    }

    [TestMethod]
    public async Task ClockReferencesAreFreshWithoutChangingRecordedTimes()
    {
        await using var fixture = new Fixture();
        await fixture.SeedAsync();
        var initialNow = fixture.Clock.Now;
        var lastSuccess = initialNow.AddMinutes(-3);
        var nextAttempt = initialNow.AddSeconds(90);
        await fixture.Plugin.Store.UpdateAsync(document =>
        {
            var task = document.Tasks.Single();
            task.Runtime.LastSuccessAt = lastSuccess;
            task.Runtime.NextAttemptAt = nextAttempt;
            TaskStore.AddEvent(document, task, lastSuccess, "accepted", "Recorded acceptance");
            return true;
        });
        var revision = (await fixture.Plugin.Store.ReadAsync()).Revision;

        var first = AssertJson(await fixture.Plugin.StateAsync(Fixture.Context()), 200);
        Assert.AreEqual(initialNow, first.GetProperty("serverNow").GetDateTimeOffset());
        fixture.Clock.Advance(300);
        var state = AssertJson(await fixture.Plugin.StateAsync(Fixture.Context()), 200);
        var detail = AssertJson(await fixture.Plugin.TaskDetailAsync(Fixture.Context(id: "task")), 200);
        foreach (var response in new[] { state, detail })
        {
            var reference = response.GetProperty("serverNow").GetDateTimeOffset();
            Assert.AreEqual(fixture.Clock.Now, reference);
            Assert.AreEqual(TimeSpan.Zero, reference.Offset);
            var runtime = response.TryGetProperty("runtime", out var directRuntime)
                ? directRuntime : response.GetProperty("tasks")[0].GetProperty("runtime");
            Assert.AreEqual(lastSuccess, runtime.GetProperty("lastSuccessAt").GetDateTimeOffset());
            Assert.AreEqual(nextAttempt, runtime.GetProperty("nextAttemptAt").GetDateTimeOffset());
        }
        Assert.AreEqual(initialNow, first.GetProperty("serverNow").GetDateTimeOffset());
        Assert.AreEqual(lastSuccess, detail.GetProperty("events")[0].GetProperty("at").GetDateTimeOffset());
        fixture.Clock.Advance(30);
        var refreshedDetail = AssertJson(await fixture.Plugin.TaskDetailAsync(Fixture.Context(id: "task")), 200);
        Assert.AreEqual(fixture.Clock.Now, refreshedDetail.GetProperty("serverNow").GetDateTimeOffset());
        var stored = await fixture.Plugin.Store.ReadAsync();
        Assert.AreEqual(revision, stored.Revision);
        Assert.AreEqual(lastSuccess, stored.Tasks.Single().Runtime.LastSuccessAt);
        Assert.AreEqual(nextAttempt, stored.Tasks.Single().Runtime.NextAttemptAt);
        Assert.AreEqual(lastSuccess, stored.Tasks.Single().Events.Single().At);
    }

    private static JsonElement AssertJson(PluginResult result, int statusCode)
    {
        Assert.AreEqual(statusCode, result.StatusCode);
        Assert.AreEqual("application/json", result.ContentType);
        Assert.IsInstanceOfType<JsonElement>(result.Body);
        return (JsonElement)result.Body!;
    }
}
