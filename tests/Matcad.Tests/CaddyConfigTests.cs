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
    private readonly DockerRouteCache _docker = new();

    public CaddyConfigTests()
    {
        _cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Matcad:ConfigDir"] = _folder }).Build();
        _store = new ConfigStore(_cfg, NullLogger<ConfigStore>.Instance);
        _generator = new CaddyConfigGenerator(_store, new RouteProvider(_store, _docker, _cfg), _cfg);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    private JsonNode Build() => JsonSerializer.SerializeToNode(_generator.Build())!;

    // The site routes: without the leading HTTP -> HTTPS redirect (its own tests look at it).
    private static List<JsonNode> MainRoutes(JsonNode config) =>
        AllMainRoutes(config).Where(r => r!["match"]?[0]?["protocol"] == null).Select(r => r!).ToList();

    private static JsonArray AllMainRoutes(JsonNode config) => config["apps"]!["http"]!["servers"]!["matcad"]!["routes"]!.AsArray();

    private static IEnumerable<string> Handlers(JsonNode route) => route["handle"]!.AsArray().Select(h => h!["handler"]!.GetValue<string>());

    private static JsonNode RouteFor(JsonNode config, string host) =>
        MainRoutes(config).Single(r => r!["match"]![0]!["host"]![0]!.GetValue<string>() == host && r["match"]![0]!["path"] == null)!;

    [Fact]
    public void Plain_http_to_a_known_host_is_sent_to_https_before_any_site_route()
    {
        _store.UpsertAuthentication(new AuthenticationConfig { Name = "portal", Type = AuthType.Matcad }, null);
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1", AuthenticationId = _store.Authentications.Single().Id }, null);
        _store.UpsertRoute(new RouteConfig { Host = "old.example.com", FallbackUrl = "https://new.example.com" }, null);
        _store.UpsertRoute(new RouteConfig { Host = "*.example.com", Wildcard = true, Upstream = "b:1" }, null);
        _store.UpsertRoute(new RouteConfig { Host = "off.example.com", Upstream = "c:1", Enabled = false }, null);
        _store.UpsertRoute(new RouteConfig { ListenPort = 8100, Upstream = "d:1" }, null);

        var config = Build();
        var first = AllMainRoutes(config)[0]!;

        // First of all, so neither a site route nor the login pages (/auth/*) answer plain HTTP themselves.
        Assert.Equal("http", first["match"]![0]!["protocol"]!.GetValue<string>());
        Assert.Equal(["app.example.com", "old.example.com", "*.example.com"], first["match"]![0]!["host"]!.AsArray().Select(h => h!.GetValue<string>()));
        Assert.True(first["terminal"]!.GetValue<bool>());
        var handler = first["handle"]![0]!;
        Assert.Equal(308, handler["status_code"]!.GetValue<int>());
        Assert.Equal("https://{http.request.host}{http.request.uri}", handler["headers"]!["Location"]![0]!.GetValue<string>());
        // A port binding is plain HTTP on purpose and keeps its own server.
        Assert.DoesNotContain("protocol", config["apps"]!["http"]!["servers"]!["port_8100"]!.ToJsonString());
    }

    [Fact]
    public void Without_host_routes_there_is_no_redirect_and_unknown_hosts_stay_after_it()
    {
        Assert.Empty(AllMainRoutes(Build()));

        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1" }, null);
        var settings = _store.Settings;
        settings.UnknownHost = "notfound";
        _store.SaveSettings(settings);
        var caddy = new CaddyService(new TestHttpClientFactory(), _generator, _store, _cfg, NullLogger<CaddyService>.Instance);
        var routes = AllMainRoutes(JsonNode.Parse(caddy.BuildJson())!);

        Assert.Equal("http", routes[0]!["match"]![0]!["protocol"]!.GetValue<string>());
        Assert.Null(routes[^1]!["match"]); // the unknown-host answer is not in the list of the redirect, so plain HTTP reaches it
    }

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

    // ---- Guards and upstream properties --------------------------------------------------------------------------

    private const string Secret = "edge-secret-1234567890";

    private static JsonNode Guard(JsonNode route) => route["handle"]![0]!;

    [Fact]
    public void A_required_header_is_the_first_handler_and_refuses_with_403_and_no_body()
    {
        _store.UpsertAuthentication(new AuthenticationConfig { Name = "portal", Type = AuthType.Matcad }, null);
        _store.UpsertRoute(new RouteConfig
        {
            Host = "app.example.com", Upstream = "a:1", AllowEmbedding = true, AuthenticationId = _store.Authentications.Single().Id,
            RequireHeader = new HeaderPair { Name = "x-edge", Value = Secret }
        }, null);

        var route = RouteFor(Build(), "app.example.com");

        // Before the framing strip, before the authentication, before the proxy.
        Assert.Equal(["subroute", "headers", "reverse_proxy", "reverse_proxy"], Handlers(route));
        var refusal = Guard(route)["routes"]!.AsArray().Single()!;
        var matcher = refusal["match"]![0]!["not"]![0]!["header"]!;
        Assert.Equal(Secret, matcher["X-Edge"]![0]!.GetValue<string>()); // written the way the HTTP stack names it
        Assert.Single(matcher.AsObject());
        var answer = refusal["handle"]![0]!;
        Assert.Equal("static_response", answer["handler"]!.GetValue<string>());
        Assert.Equal(403, answer["status_code"]!.GetValue<int>());
        Assert.Null(answer["body"]);
        Assert.True(refusal["terminal"]!.GetValue<bool>());
    }

    [Fact]
    public void Allowed_ips_refuse_every_peer_that_is_not_in_the_list()
    {
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1", AllowedIps = ["10.0.0.0/8", "203.0.113.7", "fd00::/8"] }, null);

        var route = RouteFor(Build(), "app.example.com");

        Assert.Equal(["subroute", "reverse_proxy"], Handlers(route));
        var refusal = Guard(route)["routes"]!.AsArray().Single()!;
        var ranges = refusal["match"]![0]!["not"]![0]!["remote_ip"]!["ranges"]!.AsArray().Select(r => r!.GetValue<string>());
        Assert.Equal(["10.0.0.0/8", "203.0.113.7", "fd00::/8"], ranges);
        Assert.Equal(403, refusal["handle"]![0]!["status_code"]!.GetValue<int>());
    }

    [Fact]
    public void With_both_guards_a_request_has_to_get_past_both()
    {
        _store.UpsertRoute(new RouteConfig
        {
            Host = "app.example.com", Upstream = "a:1", AllowedIps = ["10.0.0.0/8"],
            RequireHeader = new HeaderPair { Name = "X-Edge", Value = Secret }
        }, null);

        var refusals = Guard(RouteFor(Build(), "app.example.com"))["routes"]!.AsArray();

        // Two refusals in one subroute: a wrong address OR a missing header ends the request with 403.
        Assert.Equal(2, refusals.Count);
        Assert.NotNull(refusals[0]!["match"]![0]!["not"]![0]!["remote_ip"]);
        Assert.NotNull(refusals[1]!["match"]![0]!["not"]![0]!["header"]);
        Assert.All(refusals, r => Assert.Equal(403, r!["handle"]![0]!["status_code"]!.GetValue<int>()));
    }

    [Fact]
    public void A_route_without_guards_is_generated_as_before()
    {
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "http://backend:8080" }, null);

        var proxy = RouteFor(Build(), "app.example.com")["handle"]!.AsArray().Single()!;

        Assert.Null(proxy["headers"]);
        Assert.Null(proxy["transport"]);
        Assert.Equal("json", Build()["logging"]!["logs"]!["access"]!["encoder"]!["format"]!.GetValue<string>());
    }

    [Fact]
    public void A_redirect_can_be_guarded_too()
    {
        _store.UpsertRoute(new RouteConfig { Host = "old.example.com", FallbackUrl = "https://new.example.com", RequireHeader = new HeaderPair { Name = "X-Edge", Value = Secret } }, null);

        var handlers = RouteFor(Build(), "old.example.com")["handle"]!.AsArray();

        Assert.Equal(["subroute", "static_response"], handlers.Select(h => h!["handler"]!.GetValue<string>()));
        Assert.Equal(302, handlers[1]!["status_code"]!.GetValue<int>());
    }

    [Fact]
    public void A_port_bound_route_and_a_docker_route_use_the_same_chain_and_so_the_same_guard()
    {
        _store.UpsertRoute(new RouteConfig { Name = "port 8100", ListenPort = 8100, Upstream = "http://app:80", AllowedIps = ["10.0.0.0/8"] }, null);
        _docker.Set([new RouteConfig
        {
            Host = "box.example.com", Upstream = "http://box:80", Source = "docker", SourceDetail = "box",
            RequireHeader = new HeaderPair { Name = "X-Edge", Value = Secret }
        }]);

        var config = Build();

        var portHandlers = config["apps"]!["http"]!["servers"]!["port_8100"]!["routes"]![0]!["handle"]!.AsArray();
        Assert.Equal(["subroute", "reverse_proxy"], portHandlers.Select(h => h!["handler"]!.GetValue<string>()));
        Assert.NotNull(portHandlers[0]!["routes"]![0]!["match"]![0]!["not"]![0]!["remote_ip"]);
        Assert.Equal(["subroute", "reverse_proxy"], Handlers(RouteFor(config, "box.example.com")));
    }

    [Fact]
    public void The_login_pages_of_a_guarded_host_are_behind_the_guard_as_well()
    {
        _store.UpsertAuthentication(new AuthenticationConfig { Name = "portal", Type = AuthType.Matcad }, null);
        _store.UpsertRoute(new RouteConfig
        {
            Host = "app.example.com", Upstream = "a:1", AuthenticationId = _store.Authentications.Single().Id,
            RequireHeader = new HeaderPair { Name = "X-Edge", Value = Secret }
        }, null);

        var loginRoute = MainRoutes(Build())[0]!;

        Assert.Equal("/auth/*", loginRoute["match"]![0]!["path"]![0]!.GetValue<string>());
        Assert.Equal(["subroute", "reverse_proxy"], Handlers(loginRoute));
    }

    [Fact]
    public void The_host_header_and_the_tls_server_name_follow_the_upstream_host()
    {
        _store.UpsertRoute(new RouteConfig { Host = "a.example.com", Upstream = "https://node:443", UpstreamHost = "Site.Example.org:8443", InsecureSkipVerify = true }, null);
        _store.UpsertRoute(new RouteConfig { Host = "b.example.com", Upstream = "http://node:80", UpstreamHost = "site.example.org" }, null);
        var config = Build();

        var secure = RouteFor(config, "a.example.com")["handle"]![0]!;
        Assert.Equal("site.example.org:8443", secure["headers"]!["request"]!["set"]!["Host"]![0]!.GetValue<string>());
        Assert.Equal("site.example.org", secure["transport"]!["tls"]!["server_name"]!.GetValue<string>()); // no port in the TLS name
        Assert.True(secure["transport"]!["tls"]!["insecure_skip_verify"]!.GetValue<bool>());

        var plain = RouteFor(config, "b.example.com")["handle"]![0]!;
        Assert.Equal("site.example.org", plain["headers"]!["request"]!["set"]!["Host"]![0]!.GetValue<string>());
        Assert.Null(plain["transport"]); // nothing to say about TLS to a plain http upstream
    }

    [Fact]
    public void Upstream_headers_are_set_under_their_canonical_name_so_they_replace_what_the_visitor_sent()
    {
        _store.UpsertRoute(new RouteConfig
        {
            Host = "app.example.com", Upstream = "a:1",
            UpstreamHeaders = [new HeaderPair { Name = "x-up", Value = "one" }, new HeaderPair { Name = "X-FORWARDED-PROTO", Value = "https" }]
        }, null);

        var set = RouteFor(Build(), "app.example.com")["handle"]![0]!["headers"]!["request"]!["set"]!.AsObject();

        Assert.Equal(["X-Up", "X-Forwarded-Proto"], set.Select(p => p.Key));
        Assert.Equal("one", set["X-Up"]![0]!.GetValue<string>());
    }

    [Fact]
    public void The_required_header_is_removed_from_the_request_before_it_goes_to_the_upstream()
    {
        _store.UpsertRoute(new RouteConfig { Host = "a.example.com", Upstream = "a:1", RequireHeader = new HeaderPair { Name = "x-edge", Value = Secret } }, null);
        _store.UpsertRoute(new RouteConfig
        {
            Host = "b.example.com", Upstream = "https://node:443", UpstreamHost = "site.example.org", RequireHeader = new HeaderPair { Name = "X-Edge", Value = Secret },
            UpstreamHeaders = [new HeaderPair { Name = "X-Up", Value = "up-secret-value-1234" }]
        }, null);
        var config = Build();

        // After the guard, in the same headers object as the Host and the headers that are set.
        var alone = RouteFor(config, "a.example.com")["handle"]![1]!["headers"]!["request"]!;
        Assert.Equal(["X-Edge"], alone["delete"]!.AsArray().Select(h => h!.GetValue<string>()));
        Assert.Null(alone["set"]);
        var merged = RouteFor(config, "b.example.com")["handle"]![1]!["headers"]!["request"]!;
        Assert.Equal(["X-Edge"], merged["delete"]!.AsArray().Select(h => h!.GetValue<string>()));
        Assert.Equal(["Host", "X-Up"], merged["set"]!.AsObject().Select(p => p.Key));
    }

    [Fact]
    public void A_stored_route_whose_upstream_header_has_the_name_of_the_required_header_is_not_served()
    {
        // The editor refuses it (Caddy deletes after it sets, so the set value would vanish); a backup could still carry it.
        _store.UpsertRoute(new RouteConfig
        {
            Host = "a.example.com", Upstream = "a:1", RequireHeader = new HeaderPair { Name = "X-Edge", Value = Secret },
            UpstreamHeaders = [new HeaderPair { Name = "x-edge", Value = "another-secret-1234" }]
        }, null);

        var handlers = RouteFor(Build(), "a.example.com")["handle"]!.AsArray();

        Assert.Equal(["subroute", "static_response"], handlers.Select(h => h!["handler"]!.GetValue<string>()));
        Assert.Equal(503, handlers[1]!["status_code"]!.GetValue<int>());
    }

    [Fact]
    public void A_guard_that_cannot_be_built_never_leaves_the_route_open()
    {
        // Values the editor refuses can still arrive through a backup or a hand-edited file.
        var broken = new[]
        {
            new RouteConfig { Host = "a.example.com", RequireHeader = new HeaderPair { Name = "X-Edge", Value = "short" } },
            new RouteConfig { Host = "b.example.com", RequireHeader = new HeaderPair { Name = "X-Edge", Value = "wildcard-*-secret-1234" } },
            new RouteConfig { Host = "c.example.com", RequireHeader = new HeaderPair { Name = "", Value = Secret } },
            new RouteConfig { Host = "d.example.com", RequireHeader = new HeaderPair { Name = "Host", Value = Secret } },
            new RouteConfig { Host = "e.example.com", AllowedIps = ["not an address"] },
            new RouteConfig { Host = "f.example.com", AllowedIps = [" ", ""] }, // a list that came out empty must not mean "no list"
        };
        foreach (var r in broken) { r.Upstream = "a:1"; _store.UpsertRoute(r, null); }

        var config = Build();

        foreach (var r in broken)
        {
            var only = Assert.Single(RouteFor(config, r.Host)["handle"]!.AsArray());
            Assert.Equal("static_response", only!["handler"]!.GetValue<string>());
            Assert.Equal(503, only["status_code"]!.GetValue<int>());
            Assert.DoesNotContain(Secret, only.ToJsonString());
        }
    }

    [Fact]
    public void Invalid_upstream_settings_are_not_served_either_and_the_guard_still_answers_first()
    {
        _store.UpsertRoute(new RouteConfig
        {
            Host = "a.example.com", Upstream = "a:1", AllowedIps = ["10.0.0.0/8"],
            UpstreamHeaders = [new HeaderPair { Name = "Host", Value = "evil.example" }]
        }, null);
        _store.UpsertRoute(new RouteConfig { Host = "b.example.com", Upstream = "a:1", UpstreamHost = "bad host" }, null);
        var config = Build();

        var guarded = RouteFor(config, "a.example.com")["handle"]!.AsArray();
        Assert.Equal(["subroute", "static_response"], guarded.Select(h => h!["handler"]!.GetValue<string>()));
        Assert.Equal(503, guarded[1]!["status_code"]!.GetValue<int>());
        Assert.Equal(503, Assert.Single(RouteFor(config, "b.example.com")["handle"]!.AsArray())!["status_code"]!.GetValue<int>());
    }

    [Fact]
    public void A_guard_answers_before_the_503_of_a_missing_authentication()
    {
        _store.UpsertRoute(new RouteConfig { Host = "app.example.com", Upstream = "a:1", AuthenticationId = 99, AllowedIps = ["10.0.0.0/8"] }, null);

        var handlers = RouteFor(Build(), "app.example.com")["handle"]!.AsArray();

        Assert.Equal(["subroute", "static_response"], handlers.Select(h => h!["handler"]!.GetValue<string>()));
        Assert.Equal(503, handlers[1]!["status_code"]!.GetValue<int>());
    }

    [Fact]
    public void The_access_log_does_not_record_the_secret_of_a_required_header()
    {
        _store.UpsertRoute(new RouteConfig { Host = "a.example.com", Upstream = "a:1", RequireHeader = new HeaderPair { Name = "x-edge", Value = Secret } }, null);
        _store.UpsertRoute(new RouteConfig { Host = "b.example.com", Upstream = "a:1", RequireHeader = new HeaderPair { Name = "X-Edge", Value = Secret } }, null);
        _store.UpsertRoute(new RouteConfig { Host = "c.example.com", Upstream = "a:1", RequireHeader = new HeaderPair { Name = "X-Other-Secret", Value = Secret } }, null);
        _store.UpsertRoute(new RouteConfig { Host = "off.example.com", Upstream = "a:1", Enabled = false, RequireHeader = new HeaderPair { Name = "X-Disabled", Value = Secret } }, null);

        var encoder = Build()["logging"]!["logs"]!["access"]!["encoder"]!;

        Assert.Equal("filter", encoder["format"]!.GetValue<string>());
        Assert.Equal("json", encoder["wrap"]!["format"]!.GetValue<string>());
        var fields = encoder["fields"]!.AsObject();
        Assert.Equal(["request>headers>X-Edge", "request>headers>X-Other-Secret"], fields.Select(f => f.Key));
        Assert.All(fields, f => Assert.Equal("delete", f.Value!["filter"]!.GetValue<string>()));
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
