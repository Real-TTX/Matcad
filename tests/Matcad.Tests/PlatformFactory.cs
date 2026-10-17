using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using MatPlatform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Matcad.Tests;

/// <summary>
/// The whole app in memory on its own empty data folder, for the platform contract. <see cref="EngineKey"/> is <c>Matcad__ApiKey</c> (the engine
/// API <c>/api/v1</c>), <see cref="PlatformKey"/> is <c>MAT_API_KEY</c>; with an <c>authority</c> the sign-in through MatIdentity is on, with a fixed
/// OpenID configuration so no MatIdentity has to run. Caddy is "unreachable" (its admin API points at a closed port), the setup wizard is done.
/// The settings reach Program through <c>UseSetting</c> (not environment variables), so these factories run in parallel with the others.
/// </summary>
public class PlatformFactory : WebApplicationFactory<Program>
{
    public const string EngineKey = "engine-key-0123456789";
    public const string PlatformKey = "platform-key-0123456789";
    public const string AdminPassword = "admin-password-0123";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N"));
    private readonly bool _engineKey;
    private readonly bool _platformKey;
    private readonly string? _authority;
    private readonly bool _reachable;

    public PlatformFactory()
        : this(engineKey: true, platformKey: true, authority: null)
    {
    }

    protected PlatformFactory(bool engineKey, bool platformKey, string? authority, bool reachable = true)
    {
        _engineKey = engineKey;
        _platformKey = platformKey;
        _authority = authority;
        _reachable = reachable;

        var config = Path.Combine(_data, "config");
        Directory.CreateDirectory(config);
        // A closed port: a push to Caddy fails at once instead of waiting for the name "caddy".
        File.WriteAllText(Path.Combine(config, "settings.json"),
            JsonSerializer.Serialize(new { SetupCompleted = true, CaddyAdminUrl = "http://127.0.0.1:9" }));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(WebHostDefaults.EnvironmentKey, "Production");
        builder.UseSetting("Matcad:DataDir", _data);
        builder.UseSetting("Matcad:ConfigDir", Path.Combine(_data, "config"));
        builder.UseSetting("Matcad:CaddyLogDir", Path.Combine(_data, "caddy-logs"));
        // "" rather than nothing: a variable of the same name in the environment of the test run must not leak in.
        builder.UseSetting("Matcad:ApiKey", _engineKey ? EngineKey : "");
        // The admin of a headless install (as Mattiverse makes it): no forced password change in the way of the tests.
        builder.UseSetting("MATCAD_ADMIN_PASSWORD", AdminPassword);
        builder.UseSetting(MatPlatformSettings.ApiKeyVariable, _platformKey ? PlatformKey : "");
        builder.UseSetting(MatPlatformSettings.AuthorityVariable, _authority ?? "");
        builder.UseSetting(MatPlatformSettings.ClientIdVariable, "");
        builder.UseSetting(MatPlatformSettings.ClientSecretVariable, _authority is null ? "" : "test-secret");
        builder.UseSetting(MatPlatformSettings.InternalUrlVariable, "");

        if (_authority is not null && _reachable)
        {
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = _authority,
                AuthorizationEndpoint = _authority + "/connect/authorize",
                TokenEndpoint = _authority + "/connect/token",
                EndSessionEndpoint = _authority + "/connect/logout",
            };

            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<OpenIdConnectOptions>(MatIdentitySignIn.Scheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
                services.PostConfigure<JwtBearerOptions>(MatApiAccess.BearerScheme, options =>
                {
                    options.Configuration = configuration;
                    options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
                });
            });
        }
    }

    /// <summary>A client that keeps cookies and does not follow redirects (the tests look at each step).</summary>
    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>A client with <paramref name="key"/> in <c>X-Api-Key</c> (null: none).</summary>
    public HttpClient Api(string? key = PlatformKey)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (key != null) client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    /// <summary>A browser with its own cookie jar, so that a test can hand cookies over from a sign-in through MatIdentity.</summary>
    public (HttpClient Client, CookieContainer Cookies) CookieBrowser()
    {
        var cookies = new CookieContainer();
        return (CreateDefaultClient(new Uri("http://localhost"), new CookieContainerHandler(cookies)), cookies);
    }

    /// <summary>Signs in on the local form (<c>/Login?local=1</c>).</summary>
    public static async Task<HttpResponseMessage> SignInLocallyAsync(HttpClient client, string username, string password)
    {
        var page = await client.GetStringAsync("/Login?local=1");
        return await client.PostAsync("/Login?local=1", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(page),
            ["Username"] = username,
            ["Password"] = password,
        }));
    }

    /// <summary>Signs out the way the menu does: a POST of <c>/Logout</c> with the antiforgery token of a signed-in page.</summary>
    public static async Task<HttpResponseMessage> SignOutAsync(HttpClient client)
    {
        var page = await client.GetStringAsync("/");
        return await client.PostAsync("/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(page),
        }));
    }

    public static string Token(string html)
        => Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""").Groups[1].Value;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file for a moment; the temp folder is cleaned by the system later.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Only the engine key (an install from before the platform): the platform endpoints take it too.</summary>
public sealed class EngineKeyOnlyFactory() : PlatformFactory(engineKey: true, platformKey: false, authority: null);

/// <summary>No key at all: <c>/api/v1</c> answers 503, the protected platform endpoints 401.</summary>
public sealed class NoKeyFactory() : PlatformFactory(engineKey: false, platformKey: false, authority: null);

/// <summary>With <c>MATIDENTITY_AUTHORITY</c> (a MatIdentity that does not need to run).</summary>
public sealed class IdentityFactory() : PlatformFactory(engineKey: true, platformKey: true, authority: IdentityFactory.Authority)
{
    public const string Authority = "http://identity.test";
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c> pointing at an address where nothing answers (MatIdentity is down, or behind this very proxy).</summary>
public sealed class UnreachableIdentityFactory() : PlatformFactory(engineKey: true, platformKey: true, authority: "http://127.0.0.1:9", reachable: false);
