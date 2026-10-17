using Matcad.Config;
using Matcad.Data;
using Matcad.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

public class PasswordHashingTests
{
    [Fact]
    public void Only_bcrypt_hashes_count_and_garbage_never_throws()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword("secret", 4);

        Assert.True(PasswordHashing.IsBcrypt(hash));
        Assert.False(PasswordHashing.IsBcrypt("plain text"));
        Assert.False(PasswordHashing.IsBcrypt(null));
        Assert.True(PasswordHashing.Verify("secret", hash));
        Assert.False(PasswordHashing.Verify("other", hash));
        Assert.False(PasswordHashing.Verify("secret", "$2a$04$broken"));
        Assert.False(PasswordHashing.Verify("secret", null));
    }

    [Fact]
    public void An_unchanged_password_keeps_its_hash_and_a_new_one_gets_a_new_hash()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword("secret", 4);

        Assert.Equal(hash, PasswordHashing.HashOrKeep("secret", hash));
        var changed = PasswordHashing.HashOrKeep("different", hash);
        Assert.NotEqual(hash, changed);
        Assert.True(PasswordHashing.Verify("different", changed));
        Assert.True(PasswordHashing.IsBcrypt(PasswordHashing.HashOrKeep("first", null)));
    }
}

public class BackupRestoreTests
{
    private static readonly string Hash = BCrypt.Net.BCrypt.HashPassword("a-real-password", 4);

    private static BackupUser User(string name, string? email = null, string role = "Admin", string? hash = null, bool mustChange = false) =>
        new() { Username = name, Email = email, Role = role, PasswordHash = hash ?? Hash, MustChangePassword = mustChange };

    [Fact]
    public void A_good_backup_comes_through_with_the_must_change_flag()
    {
        var (users, error) = BackupRestore.CheckUsers([User("admin", mustChange: true), User("bob", "bob@example.org", "User")]);

        Assert.Null(error);
        Assert.Equal([true, false], users.Select(u => u.MustChangePassword));
        Assert.Equal([UserRole.Admin, UserRole.User], users.Select(u => u.Role));
    }

    [Fact]
    public void A_default_password_in_an_old_backup_has_to_be_changed()
    {
        var defaultHash = BCrypt.Net.BCrypt.HashPassword("admin", 4);

        var (users, _) = BackupRestore.CheckUsers([User("admin", hash: defaultHash)]);

        Assert.True(Assert.Single(users).MustChangePassword);
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("$2a$04$tooShort")]
    public void A_hash_that_cannot_be_verified_is_refused_before_anything_is_replaced(string hash) =>
        Assert.Contains("not a bcrypt hash", BackupRestore.CheckUsers([User("admin", hash: hash)]).Error);

    [Fact]
    public void A_backup_without_an_administrator_is_refused() =>
        Assert.Contains("no administrator", BackupRestore.CheckUsers([User("bob", role: "User")]).Error);

    [Fact]
    public void Logins_used_twice_are_refused_but_a_user_may_use_the_same_text_as_username_and_email()
    {
        Assert.Contains("used twice", BackupRestore.CheckUsers([User("alice"), User("ALICE", role: "User")]).Error);
        Assert.Contains("used twice", BackupRestore.CheckUsers([User("alice"), User("bob", "alice", "User")]).Error);

        var (users, error) = BackupRestore.CheckUsers([User("alice@example.org", "alice@example.org")]);
        Assert.Null(error);
        Assert.Single(users);
    }

    [Fact]
    public void Settings_from_a_file_fall_back_to_safe_values()
    {
        var settings = new MatcadSettings { BaseDomain = "https://x/y", MatcadHost = " Matcad.Example.COM ", PortalMode = "embedded" };

        BackupRestore.SanitizeSettings(settings);

        Assert.Equal("", settings.BaseDomain);
        Assert.Equal("matcad.example.com", settings.MatcadHost);
        Assert.Equal("inline", settings.PortalMode);
    }
}

