using System.Net;
using System.Text.Json.Nodes;
using Matcad.Config;
using Matcad.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

/// <summary>What Caddy answers for a host that no route has: Caddy's own default, a 404 page, a redirect or a closed connection.</summary>
public sealed class UnknownHostTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConfigStore _store;
    private readonly CaddyService _caddy;

    public UnknownHostTests()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Matcad:ConfigDir"] = _folder }).Build();
        _store = new ConfigStore(cfg, NullLogger<ConfigStore>.Instance);
        var generator = new CaddyConfigGenerator(_store, new RouteProvider(_store, new DockerRouteCache(), cfg), cfg);
        _caddy = new CaddyService(new NoHttp(), generator, _store, cfg, NullLogger<CaddyService>.Instance);
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:80" }, null);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private void Configure(string mode, string redirect = "", string raw = "")
    {
        var settings = _store.Settings;
        settings.UnknownHost = mode;
        settings.UnknownHostRedirect = redirect;
        settings.RawCaddyJson = raw;
        _store.SaveSettings(settings);
    }

    private JsonArray MainRoutes() => JsonNode.Parse(_caddy.BuildJson())!["apps"]!["http"]!["servers"]!["matcad"]!["routes"]!.AsArray();

    [Fact]
    public void By_default_nothing_is_added()
    {
        Configure("");

        Assert.All(MainRoutes(), r => Assert.NotNull(r!["match"]));
    }

    [Fact]
    public void Not_found_answers_a_small_page_with_404_as_the_last_route()
    {
        Configure("notfound");

        var last = MainRoutes().Last()!;

        Assert.Null(last["match"]);
        var handler = last["handle"]![0]!;
        Assert.Equal(404, handler["status_code"]!.GetValue<int>());
        Assert.Equal("text/html; charset=utf-8", handler["headers"]!["Content-Type"]![0]!.GetValue<string>());
        var body = handler["body"]!.GetValue<string>();
        Assert.Contains("Not found", body);
        Assert.DoesNotContain("{", body); // Caddy would fill a placeholder into the body
    }

    [Fact]
    public void A_redirect_answers_302_to_the_target()
    {
        Configure("redirect", "https://www.example.com/");

        var handler = MainRoutes().Last()!["handle"]![0]!;

        Assert.Equal(302, handler["status_code"]!.GetValue<int>());
        Assert.Equal("https://www.example.com/", handler["headers"]!["Location"]![0]!.GetValue<string>());
    }

    [Theory]
    [InlineData("https://x.example.com/{env.SECRET}")]
    [InlineData("javascript:alert(1)")]
    [InlineData("")]
    public void A_redirect_target_that_is_not_valid_falls_back_to_the_page(string target)
    {
        Configure("redirect", target);

        var handler = MainRoutes().Last()!["handle"]![0]!;

        Assert.Equal(404, handler["status_code"]!.GetValue<int>());
        Assert.DoesNotContain("env.SECRET", MainRoutes().ToJsonString());
    }

    [Fact]
    public void Drop_closes_the_connection()
    {
        Configure("drop");

        Assert.True(MainRoutes().Last()!["handle"]![0]!["abort"]!.GetValue<bool>());
    }

    [Fact]
    public void The_answer_stays_behind_the_routes_of_the_extra_json()
    {
        Configure("notfound", raw: """{ "apps": { "http": { "servers": { "matcad": { "routes": [ { "match": [ { "host": [ "raw.example.com" ] } ], "terminal": true } ] } } } } }""");

        var routes = MainRoutes();

        Assert.Equal("raw.example.com", routes[^2]!["match"]![0]!["host"]![0]!.GetValue<string>());
        Assert.Null(routes[^1]!["match"]);
    }

    [Fact]
    public void A_backup_with_values_the_page_would_refuse_is_cleaned()
    {
        var settings = new MatcadSettings { UnknownHost = "teapot", UnknownHostRedirect = "https://x/{env.A}" };
        BackupRestore.SanitizeSettings(settings);
        Assert.Equal("", settings.UnknownHost);
        Assert.Equal("", settings.UnknownHostRedirect);

        settings = new MatcadSettings { UnknownHost = " NotFound ", UnknownHostRedirect = " https://www.example.com " };
        BackupRestore.SanitizeSettings(settings);
        Assert.Equal("notfound", settings.UnknownHost);
        Assert.Equal("https://www.example.com", settings.UnknownHostRedirect);
    }
}

/// <summary>The setting through the settings page.</summary>
public sealed class UnknownHostPageTests(MatcadFactory factory) : IClassFixture<MatcadFactory>
{
    private ConfigStore Store => factory.Services.GetRequiredService<ConfigStore>();

    private async Task<Browser> AdminAsync()
    {
        var auth = factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("unknown-admin") == null)
            await auth.CreateUser("unknown-admin", null, "unknown-admin-password", Matcad.Data.UserRole.Admin, null);
        var settings = Store.Settings;
        settings.SetupCompleted = true;
        Store.SaveSettings(settings);
        var browser = new Browser(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("unknown-admin", "unknown-admin-password")).StatusCode);
        return browser;
    }

    private static Dictionary<string, string> Form(string mode, string redirect) => new()
    {
        ["SystemRouteEnabled"] = "true", ["PortalMode"] = "inline", ["LogRetentionDays"] = "30", ["LogRetentionMaxRows"] = "1000",
        ["UnknownHost"] = mode, ["UnknownHostRedirect"] = redirect,
    };

    [Fact]
    public async Task The_page_saves_the_answer_and_shows_it_again()
    {
        var browser = await AdminAsync();

        var response = await browser.PostFormAsync("/Settings", "/Settings", Form("redirect", " https://www.example.com "));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("redirect", Store.Settings.UnknownHost);
        Assert.Equal("https://www.example.com", Store.Settings.UnknownHostRedirect);
        var (_, html) = await browser.GetAsync("/Settings");
        Assert.Matches("<option(?=[^>]*selected)(?=[^>]*value=\"redirect\")", html);
        Assert.Contains("value=\"https://www.example.com\"", html);
    }

    [Theory]
    [InlineData("teapot", "")]
    [InlineData("redirect", "")]
    [InlineData("redirect", "https://x.example.com/{env.SECRET}")]
    [InlineData("notfound", "ftp://x.example.com")]
    public async Task The_page_refuses_what_it_cannot_use_and_saves_nothing(string mode, string redirect)
    {
        var browser = await AdminAsync();
        var settings = Store.Settings;
        settings.UnknownHost = "drop";
        settings.UnknownHostRedirect = "";
        Store.SaveSettings(settings);

        var response = await browser.PostFormAsync("/Settings", "/Settings", Form(mode, redirect));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("drop", Store.Settings.UnknownHost);
        Assert.Equal("", Store.Settings.UnknownHostRedirect);
        var (_, html) = await browser.GetAsync("/Settings");
        Assert.Contains("Unknown hosts:", html);
    }
}
