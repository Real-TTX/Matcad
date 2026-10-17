using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Matcad.Config;
using Matcad.Platform;
using MatPlatform;
using Microsoft.Extensions.DependencyInjection;

namespace Matcad.Tests;

/// <summary>The MatApiDefinition of Matcad is valid: keys, showIf, ids, and every icon in the sprite of MatUi.</summary>
public sealed class PlatformDefinitionTests
{
    private static readonly IReadOnlySet<string> Sprite =
        MatPlatformValidator.ReadSprite(Path.Combine(AppContext.BaseDirectory, "Fixtures", "matui-icons.svg"));

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void The_definition_is_valid_against_the_sprite(string language)
    {
        var definition = MatPlatformBuilder.Build(MatcadPlatform.Describe, language);

        Assert.Empty(MatPlatformValidator.Validate(definition, Sprite));
        Assert.Equal("matcad", definition.App.Id);
        Assert.Equal("#2f6feb", definition.App.Accent);
        Assert.InRange(definition.Configure.Count, 1, 10);
        Assert.Equal(["admin", "user"], definition.Roles!.Select(r => r.Key));
        Assert.Equal(["overview", "settings"], definition.Pages.Select(p => p.Id));
    }

    [Fact]
    public void The_texts_follow_the_language()
    {
        Assert.Equal("Overview", MatPlatformBuilder.Build(MatcadPlatform.Describe, "en").Pages[0].Label);
        Assert.Equal("Übersicht", MatPlatformBuilder.Build(MatcadPlatform.Describe, "de").Pages[0].Label);
    }
}

/// <summary>The endpoints of the contract: shapes, status codes, the key, nothing secret.</summary>
public sealed class PlatformApiTests(MatcadFactory site) : IClassFixture<MatcadFactory>
{
    private static readonly string[] StateKeys =
        ["routes", "upstreamErrors", "certificates", "certificateExpiry", "certificateErrors", "providers", "caddy"];

    [Fact]
    public async Task The_definition_is_anonymous_with_an_etag()
    {
        var client = site.Api(null);
        var response = await client.GetAsync("/api");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("contract").GetInt32());
        Assert.Equal("matcad", root.GetProperty("app").GetProperty("id").GetString());
        Assert.True(root.GetProperty("auth").GetProperty("apiKey").GetBoolean());
        Assert.False(root.GetProperty("auth").TryGetProperty("identity", out _));
        Assert.Equal(StateKeys, root.GetProperty("state").EnumerateArray().Select(s => s.GetProperty("key").GetString()));

