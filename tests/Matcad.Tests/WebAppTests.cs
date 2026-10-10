using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Matcad.Tests;

/// <summary>Starts the whole app on a temp data folder.</summary>
public sealed class MatcadFactory : WebApplicationFactory<Program>
{
    public const string ApiKey = "test-api-key";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting(Microsoft.AspNetCore.Hosting.WebHostDefaults.EnvironmentKey, "Production");
        builder.UseSetting("Matcad:DataDir", _folder);
        builder.UseSetting("Matcad:ConfigDir", Path.Combine(_folder, "config"));
        builder.UseSetting("Matcad:CaddyLogDir", Path.Combine(_folder, "caddy-logs"));
        builder.UseSetting("Matcad:ApiKey", ApiKey);
        // Nothing listens there: the pushes to Caddy fail fast and the tests do not wait for them.
        builder.UseSetting("Matcad:Caddy:AdminUrl", "http://127.0.0.1:1");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { /* temp folder */ }
    }
}

/// <summary>A browser: keeps cookies, does not follow redirects, and fills in antiforgery tokens.</summary>
public sealed class Browser
{
    private static readonly Regex Token = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);
    public HttpClient Client { get; }

    public Browser(MatcadFactory factory) =>
        Client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    public async Task<(HttpResponseMessage Response, string Body)> GetAsync(string url)
    {
        var response = await Client.GetAsync(url);
        return (response, await response.Content.ReadAsStringAsync());
    }

    public async Task<HttpResponseMessage> PostFormAsync(string pageUrl, string postUrl, Dictionary<string, string> fields)
    {
        var (_, page) = await GetAsync(pageUrl);
        var token = Token.Match(page).Groups[1].Value;
        Assert.NotEmpty(token);
        fields["__RequestVerificationToken"] = token;
        return await Client.PostAsync(postUrl, new FormUrlEncodedContent(fields));
    }

    public Task<HttpResponseMessage> LoginAsync(string login, string password) =>
        PostFormAsync("/Login", "/Login", new() { ["Username"] = login, ["Password"] = password });
}

public class WebAppTests : IClassFixture<MatcadFactory>
{
    private readonly MatcadFactory _factory;
    public WebAppTests(MatcadFactory factory) => _factory = factory;

    private static string Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? "";

