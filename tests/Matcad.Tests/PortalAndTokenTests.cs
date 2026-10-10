using Matcad.Pages.Auth;
using Matcad.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Matcad.Tests;

public class PortalAndTokenTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/dashboard?x=1", "/dashboard?x=1")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("ftp://app.example.com/x", "/")]
    [InlineData("https://app.example.com/page?x=1", "https://app.example.com/page?x=1")]
    [InlineData("https://sub.example.com/", "https://sub.example.com/")]
    [InlineData("https://example.com/", "https://example.com/")]
    [InlineData("https://evil.com/", "/")]
    [InlineData("https://notexample.com/", "/")]
    [InlineData("https://example.com.evil.com/", "/")]
    [InlineData("/\t/evil.example/login", "/")]
    [InlineData("/\n/evil.example", "/")]
    [InlineData("/ok\r\nSet-Cookie: x=1", "/")]
    [InlineData("https://app.example.com\t.evil.com/", "/")]
    [InlineData("https://app.example.com:8443/x", "https://app.example.com:8443/x")]
    public void Redirects_stay_on_this_site_or_the_base_domain(string? rd, string expected) =>
        Assert.Equal(expected, PortalModel.SafeRedirect(rd, "example.com", "app.example.com"));

    [Fact]
    public void The_current_host_is_allowed_without_a_base_domain() =>
        Assert.Equal("https://solo.org/x", PortalModel.SafeRedirect("https://solo.org/x", null, "solo.org"));

    [Theory]
    [InlineData("app.example.com", "example.com", "example.com")]
    [InlineData("example.com", "example.com", "example.com")]
    [InlineData("other.org", "example.com", null)]
    [InlineData("notexample.com", "example.com", null)]
    [InlineData("app.example.com", "", null)]
    [InlineData("", "example.com", null)]
    public void The_cookie_domain_is_the_base_domain_only_for_hosts_inside_it(string host, string baseDomain, string? expected) =>
        Assert.Equal(expected, PortalModel.CookieDomain(host, baseDomain));

    private static ForwardAuthTokens Tokens() => new(new EphemeralDataProtectionProvider());

    [Fact]
    public void A_token_carries_the_user_with_pipes_and_the_password_fingerprint()
    {
        var tokens = Tokens();

        var ticket = tokens.Validate(tokens.Issue(7, "a|b|c", "hash-1"), 7);

        Assert.NotNull(ticket);
        Assert.Equal("a|b|c", ticket.Username);
        Assert.Equal(ForwardAuthTokens.Fingerprint("hash-1"), ticket.PasswordFingerprint);
    }

    [Fact]
    public void A_token_is_only_valid_for_its_authentication()
    {
        var tokens = Tokens();

        Assert.Null(tokens.Validate(tokens.Issue(7, "bob", "h"), 8));
    }

    [Fact]
    public void Garbage_and_foreign_tokens_are_rejected()
    {
        var tokens = Tokens();
        var foreign = Tokens().Issue(7, "bob", "h"); // protected with another key

        Assert.Null(tokens.Validate("not-a-token", 7));
        Assert.Null(tokens.Validate(foreign, 7));
        Assert.Null(tokens.Validate("", 7));
    }

    [Fact]
    public void A_new_password_gives_a_new_fingerprint() =>
        Assert.NotEqual(ForwardAuthTokens.Fingerprint("old"), ForwardAuthTokens.Fingerprint("new"));
}