        var etag = response.Headers.ETag!.Tag;
        using var again = new HttpRequestMessage(HttpMethod.Get, "/api");
        again.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);
    }

    [Fact]
    public async Task The_definition_speaks_german_when_asked()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api");
        request.Headers.AcceptLanguage.ParseAdd("de-DE");
        using var doc = JsonDocument.Parse(await (await site.Api(null).SendAsync(request)).Content.ReadAsStringAsync());
        Assert.Equal("Übersicht", doc.RootElement.GetProperty("pages")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Health_is_anonymous_and_names_the_checks()
    {
        var response = await site.Api(null).GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(doc.RootElement.GetProperty("status").GetString(), new[] { "ok", "degraded" });
        Assert.Equal("matcad", doc.RootElement.GetProperty("app").GetString());
        Assert.Contains(doc.RootElement.GetProperty("checks").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "database" && c.GetProperty("status").GetString() == "ok");

        // The old liveness probe is unchanged.
        var healthz = await site.Api(null).GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, healthz.StatusCode);
        Assert.Equal("ok", await healthz.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", "/api/state")]
    [InlineData("GET", "/api/state/routes")]
    [InlineData("GET", "/api/configure")]
    [InlineData("PUT", "/api/configure")]
    [InlineData("POST", "/api/actions/reloadCaddy")]
    public async Task Without_the_key_it_is_401(string method, string path)
    {
        foreach (var key in new[] { null, "wrong-key", MatcadFactory.EngineKey })
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path)
            {
                Content = method == "GET" ? null : new StringContent("{\"values\":{}}", Encoding.UTF8, "application/json"),
            };
            var response = await site.Api(key).SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    [Fact]
    public async Task State_has_every_tile()
    {
        var response = await site.Api().GetAsync("/api/state");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var values = doc.RootElement.GetProperty("values");
        foreach (var key in StateKeys)
        {
            Assert.True(values.TryGetProperty(key, out var entry), key);
            Assert.True(entry.TryGetProperty("value", out _), key);
        }

        Assert.Equal(0, values.GetProperty("upstreamErrors").GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Number, values.GetProperty("routes").GetProperty("value").ValueKind);
        Assert.Equal(JsonValueKind.String, values.GetProperty("caddy").GetProperty("value").ValueKind);
    }

    [Fact]
    public async Task State_counts_the_routes_providers_and_certificates()
    {
        var store = site.Services.GetRequiredService<ConfigStore>();
        store.UpsertProvider(new ProviderConfig { Name = "count-dns", Type = "netcup" }, null);
        store.UpsertRoute(new RouteConfig { Name = "count", Host = "count.example.org", Upstream = "http://app:80", Enabled = true }, null);

        using var doc = JsonDocument.Parse(await site.Api().GetStringAsync("/api/state"));
        var values = doc.RootElement.GetProperty("values");
        Assert.True(values.GetProperty("routes").GetProperty("value").GetInt32() >= 1);
        Assert.True(values.GetProperty("providers").GetProperty("value").GetInt32() >= 1);
        Assert.True(values.GetProperty("certificates").GetProperty("value").GetInt32() >= 1);
    }

    [Fact]
    public async Task One_state_value_and_404_for_an_unknown_key()
    {
        var one = await site.Api().GetAsync("/api/state/routes");
        Assert.Equal(HttpStatusCode.OK, one.StatusCode);
        using var doc = JsonDocument.Parse(await one.Content.ReadAsStringAsync());
        Assert.Equal("routes", doc.RootElement.GetProperty("key").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await site.Api().GetAsync("/api/state/nothing")).StatusCode);
    }

    [Fact]
    public async Task Configure_reads_and_writes_the_settings()
    {
        var get = await site.Api().GetAsync("/api/configure");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        using (var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync()))
        {
            var keys = doc.RootElement.GetProperty("values").EnumerateObject().Select(p => p.Name).Order().ToList();
            Assert.Equal(["acmeEmail", "baseDomain", "matcadHost", "portalMode"], keys);
        }

        var put = await site.Api().PutAsJsonAsync("/api/configure", new
        {
            values = new { acmeEmail = "ops@example.org", baseDomain = "Example.ORG", portalMode = "redirect" },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        using (var doc = JsonDocument.Parse(await put.Content.ReadAsStringAsync()))
        {
            var values = doc.RootElement.GetProperty("values");
            Assert.Equal("ops@example.org", values.GetProperty("acmeEmail").GetString());
            Assert.Equal("example.org", values.GetProperty("baseDomain").GetString());
            Assert.Equal("redirect", values.GetProperty("portalMode").GetString());
        }

        var settings = site.Services.GetRequiredService<ConfigStore>().Settings;
        Assert.Equal("ops@example.org", settings.AcmeEmail);
        Assert.Equal("redirect", settings.PortalMode);

        // The engine API sees the same settings.
        using var engine = JsonDocument.Parse(await site.Api(MatcadFactory.EngineKey).GetStringAsync("/api/v1/settings"));
        Assert.Equal("example.org", engine.RootElement.GetProperty("baseDomain").GetString());
    }

    [Fact]
    public async Task A_wrong_value_saves_nothing()
    {
        var before = site.Services.GetRequiredService<ConfigStore>().Settings.BaseDomain;

        var put = await site.Api().PutAsJsonAsync("/api/configure", new
        {
            values = new { baseDomain = "changed.example.net", acmeEmail = "not an address", portalMode = "open" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        using var doc = JsonDocument.Parse(await put.Content.ReadAsStringAsync());
        var errors = doc.RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("acmeEmail", out _));
        Assert.True(errors.TryGetProperty("portalMode", out _));
        Assert.Equal(before, site.Services.GetRequiredService<ConfigStore>().Settings.BaseDomain);

        var host = await site.Api().PutAsJsonAsync("/api/configure", new { values = new { matcadHost = "bad host/x" } });
        Assert.Equal(HttpStatusCode.BadRequest, host.StatusCode);
    }

    [Fact]
    public async Task No_secret_ever_comes_out()
    {
        var store = site.Services.GetRequiredService<ConfigStore>();
        store.UpsertProvider(new ProviderConfig
        {
            Name = "secret-dns",
            Type = "netcup",
            Credentials = new() { ["api_password"] = "provider-secret-4711" },
        }, null);

        foreach (var path in new[] { "/api", "/api/health", "/api/state", "/api/configure" })
        {
            var body = await site.Api().GetStringAsync(path);
            Assert.DoesNotContain("provider-secret-4711", body);
            Assert.DoesNotContain(MatcadFactory.PlatformKey, body);
            Assert.DoesNotContain(MatcadFactory.EngineKey, body);
            Assert.DoesNotContain("PasswordHash", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Actions_answer_or_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await site.Api().PostAsync("/api/actions/nothing", null)).StatusCode);
        // A link is not something to post.
        Assert.Equal(HttpStatusCode.NotFound, (await site.Api().PostAsync("/api/actions/openRoutes", null)).StatusCode);

        // Caddy is not reachable in the tests: the push cannot run now.
        var reload = await site.Api().PostAsync("/api/actions/reloadCaddy", null);
        Assert.Equal(HttpStatusCode.Conflict, reload.StatusCode);
        using (var doc = JsonDocument.Parse(await reload.Content.ReadAsStringAsync()))
        {
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("message").GetString()));
        }

        var check = await site.Api().PostAsync("/api/actions/checkCertificates", null);
        Assert.Contains(check.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
    }

    [Fact]
    public async Task The_platform_routes_do_not_touch_the_admin_session()
    {
        // A signed-in browser without the key gets 401 too: the contract ignores the session cookie.
        var browser = site.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await MatcadFactory.SignInLocallyAsync(browser, "admin", "admin")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/state")).StatusCode);
    }
}

/// <summary>Only <c>Matcad__ApiKey</c>: Mattiverse's one key opens the platform endpoints too.</summary>
public sealed class EngineKeyFallbackTests(EngineKeyOnlyFactory site) : IClassFixture<EngineKeyOnlyFactory>
{
    [Fact]
    public async Task The_engine_key_opens_the_platform_endpoints()
    {
        Assert.Equal(HttpStatusCode.OK, (await site.Api(MatcadFactory.EngineKey).GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.Api(MatcadFactory.EngineKey).GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api(MatcadFactory.PlatformKey).GetAsync("/api/state")).StatusCode);
    }
}

/// <summary>No key at all.</summary>
public sealed class NoKeyTests(NoKeyFactory site) : IClassFixture<NoKeyFactory>
{
    [Fact]
    public async Task The_platform_answers_401_and_the_engine_api_stays_off()
    {
        using var doc = JsonDocument.Parse(await site.Api(null).GetStringAsync("/api"));
        Assert.False(doc.RootElement.GetProperty("auth").GetProperty("apiKey").GetBoolean());

        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api("anything").GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await site.Api("anything").GetAsync("/api/v1/status")).StatusCode);
    }
}