public sealed class RouteStoreRulesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly IConfiguration _cfg;
    private readonly ConfigStore _store;

    public RouteStoreRulesTests()
    {
        _cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Matcad:ConfigDir"] = _folder }).Build();
        _store = new ConfigStore(_cfg, NullLogger<ConfigStore>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    [Fact]
    public void A_host_belongs_to_one_route_unless_the_caller_means_make_sure_it_exists()
    {
        Assert.True(_store.TryUpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1" }, null, updateSameTarget: false));

        Assert.False(_store.TryUpsertRoute(new RouteConfig { Host = "APP.example.com", Upstream = "b:1" }, null, updateSameTarget: false));
        var again = new RouteConfig { Host = "app.example.com", Upstream = "b:2" };
        Assert.True(_store.TryUpsertRoute(again, null, updateSameTarget: true));

        var route = Assert.Single(_store.Routes);
        Assert.Equal("b:2", route.Upstream);
        Assert.Equal(route.Id, again.Id);
    }

    [Fact]
    public void A_route_with_an_id_cannot_take_over_the_host_of_another_route()
    {
        _store.UpsertRoute(new RouteConfig { Host = "a.example.com" }, null);
        _store.UpsertRoute(new RouteConfig { Host = "b.example.com" }, null);

        Assert.False(_store.TryUpsertRoute(new RouteConfig { Id = 2, Host = "a.example.com" }, null, updateSameTarget: true));
    }

    [Fact]
    public void A_port_is_unique_too_and_hosts_and_ports_are_separate_namespaces()
    {
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1" }, null);

        Assert.True(_store.TryUpsertRoute(new RouteConfig { ListenPort = 8100, Upstream = "b:1" }, null, updateSameTarget: true));
        Assert.True(_store.TryUpsertRoute(new RouteConfig { ListenPort = 8100, Upstream = "b:2" }, null, updateSameTarget: true));

        Assert.Equal(2, _store.Routes.Count);
        Assert.Equal("b:2", _store.Routes.Single(r => r.ListenPort == 8100).Upstream);
    }

    [Fact]
    public void A_caller_chosen_id_creates_the_item_with_audit_data_and_ids_continue_after_it()
    {
        _store.UpsertProvider(new ProviderConfig { Id = 40, Name = "chosen" }, userId: 3);

        var provider = Assert.Single(_store.Providers);
        Assert.Equal(40, provider.Id);
        Assert.Equal(3, provider.CreateUserId);
        Assert.NotEqual(default, provider.CreateDate);
        _store.UpsertProvider(new ProviderConfig { Name = "next" }, null);
        Assert.Equal(41, _store.Providers.Max(p => p.Id));
    }

    [Fact]
    public void A_port_binding_never_replaces_a_host_route_in_the_overview()
    {
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1" }, null);
        // Data from an older version: a port binding that also carries a host.
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", ListenPort = 8100, Upstream = "b:1" }, null);
        var provider = new RouteProvider(_store, new DockerRouteCache(), _cfg);

        var all = provider.All();

        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.Host == "app.example.com" && r.ListenPort is null && r.Upstream == "a:1");
        Assert.Contains(all, r => r.ListenPort == 8100);
    }

    [Fact]
    public void An_unknown_login_mode_serves_the_login_pages_on_the_protected_host_like_inline()
    {
        _store.UpsertAuthentication(new AuthenticationConfig { Name = "portal", Type = AuthType.Matcad }, null);
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1", AuthenticationId = _store.Authentications.Single().Id }, null);
        var settings = _store.Settings;
        settings.PortalMode = "embedded";
        _store.SaveSettings(settings);
        var generator = new CaddyConfigGenerator(_store, new RouteProvider(_store, new DockerRouteCache(), _cfg), _cfg);

        var json = System.Text.Json.JsonSerializer.Serialize(generator.Build());

        Assert.Contains("/auth/*", json);
    }
}