    [Fact]
    public async Task The_health_check_and_the_installable_app_files_need_no_sign_in()
    {
        var browser = new Browser(_factory);

        var (health, body) = await browser.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ok", body);
        Assert.Contains("no-store", health.Headers.CacheControl!.ToString());

        var (manifest, _) = await browser.GetAsync("/manifest.webmanifest");
        Assert.Equal("application/manifest+json", manifest.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/sw.js")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/offline.html")).Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/img/icon-192.png")).Response.StatusCode);
    }

    [Fact]
    public async Task Pages_need_a_sign_in_and_responses_carry_the_basic_security_headers()
    {
        var browser = new Browser(_factory);

        var (response, _) = await browser.GetAsync("/Routes");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login?returnUrl=", Location(response));
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/logs/stream")).Response.StatusCode);
    }

    [Fact]
    public async Task A_user_who_has_to_change_the_password_cannot_read_the_request_log()
    {
        var auth = _factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("newcomer") == null)
            await auth.CreateUser("newcomer", null, "initial-password", Matcad.Data.UserRole.User, null, mustChangePassword: true);
        var browser = new Browser(_factory);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("newcomer", "initial-password")).StatusCode);

        Assert.Equal("/Account/Profile", Location((await browser.GetAsync("/logs/stream")).Response));
        Assert.Equal("/Account/Profile", Location((await browser.GetAsync("/Statistics")).Response));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Account/Profile")).Response.StatusCode);
    }

    [Fact]
    public async Task Signing_out_needs_a_post_with_a_token()
    {
        var browser = new Browser(_factory);
        await LoginAsAdmin(browser);

        var get = await browser.GetAsync("/Logout");
        Assert.Equal(HttpStatusCode.Redirect, get.Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).Response.StatusCode); // still signed in

        var noToken = await browser.Client.PostAsync("/Logout", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        var post = await browser.PostFormAsync("/", "/Logout", new());
        Assert.Equal("/Login", Location(post));
        Assert.StartsWith("/Login", Location((await browser.GetAsync("/")).Response));
    }

    [Fact]
    public async Task Too_many_wrong_passwords_block_the_login_for_a_while()
    {
        var browser = new Browser(_factory);
        for (var i = 0; i < LoginAttemptTracker.MaxPerAccountOnClient; i++)
            Assert.Contains("Incorrect", await (await browser.LoginAsync("guesser", "wrong-" + i)).Content.ReadAsStringAsync());

        var blocked = await (await browser.LoginAsync("guesser", "wrong-again")).Content.ReadAsStringAsync();

        Assert.Contains("Too many failed attempts", blocked);
    }

    [Fact]
    public async Task The_api_needs_its_key_and_manages_routes_idempotently()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/status")).StatusCode);

        var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/api/v1/status")).StatusCode);

        // The same host twice is one route.
        var first = await (await api.PostAsJsonAsync("/api/v1/routes", new { host = "Matfile.apps.example", wildcard = false, target = "proxy", upstream = "http://mattiverse-matfile-web:8080", enabled = true, name = "MatFile" })).Content.ReadFromJsonAsync<JsonObject>();
        var second = await (await api.PostAsJsonAsync("/api/v1/routes", new { host = "matfile.apps.example", wildcard = false, target = "proxy", upstream = "http://mattiverse-matfile-web:9090", enabled = true })).Content.ReadFromJsonAsync<JsonObject>();
        var id = first!["route"]!["id"]!.GetValue<long>();
        Assert.Equal(id, second!["route"]!["id"]!.GetValue<long>());
        var manual = await api.GetFromJsonAsync<JsonArray>("/api/v1/routes/manual");
        var route = Assert.Single(manual!, r => r!["host"]!.GetValue<string>() == "matfile.apps.example")!;
        Assert.Equal("http://mattiverse-matfile-web:9090", route["upstream"]!.GetValue<string>());

        // Bad input is rejected instead of breaking the whole Caddy config.
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/v1/routes", new { host = "https://nope", upstream = "a:1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/v1/routes", new { host = "x.example", upstream = "not a backend" })).StatusCode);
        // An id the server has not seen creates the route with that id (as before), and the host stays unique.
        var chosen = await (await api.PostAsJsonAsync("/api/v1/routes", new { id = 4242, host = "y.example", upstream = "a:1" })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(4242, chosen!["route"]!["id"]!.GetValue<long>());
        Assert.Equal(HttpStatusCode.Conflict, (await api.PostAsJsonAsync("/api/v1/routes", new { id = 4243, host = "y.example", upstream = "a:1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsJsonAsync("/api/v1/routes", new { host = "", listenPort = 443, upstream = "a:1" })).StatusCode);

        // A port binding, as matOS creates it, is idempotent as well.
        var body = new { name = "stack", host = "", listenPort = 8100, upstream = "http://c:80", enabled = true, allowEmbedding = true };
        await api.PostAsJsonAsync("/api/v1/routes", body);
        await api.PostAsJsonAsync("/api/v1/routes", body);
        manual = await api.GetFromJsonAsync<JsonArray>("/api/v1/routes/manual");
        Assert.Single(manual!, r => r!["listenPort"]?.GetValue<int>() == 8100);
    }

    [Fact]
    public async Task The_api_checks_settings_before_it_changes_anything()
    {
        var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);

        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsJsonAsync("/api/v1/settings", new { baseDomain = "https://example.com/" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PutAsJsonAsync("/api/v1/settings", new { portalMode = "sideways" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.PutAsJsonAsync("/api/v1/settings", new { baseDomain = "Apps.Example.com", portalMode = "Redirect" })).StatusCode);

        var settings = await api.GetFromJsonAsync<JsonObject>("/api/v1/settings");
        Assert.Equal("apps.example.com", settings!["baseDomain"]!.GetValue<string>());
        Assert.Equal("redirect", settings["portalMode"]!.GetValue<string>());

        await api.PutAsJsonAsync("/api/v1/settings", new { baseDomain = "", portalMode = "inline" }); // the app is shared with the other tests
    }

    [Fact]
    public async Task The_matcad_login_portal_issues_a_cookie_that_ends_with_the_password()
    {
        var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);
        async Task<long> SaveAuthentication(string password, long? id = null)
        {
            var result = await (await api.PostAsJsonAsync("/api/v1/authentications",
                new { id, name = "Customers", type = "Matcad", users = new[] { new { username = "kunde", password } } })).Content.ReadFromJsonAsync<JsonObject>();
            return result!["id"]!.GetValue<long>();
        }
        var authId = await SaveAuthentication("first-password");

        var browser = new Browser(_factory);
        string Verify() => $"/auth/verify";
        async Task<HttpResponseMessage> CallVerify()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Verify());
            request.Headers.Add("X-Matcad-Auth", authId.ToString());
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-Host", "app.example.com");
            request.Headers.Add("X-Forwarded-Uri", "/private?x=1");
            return await browser.Client.SendAsync(request);
        }

        // Without a cookie: sent to the login, which knows where to come back to.
        var anonymous = await CallVerify();
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.Contains($"/auth/portal?auth={authId}&rd=https%3A%2F%2Fapp.example.com%2Fprivate%3Fx%3D1", Location(anonymous));

        var wrong = await browser.PostFormAsync($"/auth/portal?auth={authId}", $"/auth/portal?auth={authId}", new() { ["Auth"] = authId.ToString(), ["Username"] = "kunde", ["Password"] = "nope" });
        Assert.Contains("Incorrect username or password", await wrong.Content.ReadAsStringAsync());

        var login = await browser.PostFormAsync($"/auth/portal?auth={authId}", $"/auth/portal?auth={authId}",
            new() { ["Auth"] = authId.ToString(), ["Username"] = "KUNDE", ["Password"] = "first-password", ["Rd"] = "https://evil.example/" });
        Assert.Equal("/", Location(login)); // not an address this portal may send people to
        Assert.Contains($"matcad_auth_{authId}", login.Headers.GetValues("Set-Cookie").Single());
        Assert.Contains("httponly", login.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);

        var allowed = await CallVerify();
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("kunde", allowed.Headers.GetValues("X-Matcad-User").Single());

        // Saving the same password again (an API client that re-posts its user list) does not end the session ...
        await SaveAuthentication("first-password", authId);
        Assert.Equal(HttpStatusCode.OK, (await CallVerify()).StatusCode);

        // ... but a new password does.
        await SaveAuthentication("second-password", authId);
        Assert.Equal(HttpStatusCode.Redirect, (await CallVerify()).StatusCode);
    }

    [Fact]
    public async Task A_portal_user_with_a_non_ascii_name_does_not_break_the_verify_endpoint()
    {
        var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);
        var saved = await (await api.PostAsJsonAsync("/api/v1/authentications",
            new { name = "Umlauts", type = "Matcad", users = new[] { new { username = "Jürgen", password = "a-password-1" } } })).Content.ReadFromJsonAsync<JsonObject>();
        var authId = saved!["id"]!.GetValue<long>();
        var browser = new Browser(_factory);
        await browser.PostFormAsync($"/auth/portal?auth={authId}", $"/auth/portal?auth={authId}",
            new() { ["Auth"] = authId.ToString(), ["Username"] = "jürgen", ["Password"] = "a-password-1" });

        var request = new HttpRequestMessage(HttpMethod.Get, "/auth/verify");
        request.Headers.Add("X-Matcad-Auth", authId.ToString());
        var response = await browser.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("J%C3%BCrgen", response.Headers.GetValues("X-Matcad-User").Single());
    }

    [Fact]
    public async Task A_deleted_authentication_stops_serving_its_routes_open()
    {
        var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);
        await api.PostAsJsonAsync("/api/v1/routes", new { host = "ghost.example", upstream = "a:1", enabled = true, authenticationId = 777 });

        var config = _factory.Services.GetRequiredService<CaddyService>().BuildJson();

        Assert.Contains("the authentication of this route is missing", config);
    }

    private async Task LoginAsAdmin(Browser browser)
    {
        // The seeded admin must choose a password first; this test signs in as someone who already has.
        var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Add("X-Api-Key", MatcadFactory.ApiKey);
        var auth = _factory.Services.CreateScope().ServiceProvider.GetRequiredService<AuthService>();
        if (await auth.FindByLogin("tester") == null)
            await auth.CreateUser("tester", null, "tester-password", Matcad.Data.UserRole.Admin, null);
        var setup = _factory.Services.GetRequiredService<Matcad.Config.ConfigStore>().Settings;
        setup.SetupCompleted = true;
        _factory.Services.GetRequiredService<Matcad.Config.ConfigStore>().SaveSettings(setup);

        Assert.Equal(HttpStatusCode.Redirect, (await browser.LoginAsync("tester", "tester-password")).StatusCode);
    }
}

