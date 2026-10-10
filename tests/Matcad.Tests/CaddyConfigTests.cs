using System.Text.Json;
using System.Text.Json.Nodes;
using Matcad.Config;
using Matcad.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

public sealed class CaddyConfigTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly IConfiguration _cfg;
    private readonly ConfigStore _store;
    private readonly CaddyConfigGenerator _generator;

    public CaddyConfigTests()
    {
        _cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Matcad:ConfigDir"] = _folder }).Build();
        _store = new ConfigStore(_cfg, NullLogger<ConfigStore>.Instance);
        _generator = new CaddyConfigGenerator(_store, new RouteProvider(_store, new DockerRouteCache(), _cfg), _cfg);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    private JsonNode Build() => JsonSerializer.SerializeToNode(_generator.Build())!;

    private static JsonArray MainRoutes(JsonNode config) => config["apps"]!["http"]!["servers"]!["matcad"]!["routes"]!.AsArray();

    private static IEnumerable<string> Handlers(JsonNode route) => route["handle"]!.AsArray().Select(h => h!["handler"]!.GetValue<string>());

    private static JsonNode RouteFor(JsonNode config, string host) =>
        MainRoutes(config).Single(r => r!["match"]![0]!["host"]![0]!.GetValue<string>() == host && r["match"]![0]!["path"] == null)!;

    [Fact]
    public void A_proxy_route_dials_the_upstream_and_a_redirect_answers_itself()
    {
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "http://backend:8080" }, null);
        _store.UpsertRoute(new RouteConfig { Host = "old.example.com", FallbackUrl = "https://new.example.com", RedirectPermanent = true }, null);

        var config = Build();

        var proxy = RouteFor(config, "app.example.com")["handle"]![0]!;
        Assert.Equal("reverse_proxy", proxy["handler"]!.GetValue<string>());
        Assert.Equal("backend:8080", proxy["upstreams"]![0]!["dial"]!.GetValue<string>());
        var redirect = RouteFor(config, "old.example.com")["handle"]![0]!;
        Assert.Equal(301, redirect["status_code"]!.GetValue<int>());
        Assert.Equal("https://new.example.com", redirect["headers"]!["Location"]![0]!.GetValue<string>());
    }

    [Fact]
    public void An_https_upstream_gets_a_tls_transport_and_can_skip_verification()
    {
        _store.UpsertRoute(new RouteConfig { Host = "a.example.com", Upstream = "https://nas:5001", InsecureSkipVerify = true }, null);
        _store.UpsertRoute(new RouteConfig { Host = "b.example.com", Upstream = "nas" }, null);

        var config = Build();

        var tls = RouteFor(config, "a.example.com")["handle"]![0]!["transport"]!["tls"]!;
        Assert.True(tls["insecure_skip_verify"]!.GetValue<bool>());
        Assert.Equal("nas:80", RouteFor(config, "b.example.com")["handle"]![0]!["upstreams"]![0]!["dial"]!.GetValue<string>());
        Assert.Null(RouteFor(config, "b.example.com")["handle"]![0]!["transport"]);
    }

    [Fact]
    public void Disabled_routes_are_left_out_and_specific_hosts_come_before_wildcards()
    {
        _store.UpsertRoute(new RouteConfig { Host = "*.example.com", Wildcard = true, Upstream = "a:1" }, null);
        _store.UpsertRoute(new RouteConfig { Host = "off.example.com", Upstream = "a:1", Enabled = false }, null);
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1" }, null);

        var hosts = MainRoutes(Build()).Select(r => r!["match"]![0]!["host"]![0]!.GetValue<string>()).ToList();

        Assert.Equal(["app.example.com", "*.example.com"], hosts);
    }

    [Fact]
    public void Basic_auth_hashes_go_to_caddy_base64_encoded()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword("pw", 4);
        _store.UpsertAuthentication(new AuthenticationConfig { Id = 0, Name = "internal", Type = AuthType.BasicAuth, Users = [new BasicAuthUser { Username = "bob", PasswordHash = hash }] }, null);
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1", AuthenticationId = _store.Authentications.Single().Id }, null);

        var handlers = RouteFor(Build(), "app.example.com")["handle"]!.AsArray();

        Assert.Equal(["authentication", "reverse_proxy"], handlers.Select(h => h!["handler"]!.GetValue<string>()));
        var account = handlers[0]!["providers"]!["http_basic"]!["accounts"]![0]!;
        Assert.Equal("bob", account["username"]!.GetValue<string>());
        Assert.Equal(hash, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(account["password"]!.GetValue<string>())));
    }

    [Fact]
    public void A_route_whose_authentication_is_gone_is_never_served_open()
    {
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1", AuthenticationId = 99 }, null);

        var handlers = RouteFor(Build(), "app.example.com")["handle"]!.AsArray();

        var only = Assert.Single(handlers);
        Assert.Equal("static_response", only!["handler"]!.GetValue<string>());
        Assert.Equal(503, only["status_code"]!.GetValue<int>());
    }

    [Fact]
    public void Basic_auth_without_users_is_never_served_open_either()
    {
        _store.UpsertAuthentication(new AuthenticationConfig { Name = "empty", Type = AuthType.BasicAuth }, null);
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1", AuthenticationId = _store.Authentications.Single().Id }, null);

        var only = Assert.Single(RouteFor(Build(), "app.example.com")["handle"]!.AsArray());

        Assert.Equal("static_response", only!["handler"]!.GetValue<string>());
    }

    [Fact]
    public void A_matcad_login_asks_matcad_and_serves_the_login_pages_on_the_same_host_in_inline_mode()
    {
        _store.UpsertAuthentication(new AuthenticationConfig { Name = "portal", Type = AuthType.Matcad }, null);
        var id = _store.Authentications.Single().Id;
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1", AuthenticationId = id }, null);

        var routes = MainRoutes(Build());

        Assert.Equal(2, routes.Count);
        Assert.Equal("/auth/*", routes[0]!["match"]![0]!["path"]![0]!.GetValue<string>()); // before the protected route
        var verify = routes[1]!["handle"]![0]!;
        Assert.Equal("/auth/verify", verify["rewrite"]!["uri"]!.GetValue<string>());
        var forwarded = verify["headers"]!["request"]!["set"]!;
        Assert.Equal(id.ToString(), forwarded["X-Matcad-Auth"]![0]!.GetValue<string>());
        // With the port, so that after the login the visitor lands on the address they came from (dev stacks use 8443).
        Assert.Equal("{http.request.hostport}", forwarded["X-Forwarded-Host"]![0]!.GetValue<string>());
    }

    [Fact]
    public void A_wildcard_with_a_provider_gets_a_dns_challenge_policy()
    {
        _store.UpsertProvider(new ProviderConfig { Name = "netcup", Type = "netcup", Credentials = new() { ["api_key"] = "k" } }, null);
        _store.UpsertRoute(new RouteConfig { Host = "*.example.com", Wildcard = true, Upstream = "a:1", ProviderId = _store.Providers.Single().Id, AcmeEmail = "me@example.org" }, null);
        _store.UpsertRoute(new RouteConfig { Host = "example.com", Upstream = "a:1" }, null);
        var settings = _store.Settings;
        settings.AcmePropagationDelaySeconds = 120;
        settings.AcmePropagationTimeoutSeconds = -1;
        _store.SaveSettings(settings);

        var policy = Build()["apps"]!["tls"]!["automation"]!["policies"]![0]!;

        Assert.Equal(["*.example.com", "example.com"], policy["subjects"]!.AsArray().Select(s => s!.GetValue<string>()));
        var issuer = policy["issuers"]![0]!;
        Assert.Equal("me@example.org", issuer["email"]!.GetValue<string>());
        var dns = issuer["challenges"]!["dns"]!;
        Assert.Equal("netcup", dns["provider"]!["name"]!.GetValue<string>());
        Assert.Equal("k", dns["provider"]!["api_key"]!.GetValue<string>());
        Assert.Equal("120s", dns["propagation_delay"]!.GetValue<string>());
        Assert.Equal(-1, dns["propagation_timeout"]!.GetValue<int>());
    }

    [Fact]
    public void A_placeholder_acme_email_on_example_com_is_dropped()
    {
        _store.UpsertProvider(new ProviderConfig { Name = "p", Type = "netcup" }, null);
        _store.UpsertRoute(new RouteConfig { Host = "*.example.com", Wildcard = true, Upstream = "a:1", ProviderId = _store.Providers.Single().Id, AcmeEmail = "admin@example.com" }, null);

        var issuer = Build()["apps"]!["tls"]!["automation"]!["policies"]![0]!["issuers"]![0]!;

        Assert.Null(issuer["email"]);
    }

    [Fact]
    public void A_port_bound_route_gets_its_own_listener_without_host_matching()
    {
        _store.UpsertRoute(new RouteConfig { Name = "port 8100", ListenPort = 8100, Upstream = "http://app:80", AllowEmbedding = true }, null);

        var server = Build()["apps"]!["http"]!["servers"]!["port_8100"]!;

        Assert.Equal(":8100", server["listen"]![0]!.GetValue<string>());
        var handlers = server["routes"]![0]!["handle"]!.AsArray();
        Assert.Equal(["headers", "reverse_proxy"], handlers.Select(h => h!["handler"]!.GetValue<string>()));
        Assert.Empty(MainRoutes(Build())); // and nothing on the main server
    }

    [Fact]
    public void Raw_json_is_merged_into_the_generated_config()
    {
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1" }, null);
        var settings = _store.Settings;
        settings.RawCaddyJson = """{ "apps": { "http": { "servers": { "matcad": { "routes": [ { "terminal": true } ] } } } }, "extra": 1 }""";
        _store.SaveSettings(settings);
        var caddy = new CaddyService(new TestHttpClientFactory(), _generator, _store, _cfg, NullLogger<CaddyService>.Instance);

        var merged = JsonNode.Parse(caddy.BuildJson())!;

        Assert.Equal(2, MainRoutes(merged).Count); // arrays are joined
        Assert.Equal(1, merged["extra"]!.GetValue<int>());
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
