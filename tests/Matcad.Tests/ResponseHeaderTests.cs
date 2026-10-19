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

/// <summary>Headers set on or removed from the answer of a route, and the checks that keep header injection and Caddy placeholders out.</summary>
public sealed class ResponseHeaderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConfigStore _store;
    private readonly CaddyConfigGenerator _generator;

    public ResponseHeaderTests()
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
            .Single(r => r!["match"]![0]!["host"]![0]!.GetValue<string>() == host)!;
        return route["handle"]!.AsArray();
    }

    private static ResponseHeader Set(string name, string value) => new() { Name = name, Value = value };
    private static ResponseHeader Remove(string name) => new() { Name = name, Remove = true };

    private static RouteForm Proxy(IReadOnlyList<ResponseHeader> headers, bool hsts = false, bool allowEmbedding = false) =>
        new("a.example.com", false, false, "backend:80", false, null, false, null, null, null, true, null, allowEmbedding, null,
            Options: new RouteExtras(false, hsts, false, false, [], ResponseHeaders: headers));

    [Fact]
    public void The_headers_are_set_and_removed_deferred_with_canonical_names()
    {
        _store.UpsertRoute(new RouteConfig
        {
            Host = "resp.example.com", Upstream = "a:80", Hsts = true,
            ResponseHeaders = [Set("x-content-type-options", "nosniff"), Set("Referrer-Policy", "no-referrer"), Remove("server"), Remove("X-Powered-By")]
        }, null);

        var handlers = HandlersOf("resp.example.com");

        Assert.Equal(["headers", "headers", "reverse_proxy"], handlers.Select(h => h!["handler"]!.GetValue<string>()));
        var response = handlers[1]!["response"]!;
        Assert.True(response["deferred"]!.GetValue<bool>());
        Assert.Equal("nosniff", response["set"]!["X-Content-Type-Options"]![0]!.GetValue<string>());
        Assert.Equal("no-referrer", response["set"]!["Referrer-Policy"]![0]!.GetValue<string>());
        Assert.Equal(["Server", "X-Powered-By"], response["delete"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    [Fact]
    public void A_redirect_route_carries_them_too()
    {
        _store.UpsertRoute(new RouteConfig { Host = "go.example.com", FallbackUrl = "https://x.example.com", ResponseHeaders = [Set("Cache-Control", "no-store")] }, null);

        Assert.Equal(["headers", "static_response"], HandlersOf("go.example.com").Select(h => h!["handler"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("X-Test", "a\r\nSet-Cookie: x=1", "line breaks")]
    [InlineData("X-Test", "a\nb", "line breaks")]
    [InlineData("X-Test", "tab\there", "line breaks")]
    [InlineData("X-Test", "{env.SECRET}", "placeholder")]
    [InlineData("X-Test", "a}b", "placeholder")]
    [InlineData("X-Test", "Grüße", "visible ASCII")]
    [InlineData("X-Test", " edge", "space")]
    [InlineData("X-Test", "", "enter a value")]
    [InlineData("X Test", "v", "not a usable header name")]
    [InlineData("X-Test\r\nEvil", "v", "not a usable header name")]
    [InlineData("X-{a}", "v", "not a usable header name")]
    [InlineData("X-*", "v", "not a usable header name")]
    [InlineData("-Server", "v", "not a usable header name")]
    [InlineData("Content-Length", "1", "HTTP message")]
    [InlineData("transfer-encoding", "chunked", "HTTP message")]
    [InlineData("Content-Encoding", "gzip", "HTTP message")]
    public void Injection_and_placeholders_are_refused(string name, string value, string reason)
    {
        var route = new RouteConfig();

        var error = RouteEditor.Apply(Proxy([Set(name, value)]), route);

        Assert.NotNull(error);
        Assert.Contains(reason, error);
        Assert.Empty(route.ResponseHeaders);
    }

    [Fact]
    public void Duplicates_a_removed_header_with_a_value_and_too_many_headers_are_refused()
    {
        Assert.Contains("listed twice", RouteEditor.Apply(Proxy([Set("X-A", "1"), Remove("x-a")]), new RouteConfig()));
        Assert.Contains("has no value", RouteEditor.Apply(Proxy([new ResponseHeader { Name = "Server", Value = "x", Remove = true }]), new RouteConfig()));
        Assert.Contains("at most", RouteEditor.Apply(Proxy(Enumerable.Range(0, 21).Select(i => Set($"X-H{i}", "v")).ToList()), new RouteConfig()));
    }

    [Fact]
    public void A_header_that_a_switch_of_the_route_writes_is_refused()
    {
        Assert.Contains("HSTS", RouteEditor.Apply(Proxy([Set("Strict-Transport-Security", "max-age=60")], hsts: true), new RouteConfig()));
        Assert.Contains("embedding", RouteEditor.Apply(Proxy([Set("X-Frame-Options", "DENY")], allowEmbedding: true), new RouteConfig()));
        // Without the switch it is the user's own header; removing X-Frame-Options agrees with embedding.
        Assert.Null(RouteEditor.Apply(Proxy([Set("Strict-Transport-Security", "max-age=60")]), new RouteConfig()));
        Assert.Null(RouteEditor.Apply(Proxy([Remove("X-Frame-Options")], allowEmbedding: true), new RouteConfig()));
    }

    [Fact]
    public void The_editor_stores_them_and_keeps_them_without_options()
    {
        var route = new RouteConfig();
        Assert.Null(RouteEditor.Apply(Proxy([Set(" X-A ", "1 2"), Remove("Server")]), route));
        Assert.Equal("X-A", route.ResponseHeaders[0].Name);
        Assert.Equal("1 2", route.ResponseHeaders[0].Value);

        var api = new RouteForm("a.example.com", false, false, "b:80", false, null, false, null, null, null, true, null, false, null);
        Assert.Null(RouteEditor.Apply(api, route));
        Assert.Equal(2, route.ResponseHeaders.Count);
    }

    [Fact]
    public void A_stored_header_that_would_inject_closes_the_route()
    {
        // A hand-edited routes.json or a backup from somewhere else does not go through the editor.
        _store.UpsertRoute(new RouteConfig { Host = "evil.example.com", Upstream = "a:80", ResponseHeaders = [Set("X-A", "{env.HOME}")] }, null);

        var handlers = HandlersOf("evil.example.com");

        Assert.Equal(503, Assert.Single(handlers)!["status_code"]!.GetValue<int>());
        Assert.DoesNotContain("env.HOME", handlers.ToJsonString());
    }
}

/// <summary>The response headers through the route page and the REST API.</summary>
public sealed class ResponseHeaderPageTests(MatcadFactory factory) : IClassFixture<MatcadFactory>
{
    private ConfigStore Store => factory.Services.GetRequiredService<ConfigStore>();

    private async Task<Browser> SignedInAsync()
    {
        var auth = factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("resp-user") == null)
            await auth.CreateUser("resp-user", null, "resp-user-password", Matcad.Data.UserRole.User, null);
        var settings = Store.Settings;
        settings.SetupCompleted = true;
        Store.SaveSettings(settings);
        var browser = new Browser(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("resp-user", "resp-user-password")).StatusCode);
        return browser;
    }

    private static List<KeyValuePair<string, string>> Form(string host, params (string Action, string Name, string Value)[] rows)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("Host", host), new("RouteType", "single"), new("Target", "proxy"), new("Upstream", "a:80"), new("Enabled", "true"),
        };
        foreach (var (action, name, value) in rows)
        {
            fields.Add(new("ResponseActions", action));
            fields.Add(new("ResponseNames", name));
            fields.Add(new("ResponseValues", value));
        }
        return fields;
    }

    [Fact]
    public async Task The_page_saves_the_headers_and_shows_them_again()
    {
        var browser = await SignedInAsync();

        var response = await browser.PostFormPairsAsync("/Routes/Edit", "/Routes/Edit",
            Form("resp-page.example.com", ("set", "X-Content-Type-Options", "nosniff"), ("remove", "Server", "ignored"), ("set", "", "")));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var route = Store.Routes.Single(r => r.Host == "resp-page.example.com");
        Assert.Equal(2, route.ResponseHeaders.Count);
        Assert.True(route.ResponseHeaders[1].Remove);
        Assert.Equal("", route.ResponseHeaders[1].Value); // a removed header keeps no value
        var (_, html) = await browser.GetAsync($"/Routes/Edit?id={route.Id}");
        Assert.Contains("value=\"nosniff\"", html);
        Assert.Matches(@"<details class=""mat-collapse"" open=""open"" id=""route-response""|<details class=""mat-collapse"" open id=""route-response""", html);
    }

    [Fact]
    public async Task The_page_refuses_a_line_break_and_saves_nothing()
    {
        var browser = await SignedInAsync();

        var response = await browser.PostFormPairsAsync("/Routes/Edit", "/Routes/Edit",
            Form("resp-bad.example.com", ("set", "X-A", "ok\r\nSet-Cookie: session=evil")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no line breaks", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(Store.Routes, r => r.Host == "resp-bad.example.com");
    }

    [Fact]
    public async Task The_api_keeps_the_headers_and_answers_in_the_old_shape()
    {
        Store.UpsertRoute(new RouteConfig { Host = "resp-api.example.com", Upstream = "a:80", Enabled = true, ResponseHeaders = [new() { Name = "X-A", Value = "1" }] }, null);
        var api = factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);

        var response = await api.PostAsync("/api/v1/routes", new StringContent("""{ "host": "resp-api.example.com", "upstream": "b:80", "enabled": true }""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("X-A", Assert.Single(Store.Routes.Single(r => r.Host == "resp-api.example.com").ResponseHeaders).Name);
        Assert.Null(JsonNode.Parse(await response.Content.ReadAsStringAsync())!["route"]!["responseHeaders"]);
    }
}
