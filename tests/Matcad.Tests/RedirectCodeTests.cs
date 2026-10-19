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

/// <summary>Redirect codes 307 and 308 (the browser repeats the method and the body) next to 301 and 302.</summary>
public sealed class RedirectCodeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConfigStore _store;
    private readonly CaddyConfigGenerator _generator;

    public RedirectCodeTests()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Matcad:ConfigDir"] = _folder }).Build();
        _store = new ConfigStore(cfg, NullLogger<ConfigStore>.Instance);
        _generator = new CaddyConfigGenerator(_store, new RouteProvider(_store, new DockerRouteCache(), cfg), cfg);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    private int StatusOf(string host)
    {
        var config = JsonSerializer.SerializeToNode(_generator.Build())!;
        var route = config["apps"]!["http"]!["servers"]!["matcad"]!["routes"]!.AsArray()
            .Where(r => r!["match"]?[0]?["protocol"] == null) // not the HTTP -> HTTPS redirect
            .Single(r => r!["match"]![0]!["host"]![0]!.GetValue<string>() == host)!;
        return route["handle"]![0]!["status_code"]!.GetValue<int>();
    }

    [Theory]
    [InlineData(false, false, 302)]
    [InlineData(true, false, 301)]
    [InlineData(false, true, 307)]
    [InlineData(true, true, 308)]
    public void The_generated_config_answers_with_the_code_of_the_route(bool permanent, bool keepMethod, int code)
    {
        _store.UpsertRoute(new RouteConfig { Host = "old.example.com", FallbackUrl = "https://new.example.com", RedirectPermanent = permanent, RedirectKeepMethod = keepMethod }, null);

        Assert.Equal(code, StatusOf("old.example.com"));
    }

    [Theory]
    [InlineData(301, true, false)]
    [InlineData(302, false, false)]
    [InlineData(307, false, true)]
    [InlineData(308, true, true)]
    public void The_four_codes_split_into_permanent_and_keep_method(int code, bool permanent, bool keepMethod)
    {
        Assert.True(RouteEditor.TrySplitRedirectCode(code, out var p, out var k));
        Assert.Equal((permanent, keepMethod), (p, k));
        Assert.Equal(code, RouteEditor.RedirectCode(p, k));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(303)]
    [InlineData(304)]
    [InlineData(399)]
    public void Other_codes_are_refused(int code) =>
        Assert.False(RouteEditor.TrySplitRedirectCode(code, out _, out _));

    [Fact]
    public void Keeping_the_method_only_sticks_to_a_redirect()
    {
        var route = new RouteConfig();
        var redirect = new RouteForm("a.example.com", false, true, null, false, "https://b.example.com", true, null, null, null, true, null, false, null,
            Options: new RouteExtras(false, false, false, false, [], RedirectKeepMethod: true));
        Assert.Null(RouteEditor.Apply(redirect, route));
        Assert.Equal(308, route.RedirectStatus);

        var proxy = new RouteForm("a.example.com", false, false, "a:80", false, null, false, null, null, null, true, null, false, null,
            Options: new RouteExtras(false, false, false, false, [], RedirectKeepMethod: true));
        Assert.Null(RouteEditor.Apply(proxy, route));
        Assert.False(route.RedirectKeepMethod);
    }

    [Fact]
    public void The_caddyfile_import_keeps_307_and_308()
    {
        const string adapted = """
            { "apps": { "http": { "servers": { "srv0": { "listen": [":443"], "routes": [
              { "match": [ { "host": [ "a.example.com" ] } ], "terminal": true,
                "handle": [ { "handler": "static_response", "status_code": 308, "headers": { "Location": [ "https://b.example.com" ] } } ] },
              { "match": [ { "host": [ "c.example.com" ] } ], "terminal": true,
                "handle": [ { "handler": "static_response", "status_code": 307, "headers": { "Location": [ "https://d.example.com" ] } } ] } ] } } } } }
            """;

        var routes = new CaddyfileImporter().Analyze(adapted).Routes.Select(r => r.Route).ToList();

        Assert.Equal(308, routes.Single(r => r.Host == "a.example.com").RedirectStatus);
        Assert.Equal(307, routes.Single(r => r.Host == "c.example.com").RedirectStatus);
    }
}

/// <summary>The redirect code through the route page and the REST API.</summary>
public sealed class RedirectCodePageTests(MatcadFactory factory) : IClassFixture<MatcadFactory>
{
    private ConfigStore Store => factory.Services.GetRequiredService<ConfigStore>();

    private async Task<Browser> SignedInAsync()
    {
        var auth = factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("redirect-user") == null)
            await auth.CreateUser("redirect-user", null, "redirect-user-password", Matcad.Data.UserRole.User, null);
        var settings = Store.Settings;
        settings.SetupCompleted = true;
        Store.SaveSettings(settings);
        var browser = new Browser(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("redirect-user", "redirect-user-password")).StatusCode);
        return browser;
    }

    private static Dictionary<string, string> Redirect(string host, string code) => new()
    {
        ["Host"] = host, ["RouteType"] = "single", ["Target"] = "redirect", ["FallbackUrl"] = "https://target.example.com", ["Enabled"] = "true",
        ["RedirectCode"] = code,
    };

    [Fact]
    public async Task The_page_saves_the_code_and_shows_it_again()
    {
        var browser = await SignedInAsync();

        var response = await browser.PostFormAsync("/Routes/Edit", "/Routes/Edit", Redirect("code.example.com", "307"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var route = Store.Routes.Single(r => r.Host == "code.example.com");
        Assert.Equal(307, route.RedirectStatus);
        Assert.False(route.RedirectPermanent);
        var (_, html) = await browser.GetAsync($"/Routes/Edit?id={route.Id}");
        Assert.Matches("<option(?=[^>]*selected)(?=[^>]*value=\"307\")", html);
        var (_, list) = await browser.GetAsync("/Routes");
        Assert.Contains(">307</span>", list);
    }

    [Fact]
    public async Task The_page_refuses_a_code_that_is_not_a_redirect()
    {
        var browser = await SignedInAsync();

        var response = await browser.PostFormAsync("/Routes/Edit", "/Routes/Edit", Redirect("bad-code.example.com", "303"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("301, 302, 307 or 308", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(Store.Routes, r => r.Host == "bad-code.example.com");
    }

    [Fact]
    public async Task The_api_keeps_the_kept_method_and_answers_in_the_old_shape()
    {
        Store.UpsertRoute(new RouteConfig { Host = "api-code.example.com", FallbackUrl = "https://x.example.com", RedirectPermanent = true, RedirectKeepMethod = true, Enabled = true }, null);
        var api = factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);

        var response = await api.PostAsync("/api/v1/routes", new StringContent(
            """{ "host": "api-code.example.com", "target": "redirect", "fallbackUrl": "https://y.example.com", "redirectPermanent": false, "enabled": true }""",
            Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var route = Store.Routes.Single(r => r.Host == "api-code.example.com");
        Assert.Equal(307, route.RedirectStatus); // temporary now, the kept method stays
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.False(body["route"]!["redirectPermanent"]!.GetValue<bool>());
        Assert.Null(body["route"]!["redirectKeepMethod"]);
    }
}
