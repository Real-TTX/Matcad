using System.Net;
using System.Net.Http.Json;
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

// The access properties of a route (upstream host, upstream headers, required header, allowed IPs): what is stored and kept, what the
// config pages and the REST API show, and the secrets that must not come out anywhere. The rules for the values themselves are in
// RouteEditorTests, the generated Caddy JSON in CaddyConfigTests.

/// <summary>The Settings page shows the Caddy config to read and copy; the secrets in it are replaced there.</summary>
public class SecretRedactionTests
{
    // The routes of a guarded host as a real Caddy 2.11 answers for GET /config/ (key order is Caddy's), with made-up secrets;
    // the last route is the call a Matcad login makes to verify a session.
    private const string Live = """
        [
          { "handle": [
              { "handler": "subroute", "routes": [ { "handle": [ { "handler": "static_response", "status_code": 403 } ],
                  "match": [ { "not": [ { "header": { "X-Edge": [ "live-edge-secret-12345" ] } } ] } ], "terminal": true } ] },
              { "handler": "reverse_proxy",
                "headers": { "request": { "delete": [ "X-Edge" ], "set": { "Host": [ "inner.example" ], "X-Up": [ "live-up-secret-12345" ] } } },
                "upstreams": [ { "dial": "127.0.0.1:9001" } ] } ],
            "match": [ { "host": [ "guard.localhost" ] } ], "terminal": true },
          { "handle": [ { "handler": "reverse_proxy", "upstreams": [ { "dial": "matcad:4433" } ],
              "rewrite": { "method": "GET", "uri": "/auth/verify" },
              "headers": { "request": { "set": { "X-Matcad-Auth": [ "7" ], "X-Forwarded-Host": [ "{http.request.hostport}" ] } } },
              "handle_response": [ { "match": { "status_code": [ 2 ] }, "routes": [] } ] } ],
            "match": [ { "host": [ "other.localhost" ] } ] }
        ]
        """;

    [Fact]
    public void The_secrets_of_a_running_config_are_replaced_and_everything_else_is_left_alone()
    {
        var shown = SecretRedaction.Redact(Live);

        Assert.DoesNotContain("live-edge-secret", shown);
        Assert.DoesNotContain("live-up-secret", shown);
        Assert.Equal(2, Regex.Matches(shown, Regex.Escape(SecretRedaction.Hidden)).Count);
        Assert.Contains("X-Edge", shown);                  // the names stay: they say what is configured
        Assert.Contains("inner.example", shown);           // the Host is no secret
        Assert.Contains("{http.request.hostport}", shown); // the login check only passes request data on
        Assert.Contains("\"X-Matcad-Auth\"", shown);
    }

    [Fact]
    public void Text_that_is_not_json_is_not_passed_on()
    {
        var shown = SecretRedaction.Redact("a leaked live-edge-secret-12345 {");

        Assert.DoesNotContain("live-edge-secret", shown);
    }

    [Fact]
    public void The_generated_config_is_redacted_too()
    {
        var folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Matcad:ConfigDir"] = folder }).Build();
            var store = new ConfigStore(cfg, NullLogger<ConfigStore>.Instance);
            store.UpsertRoute(new RouteConfig
            {
                Host = "app.example.com", Upstream = "a:1", RequireHeader = new HeaderPair { Name = "X-Edge", Value = "generated-secret-1234" },
                UpstreamHeaders = [new HeaderPair { Name = "X-Up", Value = "generated-up-secret-1" }]
            }, null);
            var generator = new CaddyConfigGenerator(store, new RouteProvider(store, new DockerRouteCache(), cfg), cfg);
            var caddy = new CaddyService(new NoHttp(), generator, store, cfg, NullLogger<CaddyService>.Instance);

            Assert.Contains("generated-secret-1234", caddy.BuildJson()); // Caddy needs it ...
            var shown = SecretRedaction.Redact(caddy.BuildJson());       // ... a page does not show it

            Assert.DoesNotContain("generated-secret-1234", shown);
            Assert.DoesNotContain("generated-up-secret-1", shown);
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { /* temp folder */ }
        }
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}

