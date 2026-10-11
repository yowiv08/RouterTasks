using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugins.RouterTasks;

namespace RouterTasks.Tests;

[TestClass]
public sealed class NotificationTests
{
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
