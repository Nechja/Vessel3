using Vessel3.Primitives;
using Vessel3.Storage;
using Xunit;

namespace Vessel3.Tests;

public sealed class WebhookStoreTests : IDisposable
{
    private readonly string testDir;

    public WebhookStoreTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), $"vessel3-wh-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(testDir))
            {
                Directory.Delete(testDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void CreateAndListWebhooks_PersistsCorrectly()
    {
        var options = new WebhookStoreOptions(testDir);
        using (var store = new SqliteWebhookStore(options))
        {
            var req = new CreateWebhookRequest(
                "drummer-notifications",
                "https://events.drummer.internal/vessel",
                "secret-key-drummer-123",
                ["container.image.pushed", "container.image.deleted"],
                ["drummer:*"],
                Active: true);

            var createRes = store.CreateWebhook(req);
            Assert.True(createRes.TryGetValue(out var hook, out _));
            Assert.Equal("drummer-notifications", hook.Name);
            Assert.Equal("https://events.drummer.internal/vessel", hook.Url);
            Assert.Equal("secret-key-drummer-123", hook.Secret);
            Assert.Equal(2, hook.EventFilters.Count);
            Assert.NotNull(hook.ResourceFilters);
            Assert.Single(hook.ResourceFilters);
            Assert.Equal("drummer:*", hook.ResourceFilters[0]);
            Assert.True(hook.Active);
            Assert.False(hook.IsStatic);
        }

        // Reopen to verify SQLite persistence
        using (var store = new SqliteWebhookStore(options))
        {
            var listRes = store.ListWebhooks();
            Assert.True(listRes.TryGetValue(out var list, out _));
            Assert.Single(list);
            Assert.Equal("drummer-notifications", list[0].Name);
            Assert.Equal("secret-key-drummer-123", list[0].Secret);
        }
    }

    [Fact]
    public void UpdateWebhook_ModifiesProperties()
    {
        var options = new WebhookStoreOptions(testDir);
        using var store = new SqliteWebhookStore(options);

        var createRes = store.CreateWebhook(new CreateWebhookRequest(
            "rainier-events",
            "https://rainier.example.com/wh",
            null,
            ["container.image.pushed"]));
        Assert.True(createRes.TryGetValue(out var created, out _));

        var updateReq = new UpdateWebhookRequest(
            "rainier-events-updated",
            "https://rainier.example.com/v2/wh",
            "new-secret-rainier",
            ["container.*"],
            ["rainier/*"],
            Active: false);

        var updatedRes = store.UpdateWebhook(created.Id, updateReq);
        Assert.True(updatedRes.TryGetValue(out var updated, out _));
        Assert.Equal("rainier-events-updated", updated.Name);
        Assert.Equal("https://rainier.example.com/v2/wh", updated.Url);
        Assert.Equal("new-secret-rainier", updated.Secret);
        Assert.False(updated.Active);
        Assert.Equal("container.*", updated.EventFilters[0]);
    }

    [Fact]
    public void DeleteWebhook_RemovesFromDatabase()
    {
        var options = new WebhookStoreOptions(testDir);
        using var store = new SqliteWebhookStore(options);

        var createRes = store.CreateWebhook(new CreateWebhookRequest(
            "avasarala-alerts",
            "https://avasarala.internal/webhook",
            null,
            ["*"]));
        Assert.True(createRes.TryGetValue(out var created, out _));

        var delRes = store.DeleteWebhook(created.Id);
        Assert.False(delRes.TryGetError(out _));

        var getRes = store.GetWebhook(created.Id);
        Assert.True(getRes.TryGetValue(out var deletedHook, out _));
        Assert.Null(deletedHook);
    }

    [Fact]
    public void StaticWebhook_CannotBeModifiedOrDeleted()
    {
        var options = new WebhookStoreOptions(testDir);
        using var store = new SqliteWebhookStore(options);

        var staticHook = new Webhook(
            "whk_static_bobby",
            "bobby-static",
            "https://bobby.internal/hook",
            null,
            ["container.image.pushed"],
            null,
            Active: true,
            DateTimeOffset.UtcNow,
            null,
            null,
            null,
            IsStatic: true);

        store.UpsertStaticWebhook(staticHook);

        var updateRes = store.UpdateWebhook(staticHook.Id, new UpdateWebhookRequest(
            "bobby-changed", "https://bobby.internal/hook", null, ["*"]));
        Assert.False(updateRes.TryGetValue(out _, out _));

        var deleteRes = store.DeleteWebhook(staticHook.Id);
        Assert.True(deleteRes.TryGetError(out _));
    }

    [Fact]
    public void YamlLoader_ParsesAndExportsCorrectly()
    {
        var yamlContent = """
            # Vessel3 declarative webhooks
            webhooks:
              - id: drummer-ci
                name: "Drummer CI Webhook"
                url: "https://ci.drummer.internal/vessel"
                secret: "drummer-sec-456"
                active: true
                events:
                  - container.image.pushed
                  - container.image.deleted
                resources:
                  - drummer:*
                  - naomi:*
              - id: rainier-backup
                name: "Rainier Storage Backup"
                url: "https://backup.rainier.internal/events"
                active: false
                events:
                  - storage.object.created
            """;

        var hooksRes = YamlWebhookLoader.LoadFromYaml(yamlContent);
        Assert.True(hooksRes.TryGetValue(out var hooks, out _));
        Assert.Equal(2, hooks.Count);

        var h1 = hooks[0];
        Assert.Equal("drummer-ci", h1.Id);
        Assert.Equal("Drummer CI Webhook", h1.Name);
        Assert.Equal("https://ci.drummer.internal/vessel", h1.Url);
        Assert.Equal("drummer-sec-456", h1.Secret);
        Assert.True(h1.Active);
        Assert.True(h1.IsStatic);
        Assert.Equal(2, h1.EventFilters.Count);
        Assert.Equal("container.image.pushed", h1.EventFilters[0]);
        Assert.NotNull(h1.ResourceFilters);
        Assert.Equal(2, h1.ResourceFilters.Count);
        Assert.Equal("drummer:*", h1.ResourceFilters[0]);

        var h2 = hooks[1];
        Assert.Equal("rainier-backup", h2.Id);
        Assert.False(h2.Active);
        Assert.Null(h2.Secret);

        var exported = YamlWebhookLoader.ExportToYaml(hooks);
        Assert.Contains("id: drummer-ci", exported, StringComparison.Ordinal);
        Assert.Contains("name: \"Drummer CI Webhook\"", exported, StringComparison.Ordinal);
        Assert.Contains("url: \"https://ci.drummer.internal/vessel\"", exported, StringComparison.Ordinal);
        Assert.Contains("id: rainier-backup", exported, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("container.image.pushed", "container.image.pushed", true)]
    [InlineData("container.*", "container.image.pushed", true)]
    [InlineData("container.*", "container.image.deleted", true)]
    [InlineData("container.*", "storage.object.created", false)]
    [InlineData("*", "anything.at.all", true)]
    public void MatchesFilter_ValidatesProperly(string filter, string eventType, bool expected)
    {
        var matches = WebhookDeliveryWorker.MatchesFilter([filter], eventType);
        Assert.Equal(expected, matches);
    }

    [Theory]
    [InlineData("drummer:*", "drummer:latest", true)]
    [InlineData("drummer:*", "drummer:v1.0.0", true)]
    [InlineData("drummer:*", "naomi:latest", false)]
    [InlineData("rainier/*", "rainier/photo.jpg", true)]
    [InlineData("rainier/*", "hood/photo.jpg", false)]
    [InlineData("*", "any:resource", true)]
    public void MatchesResource_ValidatesProperly(string filter, string resource, bool expected)
    {
        var matches = WebhookDeliveryWorker.MatchesResource([filter], resource);
        Assert.Equal(expected, matches);
    }
}
