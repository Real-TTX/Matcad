using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Matcad.Tests;

/// <summary>Without <c>MATIDENTITY_AUTHORITY</c> nothing changes: the local sign-in form, as before.</summary>
public sealed class LocalSignInTests(MatcadFactory site) : IClassFixture<MatcadFactory>
{
    [Fact]
    public async Task The_sign_in_page_is_the_local_form()
    {
        var response = await site.Browser().GetAsync("/Login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.DoesNotContain("MatIdentity", html);
    }

    [Fact]
    public async Task A_page_that_needs_a_sign_in_sends_to_the_local_form_and_it_works()
    {
        var browser = site.Browser();
        var first = await browser.GetAsync("/Routes");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.StartsWith("/Login", first.Headers.Location!.OriginalString);

        var wrong = await MatcadFactory.SignInLocallyAsync(browser, "admin", "nope");
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Contains("Incorrect username or password.", await wrong.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.Redirect, (await MatcadFactory.SignInLocallyAsync(browser, "admin", "admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Routes")).StatusCode);

        // Sign-out goes back to the local form, not anywhere else.
        var logout = await browser.GetAsync("/Logout");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/Login", logout.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Routes")).StatusCode);
    }

    [Fact]
    public async Task There_is_no_oidc_callback()
    {
        // Nothing handles it: like any unknown address it asks for a sign-in on the local form.
        var response = await site.Browser().GetAsync("/signin-oidc");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Login", response.Headers.Location!.OriginalString);
    }
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c>: the automatic sign-in at MatIdentity, the local way in with <c>?local=1</c>.</summary>
public sealed class IdentitySignInTests(IdentityFactory site) : IClassFixture<IdentityFactory>
{
    [Fact]
    public async Task The_sign_in_page_goes_straight_to_matidentity_with_code_and_pkce()
    {
        var response = await site.Browser().GetAsync("/Login?returnUrl=%2FRoutes");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var location = response.Headers.Location!;
        Assert.StartsWith(IdentityFactory.Authority + "/connect/authorize", location.OriginalString);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("matcad", query["client_id"].ToString());
        Assert.Equal("code", query["response_type"].ToString());
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        Assert.EndsWith("/signin-oidc", query["redirect_uri"].ToString());
        Assert.StartsWith("http://", query["redirect_uri"].ToString());
    }

    [Fact]
    public async Task Behind_the_tls_proxy_the_redirect_address_is_https()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/Login");
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await site.Browser().SendAsync(request);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        Assert.StartsWith("https://", query["redirect_uri"].ToString());
    }

    [Fact]
    public async Task A_page_that_needs_a_sign_in_ends_at_matidentity()
    {
        var browser = site.Browser();
        var first = await browser.GetAsync("/Routes");
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        var login = first.Headers.Location!.OriginalString;
        Assert.StartsWith("/Login", login);

        var second = await browser.GetAsync(login);
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.StartsWith(IdentityFactory.Authority, second.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_local_form_stays_reachable_and_works()
    {
        var browser = site.Browser();
        var page = await browser.GetAsync("/Login?local=1");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("Sign in with MatIdentity", html);

        Assert.Equal(HttpStatusCode.Redirect, (await MatcadFactory.SignInLocallyAsync(browser, "admin", "admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Settings")).StatusCode);

        // A local session ends locally: back to the sign-in page without the automatic sign-in.
        var logout = await browser.GetAsync("/Logout");
        Assert.Equal("/Login?signedOut=1", logout.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("/Login?signedOut=1")]
    [InlineData("/Login?ssoError=1")]
    [InlineData("/Login?local=true")]
    public async Task These_addresses_show_the_page_instead_of_going_to_matidentity(string path)
    {
        var response = await site.Browser().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Sign in with MatIdentity", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_definition_names_matidentity_and_a_bad_token_is_401()
    {
        using var doc = JsonDocument.Parse(await site.Api(null).GetStringAsync("/api"));
        Assert.Equal(IdentityFactory.Authority, doc.RootElement.GetProperty("auth").GetProperty("identity").GetString());

        var bearer = site.Api(null);
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await site.Api().GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task The_engine_api_does_not_change_with_matidentity()
    {
        Assert.Equal(HttpStatusCode.OK, (await site.Api(MatcadFactory.EngineKey).GetAsync("/api/v1/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await site.Api(null).GetAsync("/api/v1/status")).StatusCode);
    }
}

/// <summary>
/// MatIdentity is configured but cannot be reached (it may sit behind this very Matcad): the sign-in page falls back to the local form,
/// and the admin gets in with its password.
/// </summary>
public sealed class UnreachableIdentityTests(UnreachableIdentityFactory site) : IClassFixture<UnreachableIdentityFactory>
{
    [Fact]
    public async Task The_sign_in_page_shows_the_local_form_with_a_message()
    {
        var response = await site.Browser().GetAsync("/Login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("id=\"login-form\"", html);
        Assert.Contains("id=\"login-sso-error\"", html);
    }

    [Fact]
    public async Task The_admin_gets_in_locally()
    {
        var browser = site.Browser();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Login?local=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await MatcadFactory.SignInLocallyAsync(browser, "admin", "admin")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Settings")).StatusCode);
    }
}
