using System.Net;
using System.Security.Claims;
using Matcad.Data;
using Matcad.Services;
using MatPlatform;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Net.Http.Headers;

namespace Matcad.Tests;

/// <summary>
/// MatIdentity's back-channel logout (MatPlatform 1.4.0, <c>POST /signout-backchannel</c>): a signed logout token ends the Matcad sessions that began
/// with its <c>sid</c>, or with <c>sub</c> alone every session of the linked user; their next request lands on the sign-in page.
/// </summary>
public sealed class BackchannelLogoutTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    private const string Path = "/signout-backchannel";

    [Fact]
    public async Task A_valid_token_ends_the_session_it_names_and_only_that()
    {
        var first = new Browser(site);
        var second = new Browser(site);
        await first.SignInThroughIdentityAsync(Person("sub-anna", "sid-anna-1"));
        await second.SignInThroughIdentityAsync(Person("sub-anna", "sid-anna-2"));
        Assert.Equal(HttpStatusCode.OK, (await first.Client.GetAsync("/Routes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.Client.GetAsync("/Routes")).StatusCode);

        var response = await PostAsync(Token("sub-anna", "sid-anna-1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        await AssertSignedOutAsync(first.Client);
        Assert.Equal(HttpStatusCode.OK, (await second.Client.GetAsync("/Routes")).StatusCode);

        using var scope = site.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatcadDbContext>();
        Assert.True(await db.AuditEntries.AnyAsync(e => e.Actor == "MatIdentity" && e.Action == "signed out by MatIdentity" && e.Subject.StartsWith("sub-anna")));
    }

    [Fact]
    public async Task A_token_with_the_subject_alone_ends_every_session_of_the_person()
    {
        var first = new Browser(site);
        var second = new Browser(site);
        var other = new Browser(site);
        await first.SignInThroughIdentityAsync(Person("sub-bert", "sid-bert-1"));
        await second.SignInThroughIdentityAsync(Person("sub-bert", "sid-bert-2"));
        await other.SignInThroughIdentityAsync(Person("sub-carl", "sid-carl-1"));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("sub-bert", null))).StatusCode);

        await AssertSignedOutAsync(first.Client);
        await AssertSignedOutAsync(second.Client);
        Assert.Equal(HttpStatusCode.OK, (await other.Client.GetAsync("/Routes")).StatusCode);
    }

    [Fact]
    public async Task A_token_with_the_session_alone_ends_that_session()
    {
        var browser = new Browser(site);
        await browser.SignInThroughIdentityAsync(Person("sub-dora", "sid-dora-1"));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token(null, "sid-dora-1"))).StatusCode);

        await AssertSignedOutAsync(browser.Client);
    }

    [Fact]
    public async Task A_sid_of_another_person_ends_nothing()
    {
        var browser = new Browser(site);
        await browser.SignInThroughIdentityAsync(Person("sub-emil", "sid-emil-1"));
        await new Browser(site).SignInThroughIdentityAsync(Person("sub-fritz", "sid-fritz-1"));

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("sub-fritz", "sid-emil-1"))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await browser.Client.GetAsync("/Routes")).StatusCode);
    }

    [Fact]
    public async Task An_unknown_subject_or_sid_is_no_error()
    {
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token("sub-nobody", null))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token(null, "sid-nobody"))).StatusCode);
    }

    [Fact]
    public async Task The_session_of_a_linked_account_keeps_the_sid_of_the_link()
    {
        using (var scope = site.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuthService>().CreateUser("greta@example.com", null, "greta-password", UserRole.User, null);
        }

        var browser = new Browser(site);
        Assert.Equal("/Login?local=1&link=1", await browser.SignInThroughIdentityAsync(Person("sub-greta", "sid-greta-1", "greta@example.com")));
        Assert.Equal(HttpStatusCode.Redirect, (await PlatformFactory.SignInLocallyAsync(browser.Client, "greta@example.com", "greta-password")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.Client.GetAsync("/Routes")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await PostAsync(Token(null, "sid-greta-1"))).StatusCode);

        await AssertSignedOutAsync(browser.Client);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_400_and_ends_nothing()
    {
        var browser = new Browser(site);
        await browser.SignInThroughIdentityAsync(Person("sub-hans", "sid-hans-1"));
        var forger = new SigningCredentials(new RsaSecurityKey(System.Security.Cryptography.RSA.Create(2048)) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(Token("sub-hans", null, forger))).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await browser.Client.GetAsync("/Routes")).StatusCode);
    }

    [Fact]
    public async Task A_token_sent_again_is_400()
    {
        var token = Token("sub-nobody-2", null);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync(token)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync(token)).StatusCode);
    }

    private async Task<HttpResponseMessage> PostAsync(string token)
        => await site.Api(null).PostAsync(Path, new FormUrlEncodedContent(new Dictionary<string, string> { ["logout_token"] = token }));

    private static async Task AssertSignedOutAsync(HttpClient client)
    {
        var response = await client.GetAsync("/Routes");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login", response.Headers.Location!.OriginalString);
    }

    private static MatIdentityPerson Person(string subject, string sessionId, string? email = null)
        => new(subject, email, subject, null, null, subject + ".id.token", new ClaimsPrincipal()) { SessionId = sessionId };

    /// <summary>A logout token as MatIdentity writes it (OpenID Connect Back-Channel Logout 1.0).</summary>
    internal static string Token(string? subject, string? sessionId, SigningCredentials? credentials = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object>
        {
            ["iss"] = IdentityFactory.Authority,
            ["aud"] = "matcad",
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + 120,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["events"] = new Dictionary<string, object> { [MatIdentityLogoutToken.EventType] = new Dictionary<string, object>() },
        };
        if (subject != null) claims["sub"] = subject;
        if (sessionId != null) claims["sid"] = sessionId;

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            TokenType = MatIdentityLogoutToken.TokenType,
            SigningCredentials = credentials ?? PlatformFactory.IdentitySigningCredentials,
        });
    }

    /// <summary>A browser with its own cookies; the callback of MatIdentity is run in the app's services and its cookies are handed over.</summary>
    private sealed class Browser
    {
        private static readonly Uri Base = new("http://localhost");
        private readonly IdentityFactory _site;
        private readonly CookieContainer _cookies;

        public Browser(IdentityFactory site)
        {
            _site = site;
            (Client, _cookies) = site.CookieBrowser();
        }

        public HttpClient Client { get; }

        public async Task<string> SignInThroughIdentityAsync(MatIdentityPerson person)
        {
            using var scope = _site.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            http.Request.Scheme = "http";
            http.Request.Host = new HostString("localhost");
            var target = await MatIdentitySignIn.CompleteSignInAsync(http, person);
            foreach (var cookie in SetCookieHeaderValue.ParseList(http.Response.Headers.SetCookie.Select(x => x ?? string.Empty).ToList()))
            {
                _cookies.Add(Base, new Cookie(cookie.Name.Value!, cookie.Value.Value, "/"));
            }

            return target;
        }
    }
}

/// <summary>
/// Without <c>MATIDENTITY_AUTHORITY</c> there is no back-channel logout: nothing is mapped, so the path is an unknown address. Signed in it is 404;
/// anonymous, like every unknown address of Matcad, it asks for a sign-in on the local form (as <c>/signin-oidc</c> does, <see cref="LocalSignInTests"/>).
/// </summary>
public sealed class BackchannelLogoutWithoutIdentityTests(PlatformFactory site) : IClassFixture<PlatformFactory>
{
    [Fact]
    public async Task The_path_is_404()
    {
        var browser = site.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await PlatformFactory.SignInLocallyAsync(browser, "admin", PlatformFactory.AdminPassword)).StatusCode);

        var signedIn = await browser.PostAsync("/signout-backchannel", Form());
        Assert.Equal(HttpStatusCode.NotFound, signedIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Routes")).StatusCode);

        var anonymous = await site.Api(null).PostAsync("/signout-backchannel", Form());
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);
        Assert.StartsWith("/Login", anonymous.Headers.Location!.OriginalString);
    }

    private static FormUrlEncodedContent Form()
        => new(new Dictionary<string, string> { ["logout_token"] = BackchannelLogoutTests.Token("sub-x", "sid-x") });
}
