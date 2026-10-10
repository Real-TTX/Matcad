using Matcad.Config;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

public sealed class ConfigStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private ConfigStore Open() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Matcad:ConfigDir"] = _folder }).Build(),
        NullLogger<ConfigStore>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    [Fact]
    public void New_items_get_ids_and_audit_fields_and_survive_a_restart()
    {
        var store = Open();

        store.UpsertRoute(new RouteConfig { Host = "a.example.com" }, userId: 5);
        store.UpsertRoute(new RouteConfig { Host = "b.example.com" }, userId: 5);

        var reopened = Open();
        Assert.Equal([1, 2], reopened.Routes.Select(r => r.Id));
        Assert.Equal(5, reopened.Routes[0].CreateUserId);
        Assert.NotEqual(default, reopened.Routes[0].CreateDate);
    }

    [Fact]
    public void An_update_replaces_the_item_and_keeps_who_created_it()
    {
        var store = Open();
        store.UpsertRoute(new RouteConfig { Host = "a.example.com" }, userId: 5);

        store.UpsertRoute(new RouteConfig { Id = 1, Host = "changed.example.com" }, userId: 7);

        var route = Assert.Single(store.Routes);
        Assert.Equal("changed.example.com", route.Host);
        Assert.Equal(5, route.CreateUserId);
        Assert.Equal(7, route.UpdateUserId);
    }

    [Fact]
    public void Ids_are_never_reused_after_a_delete()
    {
        var store = Open();
        store.UpsertProvider(new ProviderConfig { Name = "one" }, null);
        store.UpsertProvider(new ProviderConfig { Name = "two" }, null);

        store.DeleteProvider(2);
        store.UpsertProvider(new ProviderConfig { Name = "three" }, null);

        Assert.Equal([1, 3], Open().Providers.Select(p => p.Id));
    }

    [Fact]
    public void A_list_that_was_read_does_not_change_under_the_reader()
    {
        var store = Open();
        store.UpsertRoute(new RouteConfig { Host = "a.example.com" }, null);
        var snapshot = store.Routes;

        store.UpsertRoute(new RouteConfig { Host = "b.example.com" }, null);
        store.DeleteRoute(1);

        Assert.Equal(["a.example.com"], snapshot.Select(r => r.Host));
        Assert.Equal(["b.example.com"], store.Routes.Select(r => r.Host));
    }

    [Fact]
    public void Import_replaces_everything_including_the_id_sequences()
    {
        var store = Open();
        store.UpsertRoute(new RouteConfig { Host = "old.example.com" }, null);

        store.ImportConfig(new MatcadSettings { BaseDomain = "restored.org" }, [], [new RouteConfig { Id = 9, Host = "new.example.com" }], [], new Dictionary<string, long> { ["routes"] = 40 });

        var reopened = Open();
        Assert.Equal("restored.org", reopened.Settings.BaseDomain);
        Assert.Equal(["new.example.com"], reopened.Routes.Select(r => r.Host));
        reopened.UpsertRoute(new RouteConfig { Host = "next.example.com" }, null);
        Assert.Equal(41, reopened.Routes.Max(r => r.Id));
    }

    [Fact]
    public void A_broken_file_does_not_stop_the_app_from_starting()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "routes.json"), "{ this is not json");

        var store = Open();

        Assert.Empty(store.Routes);
    }
}