/// <summary>The first run of an untouched installation. Own app instance, because it changes the account and finishes the wizard.</summary>
public class OnboardingTests : IClassFixture<MatcadFactory>
{
    private readonly MatcadFactory _factory;
    public OnboardingTests(MatcadFactory factory) => _factory = factory;

    private static string Location(HttpResponseMessage response) => response.Headers.Location?.OriginalString ?? "";

    [Fact]
    public async Task The_default_admin_has_to_choose_a_password_before_anything_else()
    {
        var browser = new Browser(_factory);

        var login = await browser.LoginAsync("admin", "admin");
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains("matcad_session", login.Headers.GetValues("Set-Cookie").Single());

        Assert.Equal("/Setup?step=account", Location((await browser.GetAsync("/")).Response));
        Assert.Equal("/Setup?step=account", Location((await browser.GetAsync("/Routes")).Response));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/healthz")).Response.StatusCode);

        // The other steps of the wizard are closed until then.
        var skip = await browser.PostFormAsync("/Setup?step=account", "/Setup?handler=Skip", new());
        Assert.Contains("step=account", Location(skip));

        var weak = await browser.PostFormAsync("/Setup?step=account", "/Setup?handler=Account",
            new() { ["NewUsername"] = "admin", ["NewPassword"] = "short", ["ConfirmPassword"] = "short" });
        Assert.Equal(HttpStatusCode.OK, weak.StatusCode);
        Assert.Contains("at least 8 characters", await weak.Content.ReadAsStringAsync());

        var changed = await browser.PostFormAsync("/Setup?step=account", "/Setup?handler=Account",
            new() { ["NewUsername"] = "matthias", ["NewEmail"] = "ms@example.org", ["NewPassword"] = "a-better-password", ["ConfirmPassword"] = "a-better-password" });
        Assert.Contains("step=domains", Location(changed));

        // The wizard still has to be finished, and then the app is open.
        Assert.Equal("/Setup", Location((await browser.GetAsync("/")).Response));
        var finish = await browser.PostFormAsync("/Setup?step=done", "/Setup?handler=Finish", new());
        Assert.Equal(HttpStatusCode.Redirect, finish.StatusCode);
        var (dashboard, html) = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("Dashboard", html);

        // The user now signs in with the email too, and the old password is gone.
        var other = new Browser(_factory);
        Assert.Equal(HttpStatusCode.Redirect, (await other.LoginAsync("MS@example.org", "a-better-password")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await new Browser(_factory).LoginAsync("admin", "admin")).StatusCode);
    }

}
