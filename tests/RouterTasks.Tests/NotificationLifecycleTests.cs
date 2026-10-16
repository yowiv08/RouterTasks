using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugins.RouterTasks;
using Router.Contracts.Host;
using Router.Contracts.Plugins;

namespace RouterTasks.Tests;

[TestClass]
public sealed class NotificationLifecycleTests
{
    [TestMethod]
    public async Task CollectiblePluginSendsNotificationsAndReleasesItsLoadContext()
    {
        var channels = new ConcurrentQueue<string>();
        await using var fixture = new Fixture(handler: async (request, token) =>
        {
            switch (request.RequestUri!.Host)
            {
                case "127.0.0.1":
                    return Fixture.Response(200, "event: response.created\ndata: {\"type\":\"response.created\"}\n\n");
                case "api.telegram.org":
                    var body = await request.Content!.ReadAsStringAsync(token);
                    Assert.IsTrue(body.Contains("CHAT-ID"));
                    channels.Enqueue("telegram");
                    return Fixture.Response(200, "{\"ok\":true}", "application/json");
                case "api.day.app":
                    Assert.IsTrue(request.RequestUri.AbsolutePath.Contains("/BARK-KEY/"));
                    channels.Enqueue("bark");
                    return Fixture.Response(200, "{\"code\":200}", "application/json");
                default:
                    throw new InvalidOperationException("Unexpected HTTP origin in offline test");
            }
        });
        var context = await SendFromCollectiblePluginAsync(fixture);
        CollectionAssert.AreEquivalent(new[] { "telegram", "bark" }, channels.ToArray());
        // 与真实宿主验收一致：让 JSON 反射访问器的短缓存到期，再触发清理。
        await Task.Delay(1200);
        AdvanceSerializerMetadata();
        for (var attempt = 0; attempt < 40 && context.IsAlive; attempt++)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            await Task.Delay(100);
        }
        Assert.IsFalse(context.IsAlive, "通知投递不能让宿主全局 JSON 缓存保留旧插件代际");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> SendFromCollectiblePluginAsync(Fixture fixture)
    {
        var loadContext = new AssemblyLoadContext("router-tasks-notifications-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        var weak = new WeakReference(loadContext);
        var assembly = loadContext.LoadFromAssemblyPath(typeof(RouterTasksTerminal).Assembly.Location);
        var terminal = assembly.GetType("Plugins.RouterTasks.RouterTasksTerminal")!;
        var clock = Activator.CreateInstance(assembly.GetType("Plugins.RouterTasks.RuntimeClock")!, nonPublic: true);
        var constructor = terminal.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        var plugin = constructor.Invoke([fixture.Services.Object, fixture.Directory, clock, null]);
        try
        {
            async Task<JsonElement> Call(string method, object? body = null)
            {
                var context = Fixture.Context(body is null ? null : JsonSerializer.SerializeToElement(body, JsonData.Options));
                var result = await (Task<PluginResult>)terminal.GetMethod(method)!.Invoke(plugin, [context])!;
                Assert.AreEqual(200, result.StatusCode, JsonSerializer.Serialize(result.Body));
                return (JsonElement)result.Body!;
            }
            var config = fixture.Config with { Mode = "queue", Settings = new PluginSettings { TelegramEnabled = true, BarkEnabled = true },
                RequestOverride = fixture.Template, NotificationEvents = ["entered"] };
            await Call("SaveAccountAsync", new SaveAccountInput(config, fixture.Site, "ACCOUNT-TOKEN",
                Secrets: new NotificationSecrets("123456:BOT-TOKEN", "CHAT-ID", "BARK-KEY", CredentialVersion: 0)));
            var action = await Call("TaskActionAsync", new TaskActionInput([config.Id], "resume"));
            Assert.IsTrue(action.GetProperty("results")[0].GetProperty("success").GetBoolean());
            await Fixture.Until(async () =>
            {
                var state = await Call("StateAsync");
                var notifications = state.GetProperty("notifications");
                if (notifications.EnumerateArray().Any(item => item.GetProperty("state").GetString() == "dead"))
                    Assert.Fail("Collectible plugin delivery failed: " + notifications.GetRawText());
                return notifications.GetArrayLength() == 2 && notifications.EnumerateArray().All(item => item.GetProperty("state").GetString() == "sent");
            });
        }
        finally
        {
            await ((IPluginModule)plugin).StopAsync(CancellationToken.None);
            loadContext.Unload();
        }
        return weak;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AdvanceSerializerMetadata()
        => _ = new DefaultJsonTypeInfoResolver().GetTypeInfo(typeof(HostSerializationProbe), new JsonSerializerOptions());

    private sealed record HostSerializationProbe(int Value);
}