/// <summary>Files and backups from before the access properties, and the ones with them.</summary>
public sealed class RouteGuardPersistenceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);

    private ConfigStore Open() => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Matcad:ConfigDir"] = _folder }).Build(),
        NullLogger<ConfigStore>.Instance);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    private static readonly JsonSerializerOptions RestoreOptions = new() { PropertyNameCaseInsensitive = true };

    private static RouteConfig Guarded() => new()
    {
        Host = "app.example.com", Upstream = "https://node:443", UpstreamHost = "site.example.org",
        UpstreamHeaders = [new HeaderPair { Name = "X-Up", Value = "up-secret-value-1234" }],
        RequireHeader = new HeaderPair { Name = "X-Edge", Value = "edge-secret-1234567890" },
        AllowedIps = ["10.0.0.0/8", "fd00::/8"]
    };

    [Fact]
    public void A_routes_file_from_before_the_access_properties_loads_with_them_switched_off()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "routes.json"),
            """[ { "Id": 1, "Name": "old", "Host": "old.example.com", "Upstream": "http://a:1", "Enabled": true, "AllowEmbedding": false } ]""");

        var route = Assert.Single(Open().Routes);

        Assert.Null(route.UpstreamHost);
        Assert.Empty(route.UpstreamHeaders);
        Assert.Null(route.RequireHeader);
        Assert.Empty(route.AllowedIps);
        Assert.False(route.HasGuards);
    }

    [Fact]
    public void A_hand_edited_file_with_nulls_cannot_break_the_lists()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "routes.json"),
            """[ { "Id": 1, "Host": "a.example.com", "UpstreamHeaders": null, "AllowedIps": null, "RequireHeader": null } ]""");

        var route = Assert.Single(Open().Routes);

        Assert.Empty(route.UpstreamHeaders);
        Assert.Empty(route.AllowedIps);
        Assert.False(route.HasGuards);
    }

    [Fact]
    public void The_access_properties_survive_a_restart()
    {
        Open().UpsertRoute(Guarded(), null);

        var route = Assert.Single(Open().Routes);

        Assert.Equal("site.example.org", route.UpstreamHost);
        Assert.Equal(("X-Up", "up-secret-value-1234"), (route.UpstreamHeaders.Single().Name, route.UpstreamHeaders.Single().Value));
        Assert.Equal(("X-Edge", "edge-secret-1234567890"), (route.RequireHeader!.Name, route.RequireHeader.Value));
        Assert.Equal(["10.0.0.0/8", "fd00::/8"], route.AllowedIps);
        Assert.True(route.HasGuards);
    }

    [Fact]
    public void A_backup_carries_them_and_an_older_backup_without_them_reads_fine()
    {
        var backup = new BackupData { Routes = [Guarded()] };

        var json = JsonSerializer.Serialize(backup, new JsonSerializerOptions { WriteIndented = true });
        var back = JsonSerializer.Deserialize<BackupData>(json, RestoreOptions)!.Routes.Single();

        // The file is the one place a secret goes (like the provider credentials and the password hashes in it): it has to be restorable.
        Assert.Equal("edge-secret-1234567890", back.RequireHeader!.Value);
        Assert.Equal("up-secret-value-1234", back.UpstreamHeaders.Single().Value);
        Assert.Equal(["10.0.0.0/8", "fd00::/8"], back.AllowedIps);
        Assert.Equal("site.example.org", back.UpstreamHost);

        var old = """{ "Version": 1, "Settings": {}, "Providers": [], "Authentications": [], "Users": [], "Routes": [ { "Id": 3, "Host": "old.example.com", "Upstream": "a:1", "Enabled": true } ] }""";
        var oldRoute = JsonSerializer.Deserialize<BackupData>(old, RestoreOptions)!.Routes.Single();
        Assert.False(oldRoute.HasGuards);
        Assert.Null(oldRoute.UpstreamHost);
    }

    [Fact]
    public void Example_data_and_a_restored_route_without_guards_serve_as_before()
    {
        var store = Open();
        ExampleData.Seed(store, null);

        Assert.All(store.Routes, r => Assert.False(r.HasGuards));
        Assert.All(store.Routes, r => Assert.Null(r.UpstreamHost));
    }

    [Fact]
    public void A_guard_in_a_caddyfile_is_kept_as_raw_passthrough_instead_of_being_imported_without_it()
    {
        const string adapted = """
            { "apps": { "http": { "servers": { "srv0": { "listen": [":443"], "routes": [ { "match": [ { "host": [ "a.example.com" ] } ], "terminal": true,
              "handle": [ { "handler": "subroute", "routes": [
                { "match": [ { "not": [ { "header": { "X-Edge": [ "secret" ] } } ] } ], "handle": [ { "handler": "static_response", "status_code": 403 } ] },
                { "handle": [ { "handler": "reverse_proxy", "upstreams": [ { "dial": "b:80" } ] } ] } ] } ] } ] } } } } }
            """;

        var plan = new CaddyfileImporter().Analyze(adapted);

        Assert.Empty(plan.Routes);
        Assert.Contains("X-Edge", plan.RawRemainder);
    }

    [Fact]
    public void An_imported_route_has_no_access_properties()
    {
        const string adapted = """
            { "apps": { "http": { "servers": { "srv0": { "listen": [":443"], "routes": [ { "match": [ { "host": [ "a.example.com" ] } ], "terminal": true,
              "handle": [ { "handler": "reverse_proxy", "upstreams": [ { "dial": "b:80" } ] } ] } ] } } } } }
            """;

        var route = Assert.Single(new CaddyfileImporter().Analyze(adapted).Routes).Route;

        Assert.False(route.HasGuards);
        Assert.Null(route.UpstreamHost);
        Assert.Empty(route.UpstreamHeaders);
    }
}

/// <summary>The REST API: the new fields of POST /routes and how they are shown.</summary>
public class RouteGuardApiTests : IClassFixture<MatcadFactory>
{
    private const string EdgeSecret = "edge-secret-1234567890";
    private const string UpSecret = "up-secret-value-1234";

    private readonly MatcadFactory _factory;
    private readonly HttpClient _api;

