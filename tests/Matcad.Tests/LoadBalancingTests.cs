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

/// <summary>Several upstreams per route: load balancing, active health checks, and the health Matcad shows on the routes list.</summary>
public sealed class LoadBalancingTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConfigStore _store;
    private readonly CaddyConfigGenerator _generator;

    public LoadBalancingTests()
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

    private static RouteForm Proxy(string upstream, RouteBalancingForm balancing) =>
        new("a.example.com", false, false, upstream, false, null, false, null, null, null, true, null, false, null,
            Options: new RouteExtras(false, false, false, false, [], Balancing: balancing));

    private static RouteBalancingForm Balancing(string[] extra, string policy = "round_robin", string? path = null, int interval = 30, int status = 2) =>
        new(extra, policy, path, interval, status);

    [Fact]
    public void Several_upstreams_get_a_policy_and_a_health_check()
    {
        _store.UpsertRoute(new RouteConfig
        {
            Host = "lb.example.com", Upstream = "http://a:80", ExtraUpstreams = ["b:80", "http://c"], LoadBalancing = "least_conn",
            HealthPath = "/health", HealthIntervalSeconds = 10, HealthStatus = 200,
            Locations = [new RouteLocation { Path = "/api", Upstream = "api:80" }]
        }, null);

        var subroute = HandlersOf("lb.example.com").Single()!["routes"]!.AsArray();
        var location = subroute[0]!["handle"]![0]!;
        var main = subroute[1]!["handle"]![0]!;

        Assert.Equal(["a:80", "b:80", "c:80"], main["upstreams"]!.AsArray().Select(u => u!["dial"]!.GetValue<string>()));
        Assert.Equal("least_conn", main["load_balancing"]!["selection_policy"]!["policy"]!.GetValue<string>());
        var active = main["health_checks"]!["active"]!;
        Assert.Equal("/health", active["uri"]!.GetValue<string>());
        Assert.Equal("10s", active["interval"]!.GetValue<string>());
        Assert.Equal("5s", active["timeout"]!.GetValue<string>());
        Assert.Equal(200, active["expect_status"]!.GetValue<int>());
        // A location keeps its own single upstream.
        Assert.Single(location["upstreams"]!.AsArray());
        Assert.Null(location["load_balancing"]);
        Assert.Null(location["health_checks"]);
    }

    [Fact]
    public void One_upstream_has_no_policy_and_a_check_only_when_asked_for()
    {
        _store.UpsertRoute(new RouteConfig { Host = "one.example.com", Upstream = "a:80" }, null);
        _store.UpsertRoute(new RouteConfig { Host = "checked.example.com", Upstream = "a:80", HealthPath = "/up", HealthIntervalSeconds = 5 }, null);

        var plain = HandlersOf("one.example.com").Single()!;
        Assert.Null(plain["load_balancing"]);
        Assert.Null(plain["health_checks"]);
        var check = HandlersOf("checked.example.com").Single()!;
        Assert.Null(check["load_balancing"]);
        Assert.Equal(2, check["health_checks"]!["active"]!["expect_status"]!.GetValue<int>()); // 2 = any 2xx
    }

    [Theory]
    [InlineData("http://a:80", new[] { "https://b:443" }, "round_robin", null, 30, 2, "all of them must be http or all https")]
    [InlineData("a:80", new[] { "a:80" }, "round_robin", null, 30, 2, "listed twice")]
    [InlineData("a:80", new[] { "not a host" }, "round_robin", null, 30, 2, "must look like")]
    [InlineData("a:80", new[] { "b:80" }, "random", null, 30, 2, "Load balancing")]
    [InlineData("a:80", new string[0], "round_robin", "health", 30, 2, "must start with /")]
    [InlineData("a:80", new string[0], "round_robin", "/{env.X}", 30, 2, "must start with /")]
    [InlineData("a:80", new string[0], "round_robin", "/a b", 30, 2, "must start with /")]
    [InlineData("a:80", new string[0], "round_robin", "/health", 4, 2, "interval")]
    [InlineData("a:80", new string[0], "round_robin", "/health", 3601, 2, "interval")]
    [InlineData("a:80", new string[0], "round_robin", "/health", 30, 600, "expected status")]
    public void The_settings_are_checked(string upstream, string[] extra, string policy, string? path, int interval, int status, string reason)
    {
        var route = new RouteConfig();

        var error = RouteEditor.Apply(Proxy(upstream, Balancing(extra, policy, path, interval, status)), route);

        Assert.Contains(reason, error);
        Assert.Empty(route.ExtraUpstreams);
    }

    [Fact]
    public void Eleven_upstreams_are_too_many()
    {
        var extra = Enumerable.Range(1, 10).Select(i => $"b{i}:80").ToArray();

        Assert.Contains("at most", RouteEditor.Apply(Proxy("a:80", Balancing(extra)), new RouteConfig()));
    }

    [Fact]
    public void A_redirect_drops_them_and_the_api_keeps_them()
    {
        var route = new RouteConfig();
        var redirect = new RouteForm("a.example.com", false, true, null, false, "https://x.example.com", false, null, null, null, true, null, false, null,
            Options: new RouteExtras(false, false, false, false, [], Balancing: Balancing(["b:80"], path: "/health")));
        Assert.Null(RouteEditor.Apply(redirect, route));
        Assert.Empty(route.ExtraUpstreams);
        Assert.Null(route.HealthPath);

        Assert.Null(RouteEditor.Apply(Proxy("a:80", Balancing([" b:80 ", ""], "first", " /health ", 15, 204)), route));
        Assert.Equal(["b:80"], route.ExtraUpstreams);
        Assert.Equal("/health", route.HealthPath);
        var api = new RouteForm("a.example.com", false, false, "c:80", false, null, false, null, null, null, true, null, false, null);
        Assert.Null(RouteEditor.Apply(api, route));
        Assert.Equal(["b:80"], route.ExtraUpstreams);
        Assert.Equal("first", route.LoadBalancing);
        Assert.Equal(204, route.HealthStatus);
    }

    [Fact]
    public void A_stored_route_that_breaks_the_rules_is_not_served()
    {
        _store.UpsertRoute(new RouteConfig { Host = "bad.example.com", Upstream = "a:80", HealthPath = "/{env.SECRET}" }, null);

        Assert.Equal(503, Assert.Single(HandlersOf("bad.example.com"))!["status_code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("200", 200)]
    [InlineData("2xx", 2)]
    [InlineData(" 5XX ", 5)]
    [InlineData("99", null)]
    [InlineData("6xx", null)]
    [InlineData("ok", null)]
    public void The_expected_status_is_a_code_or_a_class(string text, int? stored) => Assert.Equal(stored, RouteBalancing.ParseStatus(text));

    // ---- The health Matcad checks for the routes list ------------------------------------------------------

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Asked) Asked.Add(request.RequestUri!.ToString());
            return Task.FromResult(answer(request));
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task The_health_service_asks_every_upstream_and_repeats_after_the_interval()
    {
        var handler = new StubHandler(r => r.RequestUri!.Host switch
        {
            "a" => new HttpResponseMessage(HttpStatusCode.NoContent),
            "b" => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            _ => throw new HttpRequestException("refused", null, HttpStatusCode.ServiceUnavailable),
        });
        var time = new ManualTime();
        var service = new UpstreamHealthService(_store, new StubFactory(handler), time, NullLogger<UpstreamHealthService>.Instance);
        _store.UpsertRoute(new RouteConfig { Host = "h.example.com", Upstream = "http://a:8080", ExtraUpstreams = ["b:80", "c:80"], HealthPath = "/health", HealthIntervalSeconds = 30 }, null);
        _store.UpsertRoute(new RouteConfig { Host = "unchecked.example.com", Upstream = "d:80" }, null);
        var route = _store.Routes.Single(r => r.Host == "h.example.com");

        Assert.Null(service.For(route));
        await service.CheckDueAsync(CancellationToken.None);

        var health = service.For(route)!;
        Assert.Equal(1, health.HealthyCount);
        Assert.Equal([true, false, false], health.Upstreams.Select(u => u.Healthy));
        Assert.Equal(500, health.Upstreams[1].Status);
        Assert.NotNull(health.Upstreams[2].Error);
        Assert.Equal(["http://a:8080/health", "http://b/health", "http://c/health"], handler.Asked.Order()); // :80 is the default port

        time.Now += TimeSpan.FromSeconds(10);
        await service.CheckDueAsync(CancellationToken.None);
        Assert.Equal(3, handler.Asked.Count); // not due yet
        time.Now += TimeSpan.FromSeconds(25);
        await service.CheckDueAsync(CancellationToken.None);
        Assert.Equal(6, handler.Asked.Count);

        // A changed route starts over: the old result does not belong to it.
        var changed = _store.Routes.Single(r => r.Host == "h.example.com");
        changed.ExtraUpstreams = ["b:80"];
        _store.UpsertRoute(changed, null);
        Assert.Null(service.For(_store.Routes.Single(r => r.Host == "h.example.com")));
    }
}

/// <summary>Several upstreams through the route page, the routes list and the REST API.</summary>
public sealed class LoadBalancingPageTests(MatcadFactory factory) : IClassFixture<MatcadFactory>
{
    private ConfigStore Store => factory.Services.GetRequiredService<ConfigStore>();

    private async Task<Browser> SignedInAsync()
    {
        var auth = factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("lb-user") == null)
            await auth.CreateUser("lb-user", null, "lb-user-password", Matcad.Data.UserRole.User, null);
        var settings = Store.Settings;
        settings.SetupCompleted = true;
        Store.SaveSettings(settings);
        var browser = new Browser(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("lb-user", "lb-user-password")).StatusCode);
        return browser;
    }

    private static Dictionary<string, string> Form(string host, string extra, string path, string status) => new()
    {
        ["Host"] = host, ["RouteType"] = "single", ["Target"] = "proxy", ["Upstream"] = "a:80", ["Enabled"] = "true",
        ["ExtraUpstreamsText"] = extra, ["LoadBalancing"] = "least_conn", ["HealthPath"] = path, ["HealthIntervalSeconds"] = "15", ["HealthStatus"] = status,
    };

    [Fact]
    public async Task The_page_saves_them_and_the_list_shows_the_upstreams_and_the_check()
    {
        var browser = await SignedInAsync();

        var response = await browser.PostFormAsync("/Routes/Edit", "/Routes/Edit", Form("lb-page.example.com", "b:80\r\n\r\nc:80", "/health", "2xx"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var route = Store.Routes.Single(r => r.Host == "lb-page.example.com");
        Assert.Equal(["b:80", "c:80"], route.ExtraUpstreams);
        Assert.Equal("least_conn", route.LoadBalancing);
        Assert.Equal(2, route.HealthStatus);
        Assert.Equal(15, route.HealthIntervalSeconds);
        var (_, html) = await browser.GetAsync($"/Routes/Edit?id={route.Id}");
        Assert.Matches("b:80(\n|&#xA;)c:80</textarea>", html.Replace("\r", ""));
        Assert.Contains("value=\"2xx\"", html);
        var (_, list) = await browser.GetAsync("/Routes?q=lb-page");
        Assert.Contains(">+2</span>", list);
        Assert.Contains("title=\"Health check /health\"", list); // checking, or the result once Matcad asked
    }

    [Fact]
    public async Task The_page_refuses_a_bad_status_and_saves_nothing()
    {
        var browser = await SignedInAsync();

        var response = await browser.PostFormAsync("/Routes/Edit", "/Routes/Edit", Form("lb-bad.example.com", "", "/health", "ok"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("expected status", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(Store.Routes, r => r.Host == "lb-bad.example.com");
    }

    [Fact]
    public async Task The_api_keeps_them_and_answers_in_the_old_shape()
    {
        Store.UpsertRoute(new RouteConfig { Host = "lb-api.example.com", Upstream = "a:80", Enabled = true, ExtraUpstreams = ["b:80"], HealthPath = "/up" }, null);
        var api = factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);

        var response = await api.PostAsync("/api/v1/routes", new StringContent("""{ "host": "lb-api.example.com", "upstream": "c:80", "enabled": true }""", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var route = Store.Routes.Single(r => r.Host == "lb-api.example.com");
        Assert.Equal(["c:80", "b:80"], route.AllUpstreams);
        Assert.Equal("/up", route.HealthPath);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["route"]!;
        Assert.Equal("c:80", body["upstream"]!.GetValue<string>());
        Assert.Null(body["extraUpstreams"]);
    }
}
