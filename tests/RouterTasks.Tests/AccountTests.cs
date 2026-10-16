using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Plugins.RouterTasks;
using Router.Contracts.Domain;

namespace RouterTasks.Tests;

[TestClass]
public sealed class AccountTests
{
    private static SaveAccountInput Draft(string id, string token = "TOKEN-account", PluginSettings? settings = null, NotificationSecrets? secrets = null)
        => new(new TaskConfig { Id = id, Name = id, Model = "MODEL", Settings = settings ?? new(),
            RequestOverride = new RequestTemplate { Id = "custom", Headers = new() { ["Authorization"] = "Bearer {{TOKEN}}" },
                Body = JsonNode.Parse("{\"model\":\"{{MODEL}}\",\"input\":\"{{PROMPT}}\",\"stream\":true,\"large\":9007199254740993123456789}")!.AsObject() } },
            new SiteConfig { Id = "draft-site", BaseUrl = "http://127.0.0.1", Name = id }, token, Secrets: secrets ?? new(CredentialVersion: 0));

    private static async Task<AccountDetail> Save(Fixture f, SaveAccountInput input)
    {
        var result = await f.Plugin.SaveAccountAsync(Fixture.Context(input));
        Assert.AreEqual(200, result.StatusCode, JsonSerializer.Serialize(result.Body));
        return JsonData.Read<AccountDetail>(result.Body);
    }

    private static SaveAccountInput Edit(AccountDetail account) => new(account.Config, account.Site, account.Token,
        account.CredentialVersion, account.ConfigRevision, account.Secrets);

    [TestMethod]
    public async Task NewAccountSavesConnectionExecutionAndPrivateSettingsTogether()
    {
        await using var f = new Fixture();
        await f.Plugin.Store.UpdateAsync(d => { d.Settings.TelegramEnabled = true; return true; });
        var saved = await Save(f, Draft("account-a"));
        var document = await f.Plugin.Store.ReadAsync();
        Assert.HasCount(1, document.Tasks); Assert.HasCount(1, document.Sites); Assert.HasCount(1, document.Keys);
        Assert.AreEqual("paused", document.Tasks[0].Runtime.Status);
        Assert.AreEqual(saved.Site.Id, saved.Config.SiteId); Assert.AreEqual(saved.Config.KeyId, document.Keys[0].Id);
        Assert.IsFalse(saved.Config.Settings!.TelegramEnabled);
        Assert.IsTrue(saved.Config.Claude1MContext);
        var snapshot = await f.Plugin.ResolveAsync(saved.Config, default);
        Assert.IsFalse(snapshot.Settings.TelegramEnabled);
        Assert.AreEqual("TOKEN-account", saved.Token);
        Assert.IsFalse((await File.ReadAllTextAsync(Path.Combine(f.Directory, "state.json"))).Contains("TOKEN-account"));
    }

    [TestMethod]
    public async Task NewKeysUseAHostCodecSafeCustomField()
    {
        await using var f = new Fixture();
        var saved = await Save(f, Draft("storage-safe"));
        var credential = (CustomCredential)f.AccountData[saved.Config.KeyId].Credential;
        Assert.AreEqual("TOKEN-account", credential.Fields[CredentialVault.ApiTokenField]);
        Assert.IsFalse(credential.Fields.ContainsKey("token"));
        Assert.AreEqual(saved.Site.Id, credential.Fields["siteId"]);
        Assert.AreEqual("key", credential.Fields["type"]);
        var updated = await Save(f, Edit(saved) with { Token = "TOKEN-new" });
        Assert.AreEqual("TOKEN-new", (await f.Plugin.ResolveAsync(updated.Config, default)).ApiKey);
    }

