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

    private static JsonElement AssertJson(PluginResult result, int statusCode)
    {
        Assert.AreEqual(statusCode, result.StatusCode);
        Assert.AreEqual("application/json", result.ContentType);
        Assert.IsInstanceOfType<JsonElement>(result.Body);
        return (JsonElement)result.Body!;
    }
}
