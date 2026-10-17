using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Matcad.Data;
using Matcad.Services;
using MatPlatform;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace Matcad.Tests;

/// <summary>
/// The sign-in through MatIdentity and the link proof. The attack this guards against: someone names their own Matcad account after another person's
/// address; at that person's first sign-in through MatIdentity they would land in the attacker's account. The account is linked only after its own password.
/// </summary>
public sealed class PendingLinkTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    private const string LinkTarget = "/Login?local=1&link=1";

    [Fact]
    public async Task A_pre_claimed_name_does_not_let_the_person_into_that_account()
    {
        var mallory = await AddUserAsync("victim@example.com", "mallory-password");
        var count = await CountUsersAsync();
        var browser = new Browser(site);

        var target = await browser.SignInThroughIdentityAsync(new MatIdentityPerson("sub-victim", "victim@example.com", "Victim", "victim", null, "victim.id.token", new ClaimsPrincipal()));

        Assert.Equal(LinkTarget, target);
        Assert.Null((await FindAsync(mallory.Id)).ExternalSubject);
        Assert.Equal(count, await CountUsersAsync());
        Assert.Equal(HttpStatusCode.Redirect, (await browser.Client.GetAsync("/Routes")).StatusCode);

        var page = await browser.Client.GetStringAsync(LinkTarget);
        Assert.Contains("id=\"login-link\"", page);
        Assert.Contains("victim@example.com", page);
        Assert.Contains("id=\"login-form\"", page);
    }

    [Fact]
    public async Task The_local_sign_in_of_the_matched_account_links_it()
    {
        var vera = await AddUserAsync("vera@example.com", "vera-password");
        var browser = new Browser(site);
        Assert.Equal(LinkTarget, await browser.SignInThroughIdentityAsync(Person("sub-vera", "vera@example.com", "admin")));

        var response = await MatcadFactory.SignInLocallyAsync(browser.Client, "vera@example.com", "vera-password");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var linked = await FindAsync(vera.Id);
        Assert.Equal("sub-vera", linked.ExternalSubject);
        Assert.Equal(UserRole.Admin, linked.Role);
        Assert.Equal(HttpStatusCode.OK, (await browser.Client.GetAsync("/Settings")).StatusCode);
        Assert.False(browser.HasPendingLink);

        // The session keeps the ID token of the link: the sign-out ends at MatIdentity too.
        var logout = await browser.Client.GetAsync("/Logout");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.StartsWith(IdentityFactory.Authority + "/connect/logout", logout.Headers.Location!.OriginalString);
        Assert.Contains("id_token_hint=sub-vera.id.token", logout.Headers.Location!.OriginalString);

        // The next sign-in through MatIdentity goes straight into the account.
        Assert.Equal("/", await new Browser(site).SignInThroughIdentityAsync(Person("sub-vera", "vera@example.com", null)));
    }

    [Fact]
    public async Task Another_account_signing_in_links_nothing()
    {
        var walter = await AddUserAsync("walter@example.com", "walter-password");
        var browser = new Browser(site);
        Assert.Equal(LinkTarget, await browser.SignInThroughIdentityAsync(Person("sub-walter", "walter@example.com", "admin")));

        Assert.Equal(HttpStatusCode.Redirect, (await MatcadFactory.SignInLocallyAsync(browser.Client, "admin", "admin")).StatusCode);

        Assert.Null((await FindAsync(walter.Id)).ExternalSubject);
        Assert.False(await AnyWithSubjectAsync("sub-walter"));
        Assert.False(browser.HasPendingLink);

        // The link is gone: Walter's own sign-in afterwards links nothing either.
        await MatcadFactory.SignInLocallyAsync(new Browser(site).Client, "walter@example.com", "walter-password");
        Assert.Null((await FindAsync(walter.Id)).ExternalSubject);
    }

    [Fact]
    public async Task Cancel_deletes_the_pending_link()
    {
        var clara = await AddUserAsync("clara@example.com", "clara-password");
        var browser = new Browser(site);
        Assert.Equal(LinkTarget, await browser.SignInThroughIdentityAsync(Person("sub-clara", "clara@example.com", null)));

        var page = await browser.Client.GetStringAsync(LinkTarget);
        var cancel = await browser.Client.PostAsync("/Login?handler=CancelLink", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = MatcadFactory.Token(page),
        }));

        Assert.Equal(HttpStatusCode.Redirect, cancel.StatusCode);
        Assert.Equal("/Login?local=1", cancel.Headers.Location!.OriginalString);
        Assert.False(browser.HasPendingLink);
        Assert.DoesNotContain("id=\"login-link\"", await browser.Client.GetStringAsync(LinkTarget));

        await MatcadFactory.SignInLocallyAsync(browser.Client, "clara@example.com", "clara-password");
        Assert.Null((await FindAsync(clara.Id)).ExternalSubject);
    }

    [Fact]
    public async Task An_expired_or_forged_pending_link_is_ignored()
    {
        var erik = await AddUserAsync("erik@example.com", "erik-password");
        var link = new MatIdentityPendingLink("sub-erik", "erik@example.com", "Erik", null, "admin", null, erik.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var json = JsonSerializer.Serialize(link, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var protector = site.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(MatIdentityPendingLink.Purpose).ToTimeLimitedDataProtector();

        string[] values =
        [
            protector.Protect(json, DateTimeOffset.UtcNow.AddSeconds(-1)),
            "CfDJ8forged-" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)),
            site.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("Another.Purpose").ToTimeLimitedDataProtector().Protect(json, TimeSpan.FromMinutes(5)),
        ];

        foreach (var value in values)
        {
            var browser = new Browser(site);
            browser.SetPendingLink(value);

            var page = await browser.Client.GetStringAsync(LinkTarget);
            Assert.DoesNotContain("id=\"login-link\"", page);
            Assert.Contains("id=\"login-link-expired\"", page);

            Assert.Equal(HttpStatusCode.Redirect, (await MatcadFactory.SignInLocallyAsync(browser.Client, "erik@example.com", "erik-password")).StatusCode);
            Assert.Null((await FindAsync(erik.Id)).ExternalSubject);
            Assert.Equal(UserRole.User, (await FindAsync(erik.Id)).Role);
        }

        // The same link with time left does link: the values above failed for their age, forgery or purpose only.
        var fresh = new Browser(site);
        fresh.SetPendingLink(protector.Protect(json, DateTimeOffset.UtcNow.AddMinutes(5)));
        await MatcadFactory.SignInLocallyAsync(fresh.Client, "erik@example.com", "erik-password");
        Assert.Equal("sub-erik", (await FindAsync(erik.Id)).ExternalSubject);
    }

    [Fact]
    public async Task A_new_person_gets_a_user_without_a_password_and_the_role_of_the_claim()
    {
        var browser = new Browser(site);
        Assert.Equal("/Routes", await browser.SignInThroughIdentityAsync(Person("sub-nina", "nina@example.com", "admin", preferred: "nina"), "/Routes"));

        var nina = await FindBySubjectAsync("sub-nina");
        Assert.Equal("nina", nina.Username);
        Assert.Equal("", nina.PasswordHash);
        Assert.Equal(UserRole.Admin, nina.Role);
        Assert.Equal(HttpStatusCode.OK, (await browser.Client.GetAsync("/Settings")).StatusCode);

        // No local password: the local form does not let anyone in as nina.
        Assert.Equal(HttpStatusCode.OK, (await MatcadFactory.SignInLocallyAsync(new Browser(site).Client, "nina", "")).StatusCode);

        // The role follows the claim at the next sign-in.
        await new Browser(site).SignInThroughIdentityAsync(Person("sub-nina", "nina@example.com", "user", preferred: "nina"));
        Assert.Equal(UserRole.User, (await FindBySubjectAsync("sub-nina")).Role);

        // Without a role claim the role stays as it is (managed by the app).
        await new Browser(site).SignInThroughIdentityAsync(Person("sub-nina", "nina@example.com", null, preferred: "nina"));
        Assert.Equal(UserRole.User, (await FindBySubjectAsync("sub-nina")).Role);
    }

    [Fact]
    public async Task The_last_admin_is_not_demoted_by_the_claim()
    {
        using (var scope = site.Services.CreateScope())
        {
            var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
            var result = await auth.SignInExternal(new ExternalIdentity("sub-solo", null, null, "solo-admin", "admin"));
            Assert.NotNull(result.UserId);
        }

        using (var scope = site.Services.CreateScope())
        {
            // Make the new admin the only one.
            var db = scope.ServiceProvider.GetRequiredService<MatcadDbContext>();
            var others = await db.Users.Where(u => u.Role == UserRole.Admin && u.ExternalSubject != "sub-solo").ToListAsync();
            others.ForEach(u => u.Role = UserRole.User);
            await db.SaveChangesAsync();

            var auth = scope.ServiceProvider.GetRequiredService<AuthService>();
            await auth.SignInExternal(new ExternalIdentity("sub-solo", null, null, "solo-admin", "user"));
        }

        Assert.Equal(UserRole.Admin, (await FindBySubjectAsync("sub-solo")).Role);

        using (var scope = site.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MatcadDbContext>();
            var admin = await db.Users.SingleAsync(u => u.Username == "admin");
            admin.Role = UserRole.Admin;
            await db.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Several_matching_accounts_are_refused()
    {
        await AddUserAsync("Twin@example.com", "twin-password-1");
        await AddUserAsync("twin@example.com", "twin-password-2");
        var count = await CountUsersAsync();

        var target = await new Browser(site).SignInThroughIdentityAsync(Person("sub-twin", "twin@example.com", null));

        Assert.Equal("/Login?local=1&denied=1", target);
        Assert.Equal(count, await CountUsersAsync());
        Assert.Contains("id=\"login-denied\"", await site.Browser().GetStringAsync(target));
    }

    private static MatIdentityPerson Person(string subject, string email, string? role, string? preferred = null)
        => new(subject, email, subject, preferred, role, subject + ".id.token", new ClaimsPrincipal());

    private async Task<User> AddUserAsync(string username, string password)
    {
        using var scope = site.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AuthService>().CreateUser(username, password, UserRole.User, null);
    }

    private async Task<User> FindAsync(long id)
    {
        using var scope = site.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatcadDbContext>().Users.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private async Task<User> FindBySubjectAsync(string subject)
    {
        using var scope = site.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatcadDbContext>().Users.AsNoTracking().SingleAsync(x => x.ExternalSubject == subject);
    }

    private async Task<int> CountUsersAsync()
    {
        using var scope = site.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatcadDbContext>().Users.CountAsync();
    }

    private async Task<bool> AnyWithSubjectAsync(string subject)
    {
        using var scope = site.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MatcadDbContext>().Users.AnyAsync(x => x.ExternalSubject == subject);
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

        public bool HasPendingLink => _cookies.GetCookies(Base)[MatIdentityPendingLink.CookieName] is { Expired: false } cookie && cookie.Value.Length > 0;

        public void SetPendingLink(string value) => _cookies.Add(Base, new Cookie(MatIdentityPendingLink.CookieName, value, "/"));

        /// <summary>What <c>/signin-oidc</c> does after MatIdentity signed the person in; the cookies it sets go into this browser.</summary>
        public async Task<string> SignInThroughIdentityAsync(MatIdentityPerson person, string? returnUrl = null)
        {
            using var scope = _site.Services.CreateScope();
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            http.Request.Scheme = "http";
            http.Request.Host = new HostString("localhost");
            var target = await MatIdentitySignIn.CompleteSignInAsync(http, person, returnUrl);
            foreach (var cookie in SetCookieHeaderValue.ParseList(http.Response.Headers.SetCookie.Select(x => x ?? string.Empty).ToList()))
            {
                _cookies.Add(Base, new Cookie(cookie.Name.Value!, cookie.Value.Value, "/"));
            }

            return target;
        }
    }
}
