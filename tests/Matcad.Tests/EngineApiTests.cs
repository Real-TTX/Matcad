using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Matcad.Tests;

/// <summary>
/// The engine API that Mattiverse drives (<c>/api/v1</c>, <c>Matcad__ApiKey</c>, Mattiverse docs/ENGINES.md) is unchanged by the platform contract:
/// the same key, the same status codes, the same fields.
/// </summary>
public sealed class EngineApiTests(PlatformFactory site) : IClassFixture<PlatformFactory>
{
    private static List<string> Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToList();

    [Fact]
    public async Task Status_keeps_its_shape()
    {
        var response = await site.Api(PlatformFactory.EngineKey).GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["ok", "app", "version", "apiVersion", "capabilities", "baseDomain", "setupCompleted", "counts"], Names(doc.RootElement));
        Assert.Equal(["providers", "routes", "authentications", "allRoutes"], Names(doc.RootElement.GetProperty("counts")));
        Assert.Equal("matcad", doc.RootElement.GetProperty("app").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("apiVersion").GetInt32());
    }

    [Fact]
    public async Task Only_the_engine_key_opens_it()
    {
        foreach (var key in new[] { null, "wrong", PlatformFactory.PlatformKey })
        {
            var response = await site.Api(key).GetAsync("/api/v1/status");
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("Invalid or missing X-Api-Key.", doc.RootElement.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task Routes_settings_and_certificates_keep_their_shape()
    {
        var api = site.Api(PlatformFactory.EngineKey);
        var post = await api.PostAsJsonAsync("/api/v1/routes", new
        {
            host = "pin.example.com",
            target = "proxy",
            upstream = "http://pin:8080",
            enabled = true,
            name = "Pin",
            managedBy = "mattiverse",
        });
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        using (var doc = JsonDocument.Parse(await post.Content.ReadAsStringAsync()))
        {
            Assert.Equal(["route", "applied"], Names(doc.RootElement));
            Assert.Equal(
                ["id", "name", "host", "wildcard", "upstream", "insecureSkipVerify", "fallbackUrl", "redirectPermanent", "authenticationId",
                 "providerId", "acmeEmail", "enabled", "allowEmbedding", "listenPort", "upstreamHost", "upstreamHeaders", "requireHeader", "allowedIps",
                 "managedBy"],
                Names(doc.RootElement.GetProperty("route")));
            Assert.Equal(["ok", "error"], Names(doc.RootElement.GetProperty("applied")));
        }

        using (var doc = JsonDocument.Parse(await api.GetStringAsync("/api/v1/routes")))
        {
            var route = doc.RootElement.EnumerateArray().Single(r => r.GetProperty("host").GetString() == "pin.example.com");
            Assert.Equal(
                ["id", "name", "host", "wildcard", "upstream", "fallbackUrl", "authenticationId", "providerId", "enabled", "allowEmbedding",
                 "listenPort", "upstreamHost", "upstreamHeaders", "requireHeader", "allowedIps", "managedBy", "source", "sourceDetail", "editable", "certKind", "certSubject"],
                Names(route));
        }

        using (var doc = JsonDocument.Parse(await api.GetStringAsync("/api/v1/settings")))
        {
            Assert.Equal(
                ["baseDomain", "acmeEmail", "matcadHost", "portalMode", "acmePropagationDelaySeconds", "acmePropagationTimeoutSeconds", "setupCompleted"],
                Names(doc.RootElement));
        }

        using (var doc = JsonDocument.Parse(await api.GetStringAsync("/api/v1/certificates")))
        {
            Assert.Equal(["wildcards", "individual", "internalHosts"], Names(doc.RootElement));
        }

        var bad = await api.PostAsJsonAsync("/api/v1/routes", new { host = "", enabled = true });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        using (var doc = JsonDocument.Parse(await bad.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Host is required.", doc.RootElement.GetProperty("error").GetString());
        }
    }
}