    [TestMethod]
    public async Task LegacyCustomTokenIsReadableAndMigratesOnSave()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        var vault = new CredentialVault(f.Accounts.Object);
        Assert.AreEqual("TOKEN-secret-test-only", (await vault.KeyDetailAsync("key", "site", default)).Token);
        await vault.SaveKeyAsync(new SaveKeyInput("key", "site", "Legacy", null, 1), default);
        var credential = (CustomCredential)f.AccountData["key"].Credential;
        Assert.IsFalse(credential.Fields.ContainsKey("token"));
        Assert.AreEqual("TOKEN-secret-test-only", credential.Fields[CredentialVault.ApiTokenField]);
        Assert.AreEqual("TOKEN-secret-test-only", await vault.KeyAsync("key", "site", default));
    }

    [TestMethod]
    public async Task ClaudeContextToggleIsSavedPerAccountAndExported()
    {
        await using var f = new Fixture();
        var enabled = await Save(f, Draft("context-on"));
        var draft = Draft("context-off");
        var disabled = await Save(f, draft with { Config = draft.Config with { Claude1MContext = false } });
        Assert.IsTrue(enabled.Config.Claude1MContext); Assert.IsFalse(disabled.Config.Claude1MContext);
        Assert.IsFalse(JsonData.Read<AccountDetail>((await f.Plugin.AccountDetailAsync(Fixture.Context(id: "context-off"))).Body).Config.Claude1MContext);
        var exported = (await f.Plugin.ExportAccountAsync(Fixture.Context(id: "context-off"))).Body;
        var json = JsonSerializer.SerializeToNode(exported, JsonData.Options)!;
        Assert.IsFalse(json["account"]!["config"]!["claude1MContext"]!.GetValue<bool>());
        Assert.IsTrue((await f.Plugin.ResolveAsync(enabled.Config, default)).Config.Claude1MContext);
    }

    [TestMethod]
    public async Task DraftPreviewContainsExactCredentialsAndDoesNotSaveOrSend()
    {
        await using var f = new Fixture();
        var result = await f.Plugin.PreviewAccountAsync(Fixture.Context(Draft("draft", "TOKEN-preview")));
        Assert.AreEqual(200, result.StatusCode);
        var value = JsonSerializer.Serialize(result.Body);
        StringAssert.Contains(value, "Bearer TOKEN-preview"); StringAssert.Contains(value, "9007199254740993123456789");
        Assert.HasCount(0, (await f.Plugin.Store.ReadAsync()).Tasks);
        Assert.HasCount(1, f.AccountData);
    }

    [TestMethod]
    public async Task DraftQueriesModelsAndQuotaBeforeAnAccountIsSaved()
    {
        var requests = new List<string>();
        await using var f = new Fixture(handler: (request, _) =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            Assert.AreEqual("Bearer TOKEN-query", request.Headers.Authorization!.ToString());
            var body = request.RequestUri.AbsolutePath.EndsWith("models", StringComparison.Ordinal) ? "{\"data\":[{\"id\":\"MODEL-A\"}]}" :
                request.RequestUri.AbsolutePath.EndsWith("usage", StringComparison.Ordinal) ? "{\"total_usage\":150}" : "{\"hard_limit_usd\":10}";
            return Task.FromResult(Fixture.Response(200, body, "application/json"));
        });
        var result = await f.Plugin.QueryAccountAsync(Fixture.Context(new AccountConnectionInput(new SiteConfig { BaseUrl = "http://127.0.0.1" }, "TOKEN-query")));
        Assert.AreEqual(200, result.StatusCode); var key = JsonData.Read<KeyInfo>(result.Body);
        CollectionAssert.AreEqual(new[] { "MODEL-A" }, key.Models); Assert.AreEqual(1.5m, key.UsedUsd); Assert.AreEqual(10m, key.LimitUsd);
        Assert.HasCount(3, requests); Assert.HasCount(0, (await f.Plugin.Store.ReadAsync()).Tasks); Assert.HasCount(1, f.AccountData);
    }

    [TestMethod]
    public async Task AccountRecipientsAndChannelsAreIndependent()
    {
        var requests = new ConcurrentQueue<string>();
        await using var f = new Fixture(handler: async (request, token) =>
        {
            requests.Enqueue(request.RequestUri + " " + (request.Content is null ? "" : await request.Content.ReadAsStringAsync(token)));
            return Fixture.Response(200, request.RequestUri!.Host == "api.telegram.org" ? "{\"ok\":true}" : "{\"code\":200}", "application/json");
        });
        await Save(f, Draft("account-a", settings: new() { TelegramEnabled = true }, secrets: new("BOT-A", "CHAT-A", CredentialVersion: 0)));
        await Save(f, Draft("account-b", settings: new() { TelegramEnabled = true }, secrets: new("BOT-B", "CHAT-B", CredentialVersion: 0)));
        await Save(f, Draft("account-c", settings: new() { BarkEnabled = true }, secrets: new(BarkKey: "BARK-C", CredentialVersion: 0)));
        await Save(f, Draft("account-d"));
        await f.Plugin.Store.UpdateAsync(d => { foreach (var task in d.Tasks) TaskStore.Notify(d, task, f.Clock.Now, "entered", task.Config.Id); return true; });
        var notices = (await f.Plugin.Store.ReadAsync()).Notifications;
        Assert.HasCount(3, notices); Assert.IsFalse(notices.Any(n => n.TaskId == "account-d"));
        await using var service = new NotificationService(f.Plugin.Store, new CredentialVault(f.Accounts.Object), f.Http.Object, f.Clock);
        foreach (var item in notices) await service.DeliverAsync(item.Id, default);
        Assert.HasCount(3, requests);
        Assert.IsTrue(requests.Any(r => r.Contains("botBOT-A/") && r.Contains("CHAT-A") && !r.Contains("CHAT-B")));
        Assert.IsTrue(requests.Any(r => r.Contains("botBOT-B/") && r.Contains("CHAT-B") && !r.Contains("CHAT-A")));
        Assert.IsTrue(requests.Any(r => r.Contains("/BARK-C/")));
        Assert.IsTrue((await f.Plugin.Store.ReadAsync()).Notifications.All(n => n.State == "sent"));
    }

    [TestMethod]
    public async Task EditingSharedLegacyConnectionDoesNotModifyOtherAccount()
    {
        await using var f = new Fixture(); await f.SeedAsync();
        await f.Plugin.Store.UpdateAsync(d => { d.Tasks.Add(new TaskRecord { Config = f.Config with { Id = "other" } }); return true; });
        var view = JsonData.Read<AccountDetail>((await f.Plugin.AccountDetailAsync(Fixture.Context(id: "task"))).Body);
        var saved = await Save(f, Edit(view) with { Token = "TOKEN-new", Site = view.Site with { BaseUrl = "http://127.0.0.2" } });
        var document = await f.Plugin.Store.ReadAsync();
        var other = TaskStore.Find(document, "other");
        Assert.AreEqual("site", other.Config.SiteId); Assert.AreEqual("key", other.Config.KeyId);
        Assert.AreNotEqual("site", saved.Config.SiteId); Assert.AreNotEqual("key", saved.Config.KeyId);
        Assert.AreEqual("TOKEN-secret-test-only", (await f.Plugin.ResolveAsync(other.Config, default)).ApiKey);
        Assert.AreEqual("http://127.0.0.1", document.Sites.Single(s => s.Id == "site").BaseUrl);
        Assert.IsNull(other.Config.Settings);
    }

    [TestMethod]
    public async Task AccountRevisionsAndCredentialRevisionsRejectStaleEdits()
    {
        await using var f = new Fixture(); var first = await Save(f, Draft("account"));
        var changed = await Save(f, Edit(first) with { Token = "TOKEN-updated", Secrets = first.Secrets with { TelegramToken = "BOT" } });
        Assert.AreEqual(2, changed.ConfigRevision);
        Assert.AreEqual(409, (await f.Plugin.SaveAccountAsync(Fixture.Context(Edit(first)))).StatusCode);
        Assert.AreEqual(409, (await f.Plugin.SaveAccountAsync(Fixture.Context(Edit(changed) with { CredentialVersion = first.CredentialVersion }))).StatusCode);
        Assert.AreEqual(409, (await f.Plugin.SaveAccountAsync(Fixture.Context(Edit(changed) with { Secrets = first.Secrets }))).StatusCode);
        Assert.AreEqual("TOKEN-updated", JsonData.Read<AccountDetail>((await f.Plugin.AccountDetailAsync(Fixture.Context(id: "account"))).Body).Token);
    }

    [TestMethod]
    public async Task LegacyPendingNotificationsUseTheEditedAccountRecipient()
    {
        var requests = new List<string>();
        await using var f = new Fixture(handler: async (request, token) =>
        {
            requests.Add(request.RequestUri + " " + await request.Content!.ReadAsStringAsync(token));
            return Fixture.Response(200, "{\"ok\":true}", "application/json");
        });
        await f.SeedAsync(); var vault = new CredentialVault(f.Accounts.Object);
        await vault.SaveNotificationsAsync(new NotificationSecrets("GLOBAL-BOT", "GLOBAL-CHAT"), default);
        await f.Plugin.Store.UpdateAsync(d => { d.Settings.TelegramEnabled = true; TaskStore.Notify(d, d.Tasks[0], f.Clock.Now, "entered", "accepted"); return true; });
        var detail = JsonData.Read<AccountDetail>((await f.Plugin.AccountDetailAsync(Fixture.Context(id: "task"))).Body);
        await Save(f, Edit(detail) with { Secrets = new NotificationSecrets("PRIVATE-BOT", "PRIVATE-CHAT", CredentialVersion: 0) });
        var notice = (await f.Plugin.Store.ReadAsync()).Notifications.Single();
        Assert.AreEqual(CredentialVault.AccountProfile("task"), notice.ProfileId);
        await using var service = new NotificationService(f.Plugin.Store, vault, f.Http.Object, f.Clock);
        await service.DeliverAsync(notice.Id, default);
        StringAssert.Contains(requests.Single(), "PRIVATE-CHAT"); Assert.IsFalse(requests.Single().Contains("GLOBAL-CHAT"));
        Assert.AreEqual("GLOBAL-CHAT", (await vault.NotificationAsync(default))["telegramChatId"]);
    }

    [TestMethod]
    public async Task EditingRunningAccountIsRejectedBeforeCredentialChanges()
    {
        var probe = new ScriptProbe((_, _, token) => ScriptProbe.Block(token));
        await using var f = new Fixture(probe); var account = await Save(f, Draft("active"));
        await f.Plugin.Engine.ActionAsync("active", "start");
        await Fixture.Until(() => Task.FromResult(probe.Calls == 1));
        Assert.AreEqual(409, (await f.Plugin.SaveAccountAsync(Fixture.Context(Edit(account) with { Token = "CHANGED" }))).StatusCode);
        var current = JsonData.Read<AccountDetail>((await f.Plugin.AccountDetailAsync(Fixture.Context(id: "active"))).Body);
        Assert.AreEqual(account.Token, current.Token); Assert.AreEqual(account.CredentialVersion, current.CredentialVersion);
    }

    [TestMethod]
    public async Task InvalidAccountInputDoesNotCreatePartialCredentials()
    {
        await using var f = new Fixture();
        var initial = f.AccountData.Count;
        var missingRecipient = Draft("account", settings: new() { TelegramEnabled = true });
        Assert.AreEqual(400, (await f.Plugin.SaveAccountAsync(Fixture.Context(missingRecipient))).StatusCode);
        Assert.AreEqual(400, (await f.Plugin.SaveAccountAsync(Fixture.Context(Draft("account") with { Token = "" }))).StatusCode);
        Assert.AreEqual(400, (await f.Plugin.SaveAccountAsync(Fixture.Context(Draft("account") with { Site = null! }))).StatusCode);
        Assert.AreEqual(initial, f.AccountData.Count); Assert.HasCount(0, (await f.Plugin.Store.ReadAsync()).Tasks);
    }

    [TestMethod]
    public async Task RetentionQueueLimitsAndDailyStatsAreScopedToAccount()
    {
        await using var f = new Fixture();
        await Save(f, Draft("a", settings: new() { TelegramEnabled = true, MaxEventsPerTask = 1, MaxNotificationRecords = 1 }, secrets: new("A", "CHAT-A", CredentialVersion: 0)));
        await Save(f, Draft("b", settings: new() { TelegramEnabled = true, MaxEventsPerTask = 4, MaxNotificationRecords = 3 }, secrets: new("B", "CHAT-B", CredentialVersion: 0)));
        await f.Plugin.Store.UpdateAsync(d =>
        {
            foreach (var task in d.Tasks)
            {
                for (var i = 0; i < 3; i++)
                {
                    TaskStore.AddEvent(d, task, f.Clock.Now, "test", i.ToString());
                    // 每次模拟独立运行，避免首次进入通知按 RunId 去重影响保留上限测试。
                    task.Runtime.RunId = $"run-{task.Config.Id}-{i}";
                    TaskStore.Notify(d, task, f.Clock.Now, "entered", "accepted");
                }
                TaskStore.Count(d, f.Clock.Now, "responses", "entered", task.Config.Id);
            }
            return true;
        });
        var document = await f.Plugin.Store.ReadAsync();
        Assert.HasCount(1, TaskStore.Find(document, "a").Events); Assert.HasCount(3, TaskStore.Find(document, "b").Events);
        Assert.AreEqual(1, document.Notifications.Count(n => n.TaskId == "a")); Assert.AreEqual(3, document.Notifications.Count(n => n.TaskId == "b"));
        var detail = JsonSerializer.SerializeToNode((await f.Plugin.TaskDetailAsync(Fixture.Context(id: "a"))).Body, JsonData.Options)!;
        Assert.HasCount(1, detail["stats"]!.AsArray()); Assert.AreEqual("a", detail["stats"]![0]!["taskId"]!.GetValue<string>());
        Assert.HasCount(1, detail["notifications"]!.AsArray());
    }

    [TestMethod]
    public async Task ExportIncludesAccountCredentialsAndDeletionKeepsOtherAccounts()
    {
        await using var f = new Fixture();
        var account = await Save(f, Draft("a", "TOKEN-A", secrets: new("BOT-A", "CHAT-A", CredentialVersion: 0)));
        var other = await Save(f, Draft("b", "TOKEN-B"));
        var export = await f.Plugin.ExportAccountAsync(Fixture.Context(id: "a"));
        var text = JsonSerializer.Serialize(export.Body);
        StringAssert.Contains(text, "TOKEN-A"); StringAssert.Contains(text, "BOT-A"); StringAssert.Contains(text, "9007199254740993123456789");
        Assert.IsFalse(text.Contains("TOKEN-B"));
        Assert.AreEqual(200, (await f.Plugin.DeleteAccountAsync(Fixture.Context(new IdInput("a")))).StatusCode);
        var document = await f.Plugin.Store.ReadAsync(); Assert.HasCount(1, document.Tasks); Assert.AreEqual("b", document.Tasks[0].Config.Id);
        Assert.IsFalse(f.AccountData.ContainsKey(account.Config.KeyId)); Assert.IsTrue(f.AccountData.ContainsKey(other.Config.KeyId));
        Assert.IsFalse(f.AccountData.ContainsKey(CredentialVault.AccountProfile("a")));
    }
}
