using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugins.RouterTasks;

namespace RouterTasks.Tests;

[TestClass]
public sealed class NotificationTests
{
    [TestMethod]
    [DataRow("queue", "entered")]
    [DataRow("keepalive", "entered")]
    [DataRow("queue_then_keepalive", "entered")]
    [DataRow("queue", "stopped")]
    public async Task AccountTaskActionsStartBackgroundDelivery(string mode, string notificationKind)
    {
        var clock = new ManualClock();
        var accepted = notificationKind == "entered";
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(accepted ? ProbeKind.Accepted : ProbeKind.Permanent,
            accepted ? 200 : 401, accepted ? null : "Unauthorized")));
        var requests = new System.Collections.Concurrent.ConcurrentQueue<(Uri Uri, string Body)>();
        await using var f = new Fixture(probe, async (request, token) =>
        {
            requests.Enqueue((request.RequestUri!, request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
            return Fixture.Response(200, request.RequestUri!.Host == "api.telegram.org" ? "{\"ok\":true}" : "{\"code\":200}", "application/json");
        }, runtimeClock: clock);
        var config = f.Config with { Mode = mode, Settings = new PluginSettings { TelegramEnabled = true, BarkEnabled = true },
            RequestOverride = f.Template, NotificationEvents = [notificationKind] };
        var save = await f.Plugin.SaveAccountAsync(Fixture.Context(new SaveAccountInput(config, f.Site, "ACCOUNT-TOKEN",
            Secrets: new NotificationSecrets("BOT-TOKEN", "CHAT-ID", "BARK-KEY", CredentialVersion: 0))));
        Assert.AreEqual(200, save.StatusCode, JsonSerializer.Serialize(save.Body));
        var response = await f.Plugin.TaskActionAsync(Fixture.Context(new TaskActionInput([config.Id], "resume")));
        Assert.AreEqual(200, response.StatusCode);
        Assert.IsTrue(JsonSerializer.SerializeToNode(response.Body, JsonData.Options)!["results"]![0]!["success"]!.GetValue<bool>());
        await Fixture.Until(async () => (await f.Plugin.Store.ReadAsync()).Notifications.Count == 2);
        await Fixture.Until(async () =>
        {
            clock.Advance(1);
            return (await f.Plugin.Store.ReadAsync()).Notifications.All(n => n.State == "sent");
        });
        Assert.HasCount(2, requests);
        Assert.IsTrue(requests.Any(r => r.Uri.Host == "api.telegram.org" && r.Body.Contains("CHAT-ID")));
        Assert.IsTrue(requests.Any(r => r.Uri.Host == "api.day.app" && r.Uri.AbsolutePath.Contains("/BARK-KEY/")));
        Assert.IsTrue((await f.Plugin.Store.ReadAsync()).Notifications.All(n => n.Kind == notificationKind && n.Attempts == 1));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task ChangingModeStartsANewEntryNotificationForBothChannels(bool notificationsPreviouslyEnabled)
    {
        var clock = new ManualClock();
        var probe = new ScriptProbe((_, _, _) => Task.FromResult(new ProbeResult(ProbeKind.Accepted, 200)));
        await using var f = new Fixture(probe, (request, _) => Task.FromResult(Fixture.Response(200,
            request.RequestUri!.Host == "api.telegram.org" ? "{\"ok\":true}" : "{\"code\":200}", "application/json")), runtimeClock: clock);
        var config = f.Config with { Mode = "queue", Settings = new PluginSettings
            { TelegramEnabled = notificationsPreviouslyEnabled, BarkEnabled = notificationsPreviouslyEnabled },
            RequestOverride = f.Template, NotificationEvents = ["entered"] };
        var saved = await f.Plugin.SaveAccountAsync(Fixture.Context(new SaveAccountInput(config, f.Site, "ACCOUNT-TOKEN",
            Secrets: new NotificationSecrets("BOT-TOKEN", "CHAT-ID", "BARK-KEY", CredentialVersion: 0))));
        Assert.AreEqual(200, saved.StatusCode);
        var account = JsonData.Read<AccountDetail>(saved.Body);
        var action = await f.Plugin.TaskActionAsync(Fixture.Context(new TaskActionInput([config.Id], "resume")));
        Assert.IsTrue(JsonSerializer.SerializeToNode(action.Body, JsonData.Options)!["results"]![0]!["success"]!.GetValue<bool>());
        await Fixture.Until(async () =>
        {
            clock.Advance(1);
            var document = await f.Plugin.Store.ReadAsync();
            return f.Plugin.Engine.ActiveCount == 0 && document.Notifications.Count == (notificationsPreviouslyEnabled ? 2 : 0)
                && document.Notifications.All(n => n.State == "sent");
        });
        var oldIds = (await f.Plugin.Store.ReadAsync()).Notifications.Select(n => n.Id).ToArray();
        var edited = await f.Plugin.SaveAccountAsync(Fixture.Context(new SaveAccountInput(account.Config with
            { Mode = "keepalive", Settings = new PluginSettings { TelegramEnabled = true, BarkEnabled = true } },
            account.Site, account.Token, account.CredentialVersion, account.ConfigRevision, account.Secrets)));
        Assert.AreEqual(200, edited.StatusCode, JsonSerializer.Serialize(edited.Body));
        action = await f.Plugin.TaskActionAsync(Fixture.Context(new TaskActionInput([config.Id], "resume")));
        Assert.IsTrue(JsonSerializer.SerializeToNode(action.Body, JsonData.Options)!["results"]![0]!["success"]!.GetValue<bool>(),
            JsonSerializer.Serialize(action.Body));
        await Fixture.Until(async () =>
        {
            clock.Advance(1);
            var notifications = (await f.Plugin.Store.ReadAsync()).Notifications;
            return notifications.Count == oldIds.Length + 2 && notifications.All(n => n.State == "sent");
        });
        var notices = (await f.Plugin.Store.ReadAsync()).Notifications;
        Assert.AreEqual(notificationsPreviouslyEnabled ? 2 : 1, notices.Count(n => n.Channel == "telegram"));
        Assert.AreEqual(notificationsPreviouslyEnabled ? 2 : 1, notices.Count(n => n.Channel == "bark"));
        Assert.IsTrue(oldIds.All(id => notices.Any(n => n.Id == id)), "切模式保留历史通知回执");
    }

    private static async Task<NotificationService> Prepare(Fixture f)
    {
        await f.SeedAsync(); var vault = new CredentialVault(f.Accounts.Object);
        await vault.SaveNotificationsAsync(new NotificationSecrets("TELEGRAM-TOKEN", "CHAT-ID", "BARK-KEY"), default);
        await f.Plugin.Store.UpdateAsync(d =>
        {
            d.Settings.TelegramEnabled = d.Settings.BarkEnabled = true;
            TaskStore.Notify(d, d.Tasks[0], f.Clock.Now, "entered", "Accepted"); return true;
        });
        return new NotificationService(f.Plugin.Store, vault, f.Http.Object, f.Clock);
    }

    [TestMethod]
    public async Task ChannelRetriesAreIndependentAndHonorLongerRetryAfter()
    {
        var telegramCalls = 0;
        await using var f = new Fixture(handler: (request, _) =>
        {
            var telegram = request.RequestUri!.Host == "api.telegram.org";
            if (telegram && ++telegramCalls == 1)
            {
                var response = Fixture.Response(429, "{\"ok\":false,\"error_code\":429,\"parameters\":{\"retry_after\":80}}", "application/json");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90)); return Task.FromResult(response);
            }
            return Task.FromResult(Fixture.Response(200, telegram ? "{\"ok\":true}" : "{\"code\":200}", "application/json"));
        });
        await using var service = await Prepare(f);
        var notices = (await f.Plugin.Store.ReadAsync()).Notifications;
        await Task.WhenAll(notices.Select(n => service.DeliverAsync(n.Id, default)));
        notices = (await f.Plugin.Store.ReadAsync()).Notifications;
        Assert.AreEqual("sent", notices.Single(n => n.Channel == "bark").State);
        var tg = notices.Single(n => n.Channel == "telegram"); Assert.AreEqual("retrying", tg.State);
        Assert.AreEqual(f.Clock.Now.AddSeconds(90), tg.NextAttemptAt);
        await service.DeliverAsync(tg.Id, default); Assert.AreEqual(1, telegramCalls, "截止时间之前不能投递");
        f.Clock.Advance(90); await service.DeliverAsync(tg.Id, default);
        tg = (await f.Plugin.Store.ReadAsync()).Notifications.Single(n => n.Channel == "telegram");
        Assert.AreEqual("sent", tg.State); Assert.AreEqual(2, tg.Attempts);
        var state = await File.ReadAllTextAsync(Path.Combine(f.Directory, "state.json"));
        Assert.IsFalse(state.Contains("TELEGRAM-TOKEN") || state.Contains("CHAT-ID") || state.Contains("BARK-KEY"));
    }

    [TestMethod]
    public async Task Http200WithoutAnAcknowledgementIsNotSuccess()
    {
        await using var f = new Fixture(handler: (_, _) => Task.FromResult(Fixture.Response(200, "{}", "application/json")));
        await using var service = await Prepare(f);
        foreach (var n in (await f.Plugin.Store.ReadAsync()).Notifications) await service.DeliverAsync(n.Id, default);
        Assert.IsTrue((await f.Plugin.Store.ReadAsync()).Notifications.All(n => n.State == "dead"));
    }

    [TestMethod]
    public async Task CancelledDeliveryIsPausedAndNeverReplayedByTheWorker()
    {
        await using var f = new Fixture(handler: async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Fixture.Response(200, "{}"); });
        await using var service = await Prepare(f); var notice = (await f.Plugin.Store.ReadAsync()).Notifications[0];
        using var cancel = new CancellationTokenSource(50);
        try { await service.DeliverAsync(notice.Id, cancel.Token); Assert.Fail("投递应被取消"); } catch (OperationCanceledException) { }
        notice = (await f.Plugin.Store.ReadAsync()).Notifications.Single(n => n.Id == notice.Id);
        Assert.AreEqual("paused", notice.State); Assert.AreEqual(1, notice.Attempts);
        await service.DeliverAsync(notice.Id, default);
        Assert.AreEqual(1, (await f.Plugin.Store.ReadAsync()).Notifications.Single(n => n.Id == notice.Id).Attempts);
    }

    [TestMethod]
    public async Task DedupeUsesOutageIdentityAndPendingRecordsHaveAHardLimit()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await f.Plugin.Store.UpdateAsync(d =>
        {
            d.Settings.TelegramEnabled = true; d.Settings.MaxNotificationRecords = 2;
            var task = d.Tasks[0]; task.Config.NotificationEvents = ["recovered"];
            TaskStore.Notify(d, task, f.Clock.Now, "recovered", "first");
            TaskStore.Notify(d, task, f.Clock.Now, "recovered", "same event");
            task.Runtime.OutageSequence++;
            TaskStore.Notify(d, task, f.Clock.Now, "recovered", "second");
            task.Runtime.OutageSequence++;
            TaskStore.Notify(d, task, f.Clock.Now, "recovered", "third"); return true;
        });
        var doc = await f.Plugin.Store.ReadAsync(); Assert.HasCount(2, doc.Notifications); Assert.AreEqual(1L, doc.NotificationOverflowCount);
        StringAssert.Contains(doc.NotificationWarning!, "未入队");
        Assert.AreEqual(400, (await f.Plugin.SaveSettingsAsync(Fixture.Context(new SaveSettingsInput(doc.Settings with { MaxNotificationRecords = 1 })))).StatusCode);
    }

    [TestMethod]
    public async Task FirstEntryAndOutageIdentityAreIndependentOfQueueRounds()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await f.Plugin.Store.UpdateAsync(d =>
        {
            d.Settings.TelegramEnabled = true;
            var task = d.Tasks[0]; task.Config.NotificationEvents = ["entered", "dropped", "recovered"];
            task.Runtime.OutageSequence = 1;
            foreach (var kind in task.Config.NotificationEvents) TaskStore.Notify(d, task, f.Clock.Now, kind, kind);
            task.Runtime.QueueRound++;
            foreach (var kind in task.Config.NotificationEvents) TaskStore.Notify(d, task, f.Clock.Now, kind, kind);
            task.Runtime.OutageSequence++;
            foreach (var kind in task.Config.NotificationEvents) TaskStore.Notify(d, task, f.Clock.Now, kind, kind);
            return true;
        });
        var notices = (await f.Plugin.Store.ReadAsync()).Notifications;
        Assert.HasCount(1, notices.Where(n => n.Kind == "entered").ToArray());
        Assert.HasCount(2, notices.Where(n => n.Kind == "dropped").ToArray());
        Assert.HasCount(2, notices.Where(n => n.Kind == "recovered").ToArray());
    }

    [TestMethod]
    public async Task NotificationCredentialCasDoesNotChangeOtherChannel()
    {
        await using var f = new Fixture(); var vault = new CredentialVault(f.Accounts.Object);
        await vault.SaveNotificationsAsync(new NotificationSecrets("TG", "CHAT", "BARK"), default);
        await vault.SaveNotificationsAsync(new NotificationSecrets(TelegramToken: "NEW", CredentialVersion: 1), default);
        await Assert.ThrowsExactlyAsync<ConflictException>(() => vault.SaveNotificationsAsync(new NotificationSecrets(BarkKey: "STALE", CredentialVersion: 1), default));
        var saved = await vault.NotificationAsync(default); Assert.AreEqual("BARK", saved["barkKey"]); Assert.AreEqual("CHAT", saved["telegramChatId"]);
    }
}
