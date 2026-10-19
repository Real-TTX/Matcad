using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matcad.Config;
using Matcad.Pages.Authentications;
using Matcad.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

/// <summary>What the review of 2026-10 found: secrets that reached a page, placeholders in a redirect, unchecked addresses.</summary>
public sealed class ReviewSecurityTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConfigStore _store;
    private readonly CaddyConfigGenerator _generator;

    public ReviewSecurityTests()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Matcad:ConfigDir"] = _folder }).Build();
        _store = new ConfigStore(cfg, NullLogger<ConfigStore>.Instance);
        _generator = new CaddyConfigGenerator(_store, new RouteProvider(_store, new DockerRouteCache(), cfg), cfg);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ }
    }

    private string Json() => JsonSerializer.Serialize(_generator.Build(), new JsonSerializerOptions { WriteIndented = true });

    private static RouteForm Redirect(string url) =>
        new("old.example.com", false, true, null, false, url, false, null, null, null, true, null, false, null);

    [Theory]
    [InlineData("https://example.com/{env.HOME}")]
    [InlineData("https://example.com/{file./etc/passwd}")]
    [InlineData("https://example.com/a b")]
    public void A_redirect_target_with_a_placeholder_or_a_space_is_refused(string url) =>
        Assert.NotNull(RouteEditor.Apply(Redirect(url), new RouteConfig()));

    [Fact]
    public void A_stored_redirect_with_braces_reaches_Caddy_percent_encoded()
    {
        _store.UpsertRoute(new RouteConfig { Host = "old.example.com", FallbackUrl = "https://example.com/{env.HOME}" }, null);
        _store.UpsertRoute(new RouteConfig { Host = "imp.example.com", FallbackUrl = "https://example.com{http.request.uri}" }, null);

        var json = Json();

        Assert.DoesNotContain("{env.HOME}", json);
        Assert.Contains("https://example.com/%7Benv.HOME%7D", json);
        Assert.Contains("https://example.com{http.request.uri}", json); // what the Caddyfile import makes of redir ...{uri}
    }

    [Fact]
    public void The_config_shown_on_a_page_hides_dns_credentials_and_basic_auth_hashes()
    {
        var provider = new ProviderConfig { Name = "cf", Type = "cloudflare", Credentials = new() { ["api_token"] = "cf-token-very-secret" } };
        _store.UpsertProvider(provider, null);
        var auth = new AuthenticationConfig { Name = "b", Type = AuthType.BasicAuth, Users = [new() { Username = "u", PasswordHash = BCrypt.Net.BCrypt.HashPassword("pw", 4) }] };
        _store.UpsertAuthentication(auth, null);
        _store.UpsertRoute(new RouteConfig { Host = "*.example.com", Wildcard = true, ProviderId = provider.Id, Upstream = "a:80" }, null);
        _store.UpsertRoute(new RouteConfig { Host = "b.example.com", Upstream = "b:80", AuthenticationId = auth.Id }, null);

        var raw = Json();
        var shown = SecretRedaction.Redact(raw);

        Assert.Contains("cf-token-very-secret", raw);
        Assert.DoesNotContain("cf-token-very-secret", shown);
        Assert.Contains("\"name\": \"cloudflare\"", shown);
        Assert.DoesNotContain(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(auth.Users[0].PasswordHash)), shown);
        Assert.Contains("\"username\": \"u\"", shown);
    }

    [Fact]
    public void A_credential_called_name_cannot_replace_the_dns_module()
    {
        var provider = new ProviderConfig { Name = "x", Type = "netcup", Credentials = new() { ["name"] = "evil", ["api_key"] = "k" } };
        _store.UpsertProvider(provider, null);
        _store.UpsertRoute(new RouteConfig { Host = "*.example.com", Wildcard = true, ProviderId = provider.Id, Upstream = "a:80" }, null);

        var dns = JsonNode.Parse(Json())!["apps"]!["tls"]!["automation"]!["policies"]![0]!["issuers"]![0]!["challenges"]!["dns"]!["provider"]!;

        Assert.Equal("netcup", dns["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("netcup", "api_key", null)]
    [InlineData("Cloud flare", "api_key", "module")]
    [InlineData("cloudflare", "api-key", "credential key")]
    [InlineData("cloudflare", "name", "reserved")]
    public void Provider_types_and_keys_are_names_Caddy_can_take(string module, string key, string? error)
    {
        var result = ProviderRules.Check(module, [key]);
        if (error == null) Assert.Null(result);
        else Assert.Contains(error, result);
    }

    [Fact]
    public void A_blank_secret_keeps_the_stored_value_of_the_same_type_only()
    {
        var stored = new ProviderConfig { Type = "netcup", Credentials = new() { ["api_key"] = "old", ["customer_number"] = "1" } };

        var kept = ProviderRules.KeepStored("netcup", new() { ["api_key"] = "", ["customer_number"] = "2" }, stored);
        var otherType = ProviderRules.KeepStored("cloudflare", new() { ["api_key"] = "" }, stored);

        Assert.Equal("old", kept["api_key"]);
        Assert.Equal("2", kept["customer_number"]);
        Assert.Equal("", otherType["api_key"]);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("https://cdn.example.com/logo.png", true)]
    [InlineData("/auth/img/logo.svg", true)]
    [InlineData("data:image/png;base64,iVBORw0KGgo=", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=", false)]
    [InlineData("//evil.example.com/x.png", false)]
    [InlineData("https://a.example.com/x.png\" onerror=\"x", false)]
    public void The_logo_of_a_login_page_is_an_image_address(string value, bool ok) =>
        Assert.Equal(ok, EditModel.IsLogoAddress(value));
}

/// <summary>The same through the running app: pages and API.</summary>
public sealed class ReviewSecurityPageTests(MatcadFactory factory) : IClassFixture<MatcadFactory>
{
    private async Task<Browser> AdminAsync()
    {
        var auth = factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("review-admin") == null)
            await auth.CreateUser("review-admin", null, "review-admin-password", Matcad.Data.UserRole.Admin, null);
        var store = factory.Services.GetRequiredService<ConfigStore>();
        var settings = store.Settings;
        settings.SetupCompleted = true;
        store.SaveSettings(settings);
        var browser = new Browser(factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("review-admin", "review-admin-password")).StatusCode);
        return browser;
    }

    [Fact]
    public async Task The_provider_page_never_shows_a_stored_secret_and_an_empty_field_keeps_it()
    {
        var store = factory.Services.GetRequiredService<ConfigStore>();
        var provider = new ProviderConfig { Name = "page-dns", Type = "netcup", Credentials = new() { ["customer_number"] = "12345", ["api_key"] = "key-very-secret", ["api_password"] = "pw-very-secret" } };
        store.UpsertProvider(provider, null);
        var custom = new ProviderConfig { Name = "page-custom", Type = "duckdns", Credentials = new() { ["api_token"] = "duck-very-secret" } };
        store.UpsertProvider(custom, null);
        var browser = await AdminAsync();

        var (_, page) = await browser.GetAsync($"/Providers/Edit?id={provider.Id}");
        var (_, customPage) = await browser.GetAsync($"/Providers/Edit?id={custom.Id}");
        var (_, settings) = await browser.GetAsync("/Settings?tab=caddy");

        Assert.DoesNotContain("key-very-secret", page);
        Assert.DoesNotContain("pw-very-secret", page);
        Assert.Contains("12345", page); // the customer number is no secret
        Assert.Contains("Saved - leave empty to keep", page);
        Assert.DoesNotContain("duck-very-secret", customPage);
        Assert.DoesNotContain("very-secret", settings);

        var response = await browser.PostFormAsync($"/Providers/Edit?id={provider.Id}", $"/Providers/Edit?id={provider.Id}", new()
        {
            ["Id"] = provider.Id.ToString(), ["Name"] = "page-dns", ["Type"] = "netcup",
            ["cred_netcup_customer_number"] = "12345", ["cred_netcup_api_key"] = "", ["cred_netcup_api_password"] = "pw-new-secret",
        });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var saved = store.Providers.Single(p => p.Id == provider.Id).Credentials;
        Assert.Equal("key-very-secret", saved["api_key"]);
        Assert.Equal("pw-new-secret", saved["api_password"]);
    }

    [Fact]
    public async Task The_api_refuses_a_provider_that_could_not_be_a_Caddy_module()
    {
        var api = factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);

        var bad = await api.PostAsJsonAsync("/api/v1/providers", new { name = "x", type = "netcup", credentials = new Dictionary<string, string> { ["name"] = "other" } });
        var good = await api.PostAsJsonAsync("/api/v1/providers", new { name = "y", type = "netcup", credentials = new Dictionary<string, string> { ["api_key"] = "k" } });

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        using var doc = JsonDocument.Parse(await good.Content.ReadAsStringAsync());
        Assert.Equal(["id", "name", "type", "credentials", "managedBy"], doc.RootElement.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task A_login_portal_address_with_a_path_is_refused()
    {
        var browser = await AdminAsync();
        var store = factory.Services.GetRequiredService<ConfigStore>();

        await browser.PostFormAsync("/Settings", "/Settings", new() { ["AuthPortalUrl"] = "javascript:alert(1)", ["PortalMode"] = "redirect", ["LogRetentionDays"] = "30" });

        Assert.NotEqual("javascript:alert(1)", store.Settings.AuthPortalUrl);
    }
}
