using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Matcad.Config;
using Matcad.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

/// <summary>The options of a route the yardstick (Nginx Proxy Manager) has: blocked addresses, block common exploits, HSTS,
/// compression and a redirect that keeps the path. (Checked against a real caddy:2 as well: 403s, the header, gzip and the Location.)</summary>
public sealed class RouteOptionsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConfigStore _store;
    private readonly CaddyConfigGenerator _generator;

    public RouteOptionsTests()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Matcad:ConfigDir"] = _folder }).Build();
        _store = new ConfigStore(cfg, NullLogger<ConfigStore>.Instance);
        _generator = new CaddyConfigGenerator(_store, new RouteProvider(_store, new DockerRouteCache(), cfg), cfg);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    private JsonArray HandlersOf(string host)
    {
        var config = JsonSerializer.SerializeToNode(_generator.Build())!;
        var route = config["apps"]!["http"]!["servers"]!["matcad"]!["routes"]!.AsArray()
            .Where(r => r!["match"]?[0]?["protocol"] == null) // not the HTTP -> HTTPS redirect
            .Single(r => r!["match"]![0]!["host"]![0]!.GetValue<string>() == host && r["match"]![0]!["path"] == null)!;
        return route["handle"]!.AsArray();
    }

    private static List<string> Names(JsonArray handlers) => handlers.Select(h => h!["handler"]!.GetValue<string>()).ToList();

    private static RouteForm Proxy(string host, RouteExtras? extras = null) =>
        new(host, false, false, "backend:80", false, null, false, null, null, null, true, null, false, null, Options: extras);

    [Fact]
    public void Every_option_is_off_for_a_route_that_has_none()
    {
        _store.UpsertRoute(new RouteConfig { Host = "plain.example.com", Upstream = "a:80" }, null);

        Assert.Equal(["reverse_proxy"], Names(HandlersOf("plain.example.com")));
    }

    [Fact]
    public void The_options_sit_in_order_in_front_of_the_proxy()
    {
        _store.UpsertRoute(new RouteConfig { Host = "all.example.com", Upstream = "a:80", BlockExploits = true, Hsts = true, Compress = true, BlockedIps = ["198.51.100.0/24"] }, null);

        var handlers = HandlersOf("all.example.com");

        Assert.Equal(["subroute", "subroute", "headers", "encode", "reverse_proxy"], Names(handlers));
        var blocked = handlers[0]!["routes"]![0]!;
        Assert.Equal("198.51.100.0/24", blocked["match"]![0]!["remote_ip"]!["ranges"]![0]!.GetValue<string>()); // matched directly, no "not"
        Assert.Equal(403, blocked["handle"]![0]!["status_code"]!.GetValue<int>());
        Assert.Equal("max-age=31536000", handlers[2]!["response"]!["set"]!["Strict-Transport-Security"]![0]!.GetValue<string>());
        Assert.Equal(["zstd", "gzip"], handlers[3]!["prefer"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void A_blocked_list_that_cannot_be_read_closes_the_route()
    {
        _store.UpsertRoute(new RouteConfig { Host = "broken.example.com", Upstream = "a:80", BlockedIps = ["not-an-ip"] }, null);

        var handlers = HandlersOf("broken.example.com");

        Assert.Equal(503, Assert.Single(handlers)!["status_code"]!.GetValue<int>());
    }

    [Fact]
    public void A_redirect_that_keeps_the_path_appends_the_request_uri_once()
    {
        _store.UpsertRoute(new RouteConfig { Host = "old.example.com", FallbackUrl = "https://new.example.com/", RedirectKeepPath = true, Compress = true }, null);

        var handlers = HandlersOf("old.example.com");

        Assert.Equal(["static_response"], Names(handlers)); // nothing to compress
        Assert.Equal("https://new.example.com{http.request.uri}", handlers[0]!["headers"]!["Location"]![0]!.GetValue<string>());
    }

    [Theory]
    [InlineData("/.git/config", true)]
    [InlineData("/.env", true)]
    [InlineData("/app/.htpasswd", true)]
    [InlineData("/wp-config.php", true)]
    [InlineData("/.well-known/acme-challenge/x", false)]
    [InlineData("/environment", false)]
    [InlineData("/docs/git", false)]
    public void The_exploit_filter_knows_the_paths_of_secrets(string path, bool blocked) =>
        Assert.Equal(blocked, Regex.IsMatch(path, CaddyConfigGenerator.ExploitPathPattern));

    [Theory]
    [InlineData("id=1%20UNION%20SELECT%20password", true)]
    [InlineData("id=1+union+all+select+1", true)]
    [InlineData("q=%3Cscript%3Ealert(1)", true)]
    [InlineData("GLOBALS[x]=1", true)]
    [InlineData("file=../../etc/passwd", true)]
    [InlineData("f=%2e%2e%2fsecret", true)]
    [InlineData("q=select%20a%20union", false)]
    [InlineData("page=2&sort=name", false)]
    [InlineData("q=description", false)]
    public void The_exploit_filter_knows_attacks_in_the_query(string query, bool blocked) =>
        Assert.Equal(blocked, Regex.IsMatch(query, CaddyConfigGenerator.ExploitQueryPattern));

    [Fact]
    public void The_editor_checks_the_blocked_list_and_keeps_options_where_they_mean_something()
    {
        var route = new RouteConfig();
        Assert.Contains("Blocked IPs", RouteEditor.Apply(Proxy("a.example.com", new RouteExtras(false, false, false, false, ["nope"])), route));

        Assert.Null(RouteEditor.Apply(Proxy("a.example.com", new RouteExtras(true, true, true, true, ["10.1.2.3/8"])), route));
        Assert.False(route.RedirectKeepPath); // only for a redirect
        Assert.True(route.Hsts && route.Compress && route.BlockExploits);
        Assert.Equal(["10.0.0.0/8"], route.BlockedIps);

        var redirect = new RouteForm("b.example.com", false, true, null, false, "https://x.example.com", false, null, null, null, true, null, false, null,
            Options: new RouteExtras(true, false, true, false, []));
        Assert.Null(RouteEditor.Apply(redirect, route));
        Assert.True(route.RedirectKeepPath);
        Assert.False(route.Compress); // nothing to compress
    }

    [Fact]
    public void Without_options_the_editor_leaves_those_of_the_route()
    {
        var route = new RouteConfig { Hsts = true, BlockExploits = true, BlockedIps = ["192.0.2.1"] };

        Assert.Null(RouteEditor.Apply(Proxy("a.example.com"), route));

        Assert.True(route.Hsts && route.BlockExploits);
        Assert.Equal(["192.0.2.1"], route.BlockedIps);
    }

    [Fact]
    public void The_caddyfile_import_keeps_the_path_of_a_redirect_and_leaves_other_placeholders_raw()
    {
        const string adapted = """
            { "apps": { "http": { "servers": { "srv0": { "listen": [":443"], "routes": [
              { "match": [ { "host": [ "a.example.com" ] } ], "terminal": true,
                "handle": [ { "handler": "static_response", "status_code": 301, "headers": { "Location": [ "https://b.example.com{http.request.uri}" ] } } ] },
              { "match": [ { "host": [ "c.example.com" ] } ], "terminal": true,
                "handle": [ { "handler": "static_response", "status_code": 302, "headers": { "Location": [ "https://{http.request.host}.example.net" ] } } ] } ] } } } } }
            """;

        var plan = new CaddyfileImporter().Analyze(adapted);

        var route = Assert.Single(plan.Routes).Route;
        Assert.Equal("https://b.example.com", route.FallbackUrl);
        Assert.True(route.RedirectKeepPath);
        Assert.True(route.RedirectPermanent);
        Assert.Contains("c.example.com", plan.RawRemainder);
    }
}

/// <summary>The options through the edit page and the REST API.</summary>
public sealed class RouteOptionsPageTests(MatcadFactory factory) : IClassFixture<MatcadFactory>
{
    private ConfigStore Store => factory.Services.GetRequiredService<ConfigStore>();

    private async Task<Browser> SignedInAsync()
    {
        var auth = factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("options-user") == null)
            await auth.CreateUser("options-user", null, "options-user-password", Matcad.Data.UserRole.User, null);
        var settings = Store.Settings;
        settings.SetupCompleted = true;
        Store.SaveSettings(settings);
        var browser = new Browser(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("options-user", "options-user-password")).StatusCode);
        return browser;
    }

    [Fact]
    public async Task The_page_saves_the_options_and_shows_them_again()
    {
        var browser = await SignedInAsync();

        var response = await browser.PostFormAsync("/Routes/Edit", "/Routes/Edit", new()
        {
            ["Host"] = "options.example.com", ["RouteType"] = "single", ["Target"] = "proxy", ["Upstream"] = "a:80", ["Enabled"] = "true",
            ["Hsts"] = "true", ["Compress"] = "true", ["BlockExploits"] = "true", ["BlockedIpsText"] = "198.51.100.7\n2001:db8::/32",
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var route = Store.Routes.Single(r => r.Host == "options.example.com");
        Assert.True(route.Hsts && route.Compress && route.BlockExploits);
        Assert.Equal(["198.51.100.7", "2001:db8::/32"], route.BlockedIps);

        var (_, html) = await browser.GetAsync($"/Routes/Edit?id={route.Id}");
        Assert.Matches("<input(?=[^>]*id=\"Hsts\")(?=[^>]*checked)", html);
        Assert.Contains("2001:db8::/32", html);
        Assert.Matches(@"<details class=""mat-collapse"" open", html);
    }

    [Fact]
    public async Task The_api_keeps_the_options_it_does_not_know_and_answers_in_the_old_shape()
    {
        Store.UpsertRoute(new RouteConfig { Host = "api-options.example.com", Upstream = "a:80", Enabled = true, Hsts = true, BlockExploits = true, BlockedIps = ["192.0.2.9"] }, null);
        var api = factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);

        var response = await api.PostAsync("/api/v1/routes", new StringContent("""{ "host": "api-options.example.com", "upstream": "b:80", "enabled": true }""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var route = Store.Routes.Single(r => r.Host == "api-options.example.com");
        Assert.Equal("b:80", route.Upstream);
        Assert.True(route.Hsts && route.BlockExploits);
        Assert.Equal(["192.0.2.9"], route.BlockedIps);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hsts", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blockedIps", body);
    }
}