    public RouteGuardApiTests(MatcadFactory factory)
    {
        _factory = factory;
        _api = factory.CreateClient();
        _api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);
    }

    private ConfigStore Store => _factory.Services.GetRequiredService<ConfigStore>();

    private RouteConfig Stored(string host) => Store.Routes.Single(r => r.Host == host);

    /// <summary>POSTs the JSON text as it is: a field that is missing, null or set cannot be told apart in an object that is serialised from a C# type.</summary>
    private async Task<(HttpStatusCode Status, string Body)> Post(string json)
    {
        var response = await _api.PostAsync("/api/v1/routes", new StringContent(json, Encoding.UTF8, "application/json"));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Body(string host, string extra = "", string upstream = "http://a:1") =>
        $$"""{ "host": "{{host}}", "target": "proxy", "upstream": "{{upstream}}", "enabled": true {{(extra.Length > 0 ? "," + extra : "")}} }""";

    private const string AllFour = $$"""
        "upstreamHost": "inner.example",
        "upstreamHeaders": [ { "name": "X-Up", "value": "{{UpSecret}}" } ],
        "requireHeader": { "name": "X-Edge", "value": "{{EdgeSecret}}" },
        "allowedIps": [ "10.0.0.0/8", "203.0.113.7" ]
        """;

    [Fact]
    public async Task The_status_announces_the_route_guards_capability()
    {
        var status = await _api.GetFromJsonAsync<JsonObject>("/api/v1/status");

        Assert.Equal(["routeGuards"], status!["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()));
        Assert.True(status["ok"]!.GetValue<bool>()); // and what was there is still there
        Assert.NotNull(status["counts"]);
    }

    [Fact]
    public async Task Every_response_shows_the_names_and_flags_but_never_a_secret_value()
    {
        var (status, created) = await Post(Body("shown.example", AllFour));
        Assert.Equal(HttpStatusCode.OK, status);
        var id = JsonNode.Parse(created)!["route"]!["id"]!.GetValue<long>();

        var responses = new[]
        {
            created,
            await _api.GetStringAsync("/api/v1/routes/manual"),
            await _api.GetStringAsync($"/api/v1/routes/{id}"),
            await _api.GetStringAsync("/api/v1/routes"),
        };

        foreach (var body in responses)
        {
            Assert.DoesNotContain(EdgeSecret, body);
            Assert.DoesNotContain(UpSecret, body);
            Assert.Contains("\"valueSet\":true", body);
        }
        var route = (await _api.GetFromJsonAsync<JsonObject>($"/api/v1/routes/{id}"))!;
        Assert.Equal("inner.example", route["upstreamHost"]!.GetValue<string>());
        Assert.Equal(["X-Up"], route["upstreamHeaders"]!.AsArray().Select(h => h!["name"]!.GetValue<string>()));
        Assert.True(route["upstreamHeaders"]![0]!["valueSet"]!.GetValue<bool>());
        Assert.Equal("X-Edge", route["requireHeader"]!["name"]!.GetValue<string>());
        Assert.True(route["requireHeader"]!["valueSet"]!.GetValue<bool>());
        Assert.Equal(["10.0.0.0/8", "203.0.113.7"], route["allowedIps"]!.AsArray().Select(a => a!.GetValue<string>()));
        // Nothing but name and flag, so there is no property that could carry the value.
        Assert.Equal(["name", "valueSet"], route["requireHeader"]!.AsObject().Select(p => p.Key));
        Assert.Equal(["name", "valueSet"], route["upstreamHeaders"]![0]!.AsObject().Select(p => p.Key));
        // The overview of everything Caddy serves carries the same safe fields.
        var overview = (await _api.GetFromJsonAsync<JsonArray>("/api/v1/routes"))!.Single(r => r!["host"]!.GetValue<string>() == "shown.example")!;
        Assert.Equal("X-Edge", overview["requireHeader"]!["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync("/api/v1/routes/987654")).StatusCode);
    }

    [Fact]
    public async Task A_route_without_the_fields_shows_them_empty()
    {
        await Post(Body("plain.example"));

        var route = (await _api.GetFromJsonAsync<JsonArray>("/api/v1/routes/manual"))!.Single(r => r!["host"]!.GetValue<string>() == "plain.example")!;

        Assert.Null(route["upstreamHost"]);
        Assert.Empty(route["upstreamHeaders"]!.AsArray());
        Assert.Null(route["requireHeader"]);
        Assert.Empty(route["allowedIps"]!.AsArray());
    }

    [Fact]
    public async Task A_missing_field_stays_null_removes_it_and_a_value_replaces_it()
    {
        await Post(Body("semantics.example", AllFour));

        // Missing: everything stays, whatever else the call says.
        Assert.Equal(HttpStatusCode.OK, (await Post(Body("semantics.example", upstream: "http://b:2"))).Status);
        var kept = Stored("semantics.example");
        Assert.Equal("http://b:2", kept.Upstream);
        Assert.Equal("inner.example", kept.UpstreamHost);
        Assert.Equal(UpSecret, kept.UpstreamHeaders.Single().Value);
        Assert.Equal(EdgeSecret, kept.RequireHeader!.Value);
        Assert.Equal(["10.0.0.0/8", "203.0.113.7"], kept.AllowedIps);

        // null removes one property and leaves the others.
        await Post(Body("semantics.example", "\"requireHeader\": null"));
        Assert.Null(Stored("semantics.example").RequireHeader);
        Assert.Equal(["10.0.0.0/8", "203.0.113.7"], Stored("semantics.example").AllowedIps);

        // A value replaces.
        await Post(Body("semantics.example", "\"allowedIps\": [ \"192.0.2.0/24\" ], \"requireHeader\": { \"name\": \"X-New\", \"value\": \"a-new-secret-value-1\" }"));
        var replaced = Stored("semantics.example");
        Assert.Equal(["192.0.2.0/24"], replaced.AllowedIps);
        Assert.Equal(("X-New", "a-new-secret-value-1"), (replaced.RequireHeader!.Name, replaced.RequireHeader.Value));
        Assert.Equal(UpSecret, replaced.UpstreamHeaders.Single().Value);

        // null and an empty list are both "none".
        await Post(Body("semantics.example", "\"upstreamHeaders\": [], \"allowedIps\": null, \"upstreamHost\": null"));
        var cleared = Stored("semantics.example");
        Assert.Empty(cleared.UpstreamHeaders);
        Assert.Empty(cleared.AllowedIps);
        Assert.Null(cleared.UpstreamHost);
        Assert.Equal("X-New", cleared.RequireHeader!.Name); // never mentioned, still there
    }

    [Fact]
    public async Task A_client_that_does_not_know_the_fields_cannot_wipe_a_guard_set_by_another()
    {
        await Post(Body("shared.example", AllFour));

        // What matOS sends: the old fields only, by host and (second call) by id.
        var again = await _api.PostAsJsonAsync("/api/v1/routes", new { host = "shared.example", wildcard = false, target = "proxy", upstream = "http://a:2", enabled = true, name = "Shared" });
        var id = (await again.Content.ReadFromJsonAsync<JsonObject>())!["route"]!["id"]!.GetValue<long>();
        await _api.PostAsJsonAsync("/api/v1/routes", new { id, host = "shared.example", target = "proxy", upstream = "http://a:3", enabled = true });

        var route = Stored("shared.example");
        Assert.Equal("http://a:3", route.Upstream);
        Assert.Equal(EdgeSecret, route.RequireHeader!.Value);
        Assert.Equal("inner.example", route.UpstreamHost);
        Assert.Equal(["10.0.0.0/8", "203.0.113.7"], route.AllowedIps);
        Assert.Equal(UpSecret, route.UpstreamHeaders.Single().Value);
    }

    [Fact]
    public async Task The_same_call_twice_is_one_route_with_one_guard()
    {
        var first = JsonNode.Parse((await Post(Body("twice.example", AllFour))).Body)!["route"]!["id"]!.GetValue<long>();
        var second = JsonNode.Parse((await Post(Body("twice.example", AllFour))).Body)!["route"]!["id"]!.GetValue<long>();

        Assert.Equal(first, second);
        Assert.Single(Store.Routes, r => r.Host == "twice.example");
        Assert.Single(Stored("twice.example").UpstreamHeaders);
        Assert.Equal(2, Stored("twice.example").AllowedIps.Count);
    }

    [Fact]
    public async Task A_header_without_a_value_keeps_the_saved_one_and_a_new_name_needs_one()
    {
        await Post(Body("echo.example", AllFour));

        // Read the route, send it back (the shape of the responses has no value): the values stay.
        var current = JsonNode.Parse(await _api.GetStringAsync("/api/v1/routes/manual"))!.AsArray().Single(r => r!["host"]!.GetValue<string>() == "echo.example")!;
        var echoed = $$"""{ "host": "echo.example", "upstream": "http://a:1", "enabled": true, "upstreamHeaders": {{current["upstreamHeaders"]!.ToJsonString()}}, "requireHeader": {{current["requireHeader"]!.ToJsonString()}} }""";
        Assert.Equal(HttpStatusCode.OK, (await Post(echoed)).Status);
        Assert.Equal(UpSecret, Stored("echo.example").UpstreamHeaders.Single().Value);
        Assert.Equal(EdgeSecret, Stored("echo.example").RequireHeader!.Value);

        var (status, body) = await Post(Body("echo.example", "\"upstreamHeaders\": [ { \"name\": \"X-Brand-New\" } ]"));
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("X-Brand-New", body);
        Assert.Equal("X-Up", Stored("echo.example").UpstreamHeaders.Single().Name);

        // Another name for the required header has no saved value to keep either.
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(Body("echo.example", "\"requireHeader\": { \"name\": \"X-Renamed\" }"))).Status);
        Assert.Equal("X-Edge", Stored("echo.example").RequireHeader!.Name);
    }

    [Theory]
    [InlineData("\"requireHeader\": { \"name\": \"X-Edge\", \"value\": \"short\" }", "Required header")]
    [InlineData("\"requireHeader\": { \"name\": \"X-Edge\", \"value\": \"star-*-secret-1234567\" }", "Required header")]
    [InlineData("\"requireHeader\": { \"name\": \"X-Edge\", \"value\": \"{http.request.host}-1234567\" }", "Required header")]
    [InlineData("\"requireHeader\": { \"name\": \"Host\", \"value\": \"a-long-enough-secret-1\" }", "Required header")]
    [InlineData("\"requireHeader\": { \"name\": \"X-Edge\", \"value\": \"a-long enough-secret-1\" }", "no spaces")]
    [InlineData("\"requireHeader\": { \"name\": \"X-Edge\", \"value\": \"a-long-enough-secret-1 \" }", "no spaces")]
    [InlineData("\"requireHeader\": { \"name\": \"X-Edge\", \"value\": \"a-long-enough-secret-\\u0001\" }", "visible ASCII")]
    [InlineData("\"upstreamHeaders\": [ { \"name\": \"X-A\", \"value\": \"line\\nbreak\" } ]", "control characters")]
    [InlineData("\"upstreamHeaders\": [ { \"name\": \"X-A\", \"value\": \" padded\" } ]", "space")]
    [InlineData("\"upstreamHeaders\": [ { \"name\": \"X-Edge\", \"value\": \"another-value-1234\" } ]", "cannot share a name")]
    [InlineData("\"requireHeader\": { \"name\": \"X Edge\", \"value\": \"a-long-enough-secret-1\" }", "Required header")]
    [InlineData("\"upstreamHeaders\": [ { \"name\": \"Content-Length\", \"value\": \"1\" } ]", "Upstream header")]
    [InlineData("\"upstreamHeaders\": [ { \"name\": \"X-A\", \"value\": \"1\" }, { \"name\": \"x-a\", \"value\": \"2\" } ]", "listed twice")]
    [InlineData("\"allowedIps\": [ \"10.0.0.0/8\", \"nope\" ]", "Allowed IPs")]
    [InlineData("\"upstreamHost\": \"http://x.example\"", "Upstream host")]
    [InlineData("\"upstreamHost\": 5", "upstreamHost")]
    [InlineData("\"upstreamHeaders\": { \"name\": \"X-A\" }", "upstreamHeaders")]
    [InlineData("\"upstreamHeaders\": [ { \"value\": \"x\" } ]", "upstreamHeaders")]
    [InlineData("\"upstreamHeaders\": [ { \"name\": \"X-A\", \"value\": 5 } ]", "upstreamHeaders")]
    [InlineData("\"requireHeader\": \"X-Edge\"", "requireHeader")]
    [InlineData("\"allowedIps\": \"10.0.0.0/8\"", "allowedIps")]
    [InlineData("\"allowedIps\": [ 5 ]", "allowedIps")]
    public async Task An_invalid_value_is_a_400_that_names_the_field_and_nothing_is_saved(string field, string expected)
    {
        var host = $"invalid-{Math.Abs(field.GetHashCode())}.example";
        await Post(Body(host, AllFour));
        var before = JsonSerializer.Serialize(Stored(host));

        var (status, body) = await Post(Body(host, field, upstream: "http://changed:9"));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains(expected, body);
        Assert.DoesNotContain("a-long-enough-secret", body); // the reason, never the value
        Assert.DoesNotContain("star-*-secret", body);
        Assert.DoesNotContain("http.request.host", body);
        Assert.Equal(before, JsonSerializer.Serialize(Stored(host))); // not even the part that was fine
    }

    [Fact]
    public async Task A_new_route_with_an_invalid_value_is_not_created()
    {
        var (status, _) = await Post(Body("never-created.example", "\"allowedIps\": [ \"nope\" ]"));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.DoesNotContain(Store.Routes, r => r.Host == "never-created.example");
    }

    [Fact]
    public async Task Upstream_properties_cannot_be_kept_on_a_route_that_becomes_a_redirect()
    {
        await Post(Body("becomes-redirect.example", AllFour));
        var redirect = """{ "host": "becomes-redirect.example", "target": "redirect", "fallbackUrl": "https://elsewhere.example", "enabled": true }""";

        var (status, body) = await Post(redirect);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("only apply to a reverse proxy", body);
        Assert.NotNull(Stored("becomes-redirect.example").Upstream);

        // Said explicitly it works, and the guards (which a redirect can have) stay.
        var cleared = redirect.Replace("\"enabled\": true", "\"enabled\": true, \"upstreamHost\": null, \"upstreamHeaders\": null");
        Assert.Equal(HttpStatusCode.OK, (await Post(cleared)).Status);
        var route = Stored("becomes-redirect.example");
        Assert.Null(route.UpstreamHost);
        Assert.Equal(EdgeSecret, route.RequireHeader!.Value);
    }

    [Fact]
    public async Task A_port_binding_can_be_guarded_and_the_config_carries_the_guard()
    {
        var body = """{ "name": "stack", "host": "", "listenPort": 8191, "upstream": "http://c:80", "enabled": true, "allowedIps": [ "10.0.0.0/8" ], "requireHeader": { "name": "X-Edge", "value": "edge-secret-1234567890" } }""";

        Assert.Equal(HttpStatusCode.OK, (await Post(body)).Status);
        Assert.Equal(HttpStatusCode.OK, (await Post(body)).Status); // idempotent as before

        var route = Assert.Single(Store.Routes, r => r.ListenPort == 8191);
        Assert.True(route.HasGuards);
        var config = JsonNode.Parse(_factory.Services.GetRequiredService<CaddyService>().BuildJson())!;
        var handlers = config["apps"]!["http"]!["servers"]!["port_8191"]!["routes"]![0]!["handle"]!.AsArray();
        Assert.Equal("subroute", handlers[0]!["handler"]!.GetValue<string>());
        Assert.Equal(403, handlers[0]!["routes"]![0]!["handle"]![0]!["status_code"]!.GetValue<int>());
    }
}

/// <summary>The route editor page, the list and the Settings page: values are never sent back, an empty value field keeps the saved value.</summary>
public class RouteGuardPageTests : IClassFixture<MatcadFactory>
{
    private const string EdgeSecret = "edge-secret-1234567890";
    private const string UpSecret = "up-secret-value-1234";
    private const string OtherSecret = "other-secret-value-12";

    private readonly MatcadFactory _factory;
    private readonly HttpClient _api;

    public RouteGuardPageTests(MatcadFactory factory)
    {
        _factory = factory;
        _api = factory.CreateClient();
        _api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);
    }

    private ConfigStore Store => _factory.Services.GetRequiredService<ConfigStore>();

    private async Task<Browser> SignedIn()
    {
        var auth = _factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("tester") == null)
            await auth.CreateUser("tester", null, "tester-password", Matcad.Data.UserRole.Admin, null);
        var settings = Store.Settings;
        settings.SetupCompleted = true;
        Store.SaveSettings(settings);
        var browser = new Browser(_factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("tester", "tester-password")).StatusCode);
        return browser;
    }

    private async Task<RouteConfig> CreateGuardedRoute(string host)
    {
        var json = $$"""
            { "host": "{{host}}", "target": "proxy", "upstream": "http://a:1", "enabled": true, "name": "Guarded",
              "upstreamHost": "inner.example",
              "upstreamHeaders": [ { "name": "X-Up", "value": "{{UpSecret}}" }, { "name": "X-Second", "value": "{{OtherSecret}}" } ],
              "requireHeader": { "name": "X-Edge", "value": "{{EdgeSecret}}" }, "allowedIps": [ "10.0.0.0/8", "203.0.113.7" ] }
            """;
        Assert.Equal(HttpStatusCode.OK, (await _api.PostAsync("/api/v1/routes", new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode);
        return Store.Routes.Single(r => r.Host == host);
    }

    /// <summary>The fields the edit page posts for a proxy route. Header rows and every other field that repeats are written as pairs.</summary>
    private static List<KeyValuePair<string, string>> EditForm(RouteConfig route, params (string Key, string Value)[] more)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("Id", route.Id.ToString()), new("Name", route.Name), new("Host", route.Host), new("RouteType", "single"),
            new("Target", "proxy"), new("Upstream", route.Upstream ?? ""), new("Enabled", "true"),
        };
        form.RemoveAll(p => more.Any(m => m.Key == p.Key)); // a single-valued field the caller sets replaces the default
        form.AddRange(more.Select(m => new KeyValuePair<string, string>(m.Key, m.Value)));
        return form;
    }

    private static string EditUrl(RouteConfig route) => $"/Routes/Edit?id={route.Id}";

    [Fact]
    public async Task The_editor_shows_names_and_a_set_hint_but_never_a_value()
    {
        var route = await CreateGuardedRoute("page-shown.example");
        var browser = await SignedIn();

        var (response, html) = await browser.GetAsync(EditUrl(route));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(EdgeSecret, html);
        Assert.DoesNotContain(UpSecret, html);
        Assert.DoesNotContain(OtherSecret, html);
        Assert.Contains("value=\"X-Up\"", html);
        Assert.Contains("value=\"X-Second\"", html);
        Assert.Contains("value=\"X-Edge\"", html);
        Assert.Contains("value=\"inner.example\"", html);
        Assert.Contains("203.0.113.7", html);
        Assert.Equal(3, Regex.Matches(html, "placeholder=\"Set - leave empty to keep\"").Count); // two headers and the required header
        Assert.Matches(@"<details class=""mat-collapse"" open", html); // something is set, so the section is open
        Assert.DoesNotMatch(@"type=""password""[^>]*\svalue=", html); // no password field carries a value
    }

    [Fact]
    public async Task The_editor_folds_the_section_away_when_nothing_is_set()
    {
        Assert.Equal(HttpStatusCode.OK, (await _api.PostAsync("/api/v1/routes", new StringContent("""{ "host": "page-plain.example", "upstream": "a:1", "enabled": true }""", Encoding.UTF8, "application/json"))).StatusCode);
        var browser = await SignedIn();

        var (_, html) = await browser.GetAsync(EditUrl(Store.Routes.Single(r => r.Host == "page-plain.example")));

        Assert.Matches(@"<details class=""mat-collapse""\s+id=""route-access""", html);
        Assert.DoesNotContain("Set - leave empty to keep", html);
    }

    [Fact]
    public async Task Saving_with_empty_value_fields_keeps_every_saved_value()
    {
        var route = await CreateGuardedRoute("page-keep.example");
        var browser = await SignedIn();

        var response = await browser.PostFormPairsAsync(EditUrl(route), EditUrl(route), EditForm(route,
            ("Name", "Renamed"), ("UpstreamHost", "inner.example"),
            ("HeaderNames", "X-Up"), ("HeaderValues", ""), ("HeaderNames", "X-Second"), ("HeaderValues", ""),
            ("RequireName", "X-Edge"), ("RequireValue", ""), ("AllowedIpsText", "10.0.0.0/8\n203.0.113.7")));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var saved = Store.Routes.Single(r => r.Id == route.Id);
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal(["X-Up", "X-Second"], saved.UpstreamHeaders.Select(h => h.Name));
        Assert.Equal([UpSecret, OtherSecret], saved.UpstreamHeaders.Select(h => h.Value));
        Assert.Equal(EdgeSecret, saved.RequireHeader!.Value);
        Assert.Equal("inner.example", saved.UpstreamHost);
        Assert.Equal(["10.0.0.0/8", "203.0.113.7"], saved.AllowedIps);
    }

    [Fact]
    public async Task A_typed_value_replaces_a_removed_row_is_deleted_and_an_empty_name_removes_the_guard()
    {
        var route = await CreateGuardedRoute("page-change.example");
        var browser = await SignedIn();

        // X-Up gets a new value, X-Second is gone from the form, a third header is new, the required header is cleared.
        var response = await browser.PostFormPairsAsync(EditUrl(route), EditUrl(route), EditForm(route,
            ("HeaderNames", "X-Up"), ("HeaderValues", "a-brand-new-up-value"), ("HeaderNames", "X-Third"), ("HeaderValues", "third-value"),
            ("RequireName", ""), ("RequireValue", ""), ("AllowedIpsText", "")));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var saved = Store.Routes.Single(r => r.Id == route.Id);
        Assert.Equal([("X-Up", "a-brand-new-up-value"), ("X-Third", "third-value")], saved.UpstreamHeaders.Select(h => (h.Name, h.Value)));
        Assert.Null(saved.RequireHeader);
        Assert.Empty(saved.AllowedIps);
        Assert.Null(saved.UpstreamHost); // the field was empty
        Assert.False(saved.HasGuards);
    }

    [Fact]
    public async Task An_empty_row_is_ignored_and_a_row_without_a_saved_value_asks_for_one()
    {
        var route = await CreateGuardedRoute("page-rows.example");
        var browser = await SignedIn();

        var ok = await browser.PostFormPairsAsync(EditUrl(route), EditUrl(route), EditForm(route,
            ("HeaderNames", ""), ("HeaderValues", ""), ("RequireName", "X-Edge"), ("RequireValue", "")));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Empty(Store.Routes.Single(r => r.Id == route.Id).UpstreamHeaders);

        // The saved required header is X-Edge: another name has no value to keep.
        var refused = await browser.PostFormPairsAsync(EditUrl(route), EditUrl(route), EditForm(route, ("RequireName", "X-Other-Name"), ("RequireValue", "")));
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("enter a value", await refused.Content.ReadAsStringAsync());
        Assert.Equal("X-Edge", Store.Routes.Single(r => r.Id == route.Id).RequireHeader!.Name);
    }

    [Fact]
    public async Task A_refused_form_changes_nothing_and_does_not_send_a_typed_value_back()
    {
        var route = await CreateGuardedRoute("page-refused.example");
        var browser = await SignedIn();
        const string typed = "typed-but-refused";

        var response = await browser.PostFormPairsAsync(EditUrl(route), EditUrl(route), EditForm(route,
            ("Name", "Must not stick"), ("HeaderNames", "X-Up"), ("HeaderValues", typed),
            ("RequireName", "X-Edge"), ("RequireValue", "short"), ("AllowedIpsText", "10.0.0.0/8")));

        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("at least 16 characters", html);
        Assert.DoesNotContain(typed, html);
        Assert.DoesNotContain("value=\"short\"", html);
        Assert.DoesNotContain(EdgeSecret, html);
        Assert.DoesNotContain(UpSecret, html);
        Assert.Matches(@"<details class=""mat-collapse"" open", html);
        var saved = Store.Routes.Single(r => r.Id == route.Id);
        Assert.Equal("Guarded", saved.Name);
        Assert.Equal(UpSecret, saved.UpstreamHeaders[0].Value);
        Assert.Equal(EdgeSecret, saved.RequireHeader!.Value);
        // The rows that had a saved value still say so, so a second try can leave them empty.
        Assert.Contains("placeholder=\"Set - leave empty to keep\"", html);
    }

    [Fact]
    public async Task A_bad_address_is_refused_with_the_entry_named()
    {
        var route = await CreateGuardedRoute("page-ips.example");
        var browser = await SignedIn();

        var response = await browser.PostFormPairsAsync(EditUrl(route), EditUrl(route), EditForm(route, ("AllowedIpsText", "10.0.0.0/8\nnot-an-ip")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("not-an-ip", await response.Content.ReadAsStringAsync());
        Assert.Equal(["10.0.0.0/8", "203.0.113.7"], Store.Routes.Single(r => r.Id == route.Id).AllowedIps);
    }

    [Fact]
    public async Task Switching_to_a_redirect_drops_what_is_said_to_the_upstream_and_keeps_the_guard()
    {
        var route = await CreateGuardedRoute("page-redirect.example");
        var browser = await SignedIn();

        var form = EditForm(route, ("FallbackUrl", "https://elsewhere.example"), ("UpstreamHost", "inner.example"),
            ("HeaderNames", "X-Up"), ("HeaderValues", ""), ("RequireName", "X-Edge"), ("RequireValue", ""), ("AllowedIpsText", "10.0.0.0/8"));
        form.RemoveAll(p => p.Key is "Target" or "Upstream");
        form.Add(new("Target", "redirect"));
        var response = await browser.PostFormPairsAsync(EditUrl(route), EditUrl(route), form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var saved = Store.Routes.Single(r => r.Id == route.Id);
        Assert.Null(saved.Upstream);
        Assert.Null(saved.UpstreamHost);
        Assert.Empty(saved.UpstreamHeaders);
        Assert.Equal(EdgeSecret, saved.RequireHeader!.Value);
        Assert.Equal(["10.0.0.0/8"], saved.AllowedIps);
    }

    [Fact]
    public async Task The_route_list_marks_a_guarded_route_and_says_nothing_about_the_values()
    {
        await CreateGuardedRoute("page-list.example");
        var browser = await SignedIn();

        var (_, html) = await browser.GetAsync("/Routes");

        Assert.Contains("Guarded", html);
        Assert.Contains("Refuses requests without the header X-Edge.", html);
        Assert.DoesNotContain(EdgeSecret, html);
        Assert.DoesNotContain(UpSecret, html);
    }

    [Fact]
    public async Task The_caddy_tab_of_the_settings_hides_the_secrets()
    {
        await CreateGuardedRoute("page-settings.example");
        var browser = await SignedIn();

        var (response, html) = await browser.GetAsync("/Settings?tab=caddy");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("page-settings.example", html);
        Assert.Contains(SecretRedaction.Hidden, html);
        Assert.DoesNotContain(EdgeSecret, html);
        Assert.DoesNotContain(UpSecret, html);
        Assert.DoesNotContain(OtherSecret, html);
    }
}

/// <summary>Backup and restore through the Settings page. Own app instance: a restore replaces all users and routes.</summary>
public class RouteGuardBackupTests : IClassFixture<MatcadFactory>
{
    private readonly MatcadFactory _factory;
    public RouteGuardBackupTests(MatcadFactory factory) => _factory = factory;

    [Fact]
    public async Task A_backup_takes_the_access_properties_along_and_an_older_backup_restores_without_them()
    {
        var store = _factory.Services.GetRequiredService<ConfigStore>();
        var auth = _factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        await auth.CreateUser("tester", null, "tester-password", Matcad.Data.UserRole.Admin, null);
        var settings = store.Settings;
        settings.SetupCompleted = true;
        store.SaveSettings(settings);
        store.UpsertRoute(new RouteConfig
        {
            Host = "backup.example.com", Upstream = "http://a:1", UpstreamHost = "inner.example",
            UpstreamHeaders = [new HeaderPair { Name = "X-Up", Value = "up-secret-value-1234" }],
            RequireHeader = new HeaderPair { Name = "X-Edge", Value = "edge-secret-1234567890" }, AllowedIps = ["10.0.0.0/8"]
        }, null);
        var browser = new Browser(_factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("tester", "tester-password")).StatusCode);

        // Export: the file carries the values (it is the backup), so it can be put back.
        var (export, backup) = await browser.GetAsync("/Settings?handler=Export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var exported = JsonNode.Parse(backup)!["Routes"]!.AsArray().Single(r => r!["Host"]!.GetValue<string>() == "backup.example.com")!;
        Assert.Equal("edge-secret-1234567890", exported["RequireHeader"]!["Value"]!.GetValue<string>());
        Assert.Equal("X-Up", exported["UpstreamHeaders"]![0]!["Name"]!.GetValue<string>());
        Assert.Equal("inner.example", exported["UpstreamHost"]!.GetValue<string>());
        Assert.Equal("10.0.0.0/8", exported["AllowedIps"]![0]!.GetValue<string>());

        // Restore it into a system where the route has been changed: the guard is back.
        store.UpsertRoute(new RouteConfig { Id = store.Routes.Single(r => r.Host == "backup.example.com").Id, Host = "backup.example.com", Upstream = "http://changed:1" }, null);
        Assert.False(store.Routes.Single(r => r.Host == "backup.example.com").HasGuards);
        Assert.Equal(HttpStatusCode.Redirect, (await Restore(browser, backup)).StatusCode);
        var restored = store.Routes.Single(r => r.Host == "backup.example.com");
        Assert.Equal("edge-secret-1234567890", restored.RequireHeader!.Value);
        Assert.Equal("up-secret-value-1234", restored.UpstreamHeaders.Single().Value);
        Assert.Equal("inner.example", restored.UpstreamHost);
        Assert.Equal(["10.0.0.0/8"], restored.AllowedIps);

        // A backup from before the properties existed: the same route restores with them off, and is served as before.
        var node = JsonNode.Parse(backup)!;
        foreach (var route in node["Routes"]!.AsArray().OfType<JsonObject>())
        {
            route.Remove("UpstreamHost"); route.Remove("UpstreamHeaders"); route.Remove("RequireHeader"); route.Remove("AllowedIps");
        }
        var login = new Browser(_factory);
        Assert.Equal(HttpStatusCode.Redirect, (await login.LoginAsync("tester", "tester-password")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Restore(login, node.ToJsonString())).StatusCode);
        var old = store.Routes.Single(r => r.Host == "backup.example.com");
        Assert.False(old.HasGuards);
        Assert.Null(old.UpstreamHost);
        Assert.Empty(old.UpstreamHeaders);
        Assert.Empty(old.AllowedIps);
        var only = JsonNode.Parse(_factory.Services.GetRequiredService<CaddyService>().BuildJson())!["apps"]!["http"]!["servers"]!["matcad"]!["routes"]![0]!["handle"]!.AsArray();
        Assert.Equal(["reverse_proxy"], only.Select(h => h!["handler"]!.GetValue<string>()));
    }

    private static async Task<HttpResponseMessage> Restore(Browser browser, string backup)
    {
        var (_, page) = await browser.GetAsync("/Settings?tab=system");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        var content = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes(backup)), "BackupFile", "backup.json" },
        };
        return await browser.Client.PostAsync("/Settings?handler=Restore", content);
    }
}
