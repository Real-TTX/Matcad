using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matcad.Config;
using Matcad.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

/// <summary>Custom locations: paths of a route with their own upstream or redirect.</summary>
public sealed class LocationTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConfigStore _store;
    private readonly CaddyConfigGenerator _generator;

    public LocationTests()
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
            .Where(r => r!["match"]?[0]?["protocol"] == null)
            .Single(r => r!["match"]![0]!["host"]![0]!.GetValue<string>() == host && r["match"]![0]!["path"] == null)!;
        return route["handle"]!.AsArray();
    }

    private static RouteForm Proxy(params RouteLocation[] locations) =>
        new("a.example.com", false, false, "backend:80", false, null, false, null, null, null, true, null, false, null,
            Options: new RouteExtras(false, false, false, false, [], Locations: locations));

    [Fact]
    public void Locations_are_a_subroute_with_the_most_specific_path_first_and_the_route_target_last()
    {
        _store.UpsertRoute(new RouteConfig
        {
            Host = "loc.example.com", Upstream = "main:80", BlockExploits = true, UpstreamHost = "inner.example.org",
            UpstreamHeaders = [new HeaderPair { Name = "X-Up", Value = "for-main-only" }],
            RequireHeader = new HeaderPair { Name = "X-Edge", Value = "edge-secret-1234567890" },
            Locations =
            [
                new RouteLocation { Path = "/api", Upstream = "https://api:8443", StripPath = true },
                new RouteLocation { Path = "/api/v2", Upstream = "api2:80" },
                new RouteLocation { Path = "/old", RedirectUrl = "https://new.example.com", RedirectCode = 308 },
            ]
        }, null);

        var handlers = HandlersOf("loc.example.com");

        // guard, exploit filter, then the locations at the end of the chain
        Assert.Equal(["subroute", "subroute", "subroute"], handlers.Select(h => h!["handler"]!.GetValue<string>()));
        var routes = handlers[2]!["routes"]!.AsArray();
        Assert.Equal(["/api/v2", "/api", "/old"], routes.Take(3).Select(r => r!["match"]![0]!["path"]![0]!.GetValue<string>()));
        Assert.Equal("/api/*", routes[1]!["match"]![0]!["path"]![1]!.GetValue<string>());

        var api = routes[1]!["handle"]!.AsArray();
        Assert.Equal("/api", api[0]!["strip_path_prefix"]!.GetValue<string>());
        Assert.Equal("api:8443", api[1]!["upstreams"]![0]!["dial"]!.GetValue<string>());
        Assert.NotNull(api[1]!["transport"]!["tls"]);
        // The secret of the guard never goes on; the Host header and the headers of the route are for its own upstream only.
        Assert.Equal(["X-Edge"], api[1]!["headers"]!["request"]!["delete"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Null(api[1]!["headers"]!["request"]!["set"]);
        Assert.Null(routes[0]!["handle"]![0]!["strip_path_prefix"]);

        var old = routes[2]!["handle"]![0]!;
        Assert.Equal(308, old["status_code"]!.GetValue<int>());
        Assert.Equal("https://new.example.com", old["headers"]!["Location"]![0]!.GetValue<string>());

        var rest = routes[3]!;
        Assert.Null(rest["match"]);
        Assert.Equal("main:80", rest["handle"]![0]!["upstreams"]![0]!["dial"]!.GetValue<string>());
        Assert.Equal("for-main-only", rest["handle"]![0]!["headers"]!["request"]!["set"]!["X-Up"]![0]!.GetValue<string>());
    }

    [Fact]
    public void Without_locations_the_chain_is_unchanged()
    {
        _store.UpsertRoute(new RouteConfig { Host = "plain.example.com", Upstream = "a:80" }, null);

        Assert.Equal(["reverse_proxy"], HandlersOf("plain.example.com").Select(h => h!["handler"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("api", "must look like")]
    [InlineData("/", "the route itself")]
    [InlineData("/api/*", "must look like")]
    [InlineData("/a%2fb", "must look like")]
    [InlineData("/{env.X}", "must look like")]
    [InlineData("/a b", "must look like")]
    [InlineData("/a?b", "must look like")]
    [InlineData("/a//b", "must look like")]
    [InlineData("/a/../b", ". or ..")]
    [InlineData("/auth", "login pages")]
    [InlineData("/Auth/x", "login pages")]
    public void Paths_are_checked(string path, string reason)
    {
        var route = new RouteConfig();

        var error = RouteEditor.Apply(Proxy(new RouteLocation { Path = path, Upstream = "x:80" }), route);

        Assert.Contains(reason, error);
        Assert.Empty(route.Locations);
    }

    [Fact]
    public void Targets_are_checked()
    {
        Assert.Contains("either an upstream or a redirect", RouteEditor.Apply(Proxy(new RouteLocation { Path = "/a" }), new RouteConfig()));
        Assert.Contains("either an upstream or a redirect", RouteEditor.Apply(Proxy(new RouteLocation { Path = "/a", Upstream = "x:80", RedirectUrl = "https://x.example.com" }), new RouteConfig()));
        Assert.Contains("upstream must look like", RouteEditor.Apply(Proxy(new RouteLocation { Path = "/a", Upstream = "not a host" }), new RouteConfig()));
        Assert.Contains("{ or }", RouteEditor.Apply(Proxy(new RouteLocation { Path = "/a", RedirectUrl = "https://x.example.com/{env.A}" }), new RouteConfig()));
        Assert.Contains("301, 302, 307 or 308", RouteEditor.Apply(Proxy(new RouteLocation { Path = "/a", RedirectUrl = "https://x.example.com", RedirectCode = 303 }), new RouteConfig()));
        Assert.Contains("strip", RouteEditor.Apply(Proxy(new RouteLocation { Path = "/a", RedirectUrl = "https://x.example.com", StripPath = true }), new RouteConfig()));
        Assert.Contains("twice", RouteEditor.Apply(Proxy(new RouteLocation { Path = "/a", Upstream = "x:80" }, new RouteLocation { Path = "/A/", Upstream = "y:80" }), new RouteConfig()));
    }

    [Fact]
    public void The_editor_normalises_and_keeps_them_without_options()
    {
        var route = new RouteConfig();
        Assert.Null(RouteEditor.Apply(Proxy(new RouteLocation { Path = " /api/ ", Upstream = " api:80 " }), route));
        Assert.Equal("/api", route.Locations.Single().Path);
        Assert.Equal("api:80", route.Locations.Single().Upstream);

        var api = new RouteForm("a.example.com", false, false, "b:80", false, null, false, null, null, null, true, null, false, null);
        Assert.Null(RouteEditor.Apply(api, route));
        Assert.Single(route.Locations);
    }

    [Fact]
    public void A_stored_location_that_breaks_the_rules_closes_the_route()
    {
        _store.UpsertRoute(new RouteConfig { Host = "bad.example.com", Upstream = "a:80", Locations = [new RouteLocation { Path = "/*", Upstream = "b:80" }] }, null);

        Assert.Equal(503, Assert.Single(HandlersOf("bad.example.com"))!["status_code"]!.GetValue<int>());
    }
}

/// <summary>Locations through the route page and the REST API.</summary>
public sealed class LocationPageTests(MatcadFactory factory) : IClassFixture<MatcadFactory>
{
    private ConfigStore Store => factory.Services.GetRequiredService<ConfigStore>();

    private async Task<Browser> SignedInAsync()
    {
        var auth = factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("loc-user") == null)
            await auth.CreateUser("loc-user", null, "loc-user-password", Matcad.Data.UserRole.User, null);
        var settings = Store.Settings;
        settings.SetupCompleted = true;
        Store.SaveSettings(settings);
        var browser = new Browser(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("loc-user", "loc-user-password")).StatusCode);
        return browser;
    }

    private static List<KeyValuePair<string, string>> Form(string host, params (string Path, string Kind, string Target)[] rows)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Host", host), new("RouteType", "single"), new("Target", "proxy"), new("Upstream", "a:80"), new("Enabled", "true"),
        };
        foreach (var (path, kind, target) in rows)
        {
            fields.Add(new("LocationPaths", path));
            fields.Add(new("LocationKinds", kind));
            fields.Add(new("LocationTargets", target));
        }
        return fields;
    }

    [Fact]
    public async Task The_page_saves_the_locations_and_shows_them_again()
    {
        var browser = await SignedInAsync();

        var response = await browser.PostFormPairsAsync("/Routes/Edit", "/Routes/Edit",
            Form("loc-page.example.com", ("/api", "proxy-strip", "api:8080"), ("/old", "307", "https://new.example.com"), ("", "proxy", "")));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var route = Store.Routes.Single(r => r.Host == "loc-page.example.com");
        Assert.Equal(2, route.Locations.Count);
        Assert.True(route.Locations[0].StripPath);
        Assert.Equal(307, route.Locations[1].RedirectCode);
        var (_, html) = await browser.GetAsync($"/Routes/Edit?id={route.Id}");
        Assert.Contains("value=\"/api\"", html);
        Assert.Matches("<option(?=[^>]*selected)(?=[^>]*value=\"proxy-strip\")", html);
        Assert.Matches("<option(?=[^>]*selected)(?=[^>]*value=\"307\")", html);
    }

    [Fact]
    public async Task The_page_refuses_a_bad_path_and_keeps_the_rows()
    {
        var browser = await SignedInAsync();

        var response = await browser.PostFormPairsAsync("/Routes/Edit", "/Routes/Edit", Form("loc-bad.example.com", ("/api/*", "proxy", "api:8080")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("must look like", html);
        Assert.Contains("value=\"api:8080\"", html);
        Assert.DoesNotContain(Store.Routes, r => r.Host == "loc-bad.example.com");
    }

    [Fact]
    public async Task The_api_keeps_the_locations_and_answers_in_the_old_shape()
    {
        Store.UpsertRoute(new RouteConfig { Host = "loc-api.example.com", Upstream = "a:80", Enabled = true, Locations = [new RouteLocation { Path = "/api", Upstream = "b:80" }] }, null);
        var api = factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);

        var response = await api.PostAsync("/api/v1/routes", new StringContent("""{ "host": "loc-api.example.com", "upstream": "c:80", "enabled": true }""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("/api", Store.Routes.Single(r => r.Host == "loc-api.example.com").Locations.Single().Path);
        Assert.Null(JsonNode.Parse(await response.Content.ReadAsStringAsync())!["route"]!["locations"]);
    }
}
