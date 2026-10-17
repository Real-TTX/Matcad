using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Matcad.Config;
using Matcad.Controls;
using Matcad.Data;
using Matcad.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

/// <summary>
/// What Mattiverse needs from the REST API (Mattiverse docs/ENGINES.md, client MatcadClient.cs): app/version/apiVersion in the status,
/// <c>{"error": ...}</c> on every refusal, <c>setupCompleted</c> in the settings, and <c>managedBy</c> on routes, DNS providers and
/// authentications (missing = keep, "" = clear) with the tag in the lists.
/// </summary>
public class MattiverseApiTests : IClassFixture<MatcadFactory>
{
    private readonly MatcadFactory _factory;
    private readonly HttpClient _api;

    public MattiverseApiTests(MatcadFactory factory)
    {
        _factory = factory;
        _api = factory.CreateClient();
        _api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);
    }

    private ConfigStore Store => _factory.Services.GetRequiredService<ConfigStore>();

    private async Task<JsonObject> Post(string path, string json)
    {
        var response = await _api.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonNode.Parse(body)!.AsObject();
    }

    private async Task<Browser> SignedIn()
    {
        var auth = _factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("mv-tester") == null)
            await auth.CreateUser("mv-tester", null, "mv-tester-password", UserRole.Admin, null);
        var settings = Store.Settings;
        settings.SetupCompleted = true;
        Store.SaveSettings(settings);
        var browser = new Browser(_factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("mv-tester", "mv-tester-password")).StatusCode);
        return browser;
    }

    [Fact]
    public async Task The_status_names_the_app_its_version_and_the_api_version()
    {
        var status = (await _api.GetFromJsonAsync<JsonObject>("/api/v1/status"))!;

        Assert.True(status["ok"]!.GetValue<bool>());
        Assert.Equal("matcad", status["app"]!.GetValue<string>());
        Assert.Equal(Ui.Version, status["version"]!.GetValue<string>());
        Assert.Equal(1, status["apiVersion"]!.GetValue<int>());
        Assert.NotNull(status["capabilities"]);
        Assert.NotNull(status["counts"]);
    }

    [Fact]
    public async Task A_wrong_key_is_refused_with_an_error_member()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong");

        var response = await client.GetAsync("/api/v1/status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("Invalid or missing X-Api-Key.", body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_a_configured_key_the_api_is_off_with_an_error_member()
    {
        using var keyless = _factory.WithWebHostBuilder(b => b.UseSetting("Matcad:ApiKey", ""));
        var client = keyless.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "");

        var response = await client.GetAsync("/api/v1/status");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Contains("disabled", body["error"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_route_keeps_its_mark_until_it_is_cleared_the_way_Mattiverse_upserts()
    {
        // MatcadClient.UpsertRouteAsync: look the id up by host in /routes/manual, then POST with it.
        var created = await Post("/api/v1/routes",
            """{ "host": "app.mv.example", "wildcard": false, "target": "proxy", "upstream": "http://app:80", "enabled": true, "name": "App", "managedBy": "mattiverse" }""");
        Assert.Equal("mattiverse", created["route"]!["managedBy"]!.GetValue<string>());

        var manual = (await _api.GetFromJsonAsync<JsonArray>("/api/v1/routes/manual"))!;
        var listed = manual.Single(r => r!["host"]!.GetValue<string>() == "app.mv.example")!;
        Assert.Equal("mattiverse", listed["managedBy"]!.GetValue<string>());
        var id = listed["id"]!.GetValue<long>();
        var all = (await _api.GetFromJsonAsync<JsonArray>("/api/v1/routes"))!;
        Assert.Equal("mattiverse", all.Single(r => r!["id"]!.GetValue<long>() == id)!["managedBy"]!.GetValue<string>());

        // An update without the field (matOS, an older client) keeps the mark.
        await Post("/api/v1/routes", $$"""{ "id": {{id}}, "host": "app.mv.example", "target": "proxy", "upstream": "http://app:81", "enabled": true }""");
        Assert.Equal("mattiverse", Store.Routes.Single(r => r.Id == id).ManagedBy);
        Assert.Equal("mattiverse", (await _api.GetFromJsonAsync<JsonObject>($"/api/v1/routes/{id}"))!["managedBy"]!.GetValue<string>());

        // "" clears it.
        var cleared = await Post("/api/v1/routes", $$"""{ "id": {{id}}, "host": "app.mv.example", "target": "proxy", "upstream": "http://app:81", "enabled": true, "managedBy": "" }""");
        Assert.Null(cleared["route"]!["managedBy"]);
        Assert.Null(Store.Routes.Single(r => r.Id == id).ManagedBy);
        Assert.Single(Store.Routes, r => r.Host == "app.mv.example");
    }

    [Fact]
    public async Task The_wildcard_route_with_a_provider_is_marked_too()
    {
        var provider = await Post("/api/v1/providers",
            """{ "name": "Mattiverse DNS", "type": "netcup", "credentials": { "customer_number": "1", "api_key": "k", "api_password": "p" }, "managedBy": "mattiverse" }""");
        var providerId = provider["id"]!.GetValue<long>();
        Assert.Equal("mattiverse", provider["managedBy"]!.GetValue<string>());

        var route = await Post("/api/v1/routes", $$"""
            { "host": "*.wild.mv.example", "wildcard": true, "providerId": {{providerId}}, "target": "redirect",
              "fallbackUrl": "https://mattiverse.wild.mv.example", "redirectPermanent": false, "enabled": true,
              "name": "Mattiverse wildcard", "managedBy": "mattiverse" }
            """);

        Assert.Equal("mattiverse", route["route"]!["managedBy"]!.GetValue<string>());
        Assert.Equal(providerId, route["route"]!["providerId"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_provider_keeps_its_mark_on_an_update_by_id_and_loses_it_on_blank()
    {
        var created = await Post("/api/v1/providers",
            """{ "name": "mv-dns", "type": "netcup", "credentials": { "api_key": "a" }, "managedBy": " mattiverse " }""");
        var id = created["id"]!.GetValue<long>();
        Assert.Equal("mattiverse", created["managedBy"]!.GetValue<string>()); // trimmed

        await Post("/api/v1/providers", $$"""{ "id": {{id}}, "name": "mv-dns", "type": "netcup", "credentials": { "api_key": "b" } }""");
        var listed = (await _api.GetFromJsonAsync<JsonArray>("/api/v1/providers"))!.Single(p => p!["id"]!.GetValue<long>() == id)!;
        Assert.Equal("mattiverse", listed["managedBy"]!.GetValue<string>());

        await Post("/api/v1/providers", $$"""{ "id": {{id}}, "name": "mv-dns", "type": "netcup", "credentials": { "api_key": "b" }, "managedBy": "" }""");
        Assert.Null(Store.Providers.Single(p => p.Id == id).ManagedBy);
    }

    [Fact]
    public async Task An_authentication_carries_the_mark()
    {
        var created = await Post("/api/v1/authentications",
            """{ "name": "mv-login", "type": "BasicAuth", "users": [ { "username": "u", "password": "secret-pass" } ], "managedBy": "mattiverse" }""");
        var id = created["id"]!.GetValue<long>();
        Assert.Equal("mattiverse", created["managedBy"]!.GetValue<string>());

        await Post("/api/v1/authentications", $$"""{ "id": {{id}}, "name": "mv-login", "type": "BasicAuth", "users": [ { "username": "u" } ] }""");

        var listed = (await _api.GetFromJsonAsync<JsonArray>("/api/v1/authentications"))!.Single(a => a!["id"]!.GetValue<long>() == id)!;
        Assert.Equal("mattiverse", listed["managedBy"]!.GetValue<string>());
        Assert.Equal(["u"], listed["users"]!.AsArray().Select(u => u!.GetValue<string>()));
    }

    [Fact]
    public async Task The_settings_round_trip_of_Mattiverse_completes_the_setup()
    {
        var settings = Store.Settings;
        settings.SetupCompleted = false;
        Store.SaveSettings(settings);
        try
        {
            // MatcadClient.ConfigureAsync: GET the settings, set baseDomain and setupCompleted, PUT the whole object back.
            var body = (await _api.GetFromJsonAsync<JsonObject>("/api/v1/settings"))!;
            Assert.False(body["setupCompleted"]!.GetValue<bool>());
            body["baseDomain"] = "mv.example";
            body["setupCompleted"] = true;

            var response = await _api.PutAsJsonAsync("/api/v1/settings", body);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Assert.True(answer["setupCompleted"]!.GetValue<bool>());
            Assert.True(Store.Settings.SetupCompleted);
            Assert.Equal("mv.example", Store.Settings.BaseDomain);
            Assert.True((await _api.GetFromJsonAsync<JsonObject>("/api/v1/settings"))!["setupCompleted"]!.GetValue<bool>());
        }
        finally
        {
            var restore = Store.Settings;
            restore.SetupCompleted = true;
            Store.SaveSettings(restore);
        }
    }

    [Fact]
    public async Task The_lists_show_the_tag_and_editing_by_hand_keeps_the_mark()
    {
        var route = await Post("/api/v1/routes",
            """{ "host": "page.mv.example", "target": "proxy", "upstream": "http://page:80", "enabled": true, "name": "Page", "managedBy": "mattiverse" }""");
        var routeId = route["route"]!["id"]!.GetValue<long>();
        await Post("/api/v1/providers", """{ "name": "mv-page-dns", "type": "netcup", "credentials": { "api_key": "a" }, "managedBy": "mattiverse" }""");
        await Post("/api/v1/authentications",
            """{ "name": "mv-page-login", "type": "BasicAuth", "users": [ { "username": "u", "password": "secret-pass" } ], "managedBy": "mattiverse" }""");
        var browser = await SignedIn();

        foreach (var page in new[] { "/", "/Routes", "/Providers", "/Authentications" })
        {
            var (response, html) = await browser.GetAsync(page);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("<span class=\"mat-tag\" title=\"Created by Mattiverse. Manual changes may be overwritten.\">Managed by Mattiverse</span>", html);
        }

        var saved = await browser.PostFormAsync($"/Routes/Edit?id={routeId}", $"/Routes/Edit?id={routeId}", new()
        {
            ["Id"] = routeId.ToString(), ["Name"] = "Page by hand", ["Host"] = "page.mv.example", ["RouteType"] = "single",
            ["Target"] = "proxy", ["Upstream"] = "http://page:81", ["Enabled"] = "true"
        });
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        var stored = Store.Routes.Single(r => r.Id == routeId);
        Assert.Equal("Page by hand", stored.Name);
        Assert.Equal("mattiverse", stored.ManagedBy);
    }

    [Fact]
    public void The_tag_is_empty_without_a_mark_and_names_other_tools_as_they_are()
    {
        Assert.Equal("", Ui.ManagedBy(null).ToString());
        Assert.Equal("", Ui.ManagedBy("  ").ToString());
        Assert.Contains(">Managed by Mattiverse<", Ui.ManagedBy("MATTIVERSE").ToString());
        Assert.Contains(">Managed by &lt;tool&gt;<", Ui.ManagedBy("<tool>").ToString());
    }
}

/// <summary>A headless install: <c>MATCAD_ADMIN_PASSWORD</c> seeds the admin with that password and the wizard counts as done.</summary>
public class HeadlessInstallTests
{
    private sealed class HeadlessFactory(string? adminPassword) : WebApplicationFactory<Program>
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting(WebHostDefaults.EnvironmentKey, "Production");
            builder.UseSetting("Matcad:DataDir", _folder);
            builder.UseSetting("Matcad:ConfigDir", Path.Combine(_folder, "config"));
            builder.UseSetting("Matcad:CaddyLogDir", Path.Combine(_folder, "caddy-logs"));
            builder.UseSetting("Matcad:Caddy:AdminUrl", "http://127.0.0.1:1");
            builder.UseSetting("MATCAD_ADMIN_PASSWORD", adminPassword);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ }
        }
    }

    [Fact]
    public async Task The_admin_signs_in_with_the_given_password_and_goes_straight_to_the_app()
    {
        using var factory = new HeadlessFactory("headless-secret-42");
        var browser = new Browser(factory);

        Assert.NotEqual(HttpStatusCode.Redirect, (await browser.LoginAsync("admin", "admin")).StatusCode); // admin/admin does not work
        var login = await browser.LoginAsync("admin", "headless-secret-42");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var (home, _) = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode); // no forced password change, no setup wizard
        Assert.True(factory.Services.GetRequiredService<ConfigStore>().Settings.SetupCompleted);
    }

    [Fact]
    public async Task A_password_that_breaks_the_rules_falls_back_to_the_default_admin()
    {
        using var factory = new HeadlessFactory("short");
        var browser = new Browser(factory);

        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("admin", "admin")).StatusCode);
        var (next, _) = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, next.StatusCode);
        Assert.StartsWith("/Setup", next.Headers.Location!.OriginalString); // the wizard asks for a new password first
        Assert.False(factory.Services.GetRequiredService<ConfigStore>().Settings.SetupCompleted);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("admin", false)]
    [InlineData("seven77", false)]
    [InlineData("eight888", true)]
    public async Task Seeding_takes_the_password_only_when_it_passes_the_password_rules(string? password, bool taken)
    {
        using var database = new TempDatabase();
        await using var db = database.CreateContext();
        await DatabaseBootstrapper.InitializeAsync(db, NullLogger.Instance);
        var auth = new AuthService(db);

        Assert.Equal(taken, await auth.EnsureSeedAdmin(NullLogger.Instance, password));

        var admin = await db.Users.SingleAsync();
        Assert.Equal(!taken, admin.MustChangePassword);
        Assert.NotNull(await auth.ValidateCredentials("admin", taken ? password! : "admin"));
        Assert.False(await auth.EnsureSeedAdmin(NullLogger.Instance, "another-password")); // only on an empty database
    }
}
