using Matcad.Config;

namespace Matcad.Tests;

public class RouteEditorTests
{
    private static RouteForm Form(
        string? host = "App.Example.com", bool wildcard = false, bool redirect = false, string? upstream = "http://backend:8080",
        string? fallbackUrl = null, long? providerId = null, int? listenPort = null, string? name = null, string? acmeEmail = null) =>
        new(host, wildcard, redirect, upstream, InsecureSkipVerify: false, fallbackUrl, RedirectPermanent: false,
            AuthenticationId: null, providerId, acmeEmail, Enabled: true, name, AllowEmbedding: false, listenPort);

    private static (string? Error, RouteConfig Route) Apply(RouteForm form)
    {
        var route = new RouteConfig();
        return (RouteEditor.Apply(form, route), route);
    }

    [Fact]
    public void Single_host_is_lowercased_and_named_after_itself()
    {
        var (error, route) = Apply(Form());

        Assert.Null(error);
        Assert.Equal("app.example.com", route.Host);
        Assert.Equal("app.example.com", route.Name);
        Assert.False(route.Wildcard);
        Assert.Equal("http://backend:8080", route.Upstream);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_host_is_required(string? host) =>
        Assert.Equal("Host is required.", Apply(Form(host)).Error);

    [Theory]
    [InlineData("https://app.example.com")]
    [InlineData("app.example.com/path")]
    [InlineData("app.example.com:8443")]
    [InlineData("app example.com")]
    [InlineData(".example.com")]
    public void A_host_must_be_a_plain_name(string host) =>
        Assert.NotNull(Apply(Form(host)).Error);

    [Fact]
    public void A_wildcard_gets_its_prefix_and_needs_a_provider()
    {
        var (missingProvider, _) = Apply(Form("example.com", wildcard: true));
        var (error, route) = Apply(Form("example.com", wildcard: true, providerId: 4));

        Assert.Contains("DNS provider", missingProvider);
        Assert.Null(error);
        Assert.Equal("*.example.com", route.Host);
        Assert.Equal(4, route.ProviderId);
    }

    [Fact]
    public void A_wildcard_host_needs_a_domain() =>
        Assert.Contains("must include a domain", Apply(Form("*.com", wildcard: true, providerId: 1)).Error);

    [Fact]
    public void A_single_domain_cannot_start_with_a_wildcard() =>
        Assert.Contains("must not start with", Apply(Form("*.example.com")).Error);

    [Fact]
    public void A_single_domain_drops_the_provider_and_the_acme_email()
    {
        var (_, route) = Apply(Form(providerId: 4, acmeEmail: "me@example.org"));

        Assert.Null(route.ProviderId);
        Assert.Null(route.AcmeEmail);
    }

    [Fact]
    public void A_redirect_needs_an_http_url_and_has_no_upstream()
    {
        Assert.Contains("needs a target URL", Apply(Form(redirect: true)).Error);
        Assert.Contains("http:// or https://", Apply(Form(redirect: true, fallbackUrl: "ftp://x")).Error);

        var (error, route) = Apply(Form(redirect: true, fallbackUrl: "https://www.example.com"));
        Assert.Null(error);
        Assert.Null(route.Upstream);
        Assert.Equal("https://www.example.com", route.FallbackUrl);
    }

    [Theory]
    [InlineData("backend", true)]
    [InlineData("backend:8080", true)]
    [InlineData("mattiverse-matfile-web:8080", true)]
    [InlineData("http://backend:8080", true)]
    [InlineData("https://10.0.0.5", true)]
    [InlineData("[::1]:8080", true)]
    [InlineData("", false)]
    [InlineData("back end:80", false)]
    [InlineData("http://", false)]
    [InlineData("ftp://backend", false)]
    [InlineData("backend:", false)]
    public void Upstreams_are_checked(string value, bool valid) =>
        Assert.Equal(valid, RouteEditor.IsUpstream(value));

    [Fact]
    public void A_bad_upstream_is_rejected() =>
        Assert.Contains("upstream", Apply(Form(upstream: "not a backend")).Error);

    [Fact]
    public void A_port_bound_route_needs_an_upstream_and_has_no_host()
    {
        Assert.Contains("requires an upstream", Apply(Form(host: "", upstream: null, listenPort: 8100)).Error);

        var (error, route) = Apply(Form(host: "app.example.com", listenPort: 8100)); // a host sent along is dropped
        Assert.Null(error);
        Assert.Equal(8100, route.ListenPort);
        Assert.Equal("", route.Host);
        Assert.Equal("port 8100", route.Name);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(443)]
    [InlineData(2019)]
    [InlineData(70000)]
    public void Ports_that_Caddy_uses_or_that_do_not_exist_are_refused(int port) =>
        Assert.NotNull(Apply(Form(host: "", listenPort: port)).Error);

    [Theory]
    [InlineData("a..example.com")]
    [InlineData("-a.example.com")]
    [InlineData("a-.example.com")]
    [InlineData("bücher.example.com")]
    public void Malformed_and_non_punycode_hosts_are_refused(string host) =>
        Assert.NotNull(Apply(Form(host)).Error);

    [Theory]
    [InlineData("h2c://backend:8080")]
    [InlineData("xn--bcher-kva.example.com")]
    public void H2c_upstreams_and_punycode_names_are_accepted(string value)
    {
        Assert.True(RouteEditor.IsUpstream("h2c://backend:8080"));
        Assert.Null(Apply(Form(value.Contains("://") ? "ok.example.com" : value)).Error);
    }

    [Fact]
    public void A_rejected_form_leaves_the_route_untouched()
    {
        var route = new RouteConfig { Host = "keep.example.com", Name = "keep" };

        var error = RouteEditor.Apply(Form(host: "*.example.com"), route);

        Assert.NotNull(error);
        Assert.Equal("keep.example.com", route.Host);
        Assert.Equal("keep", route.Name);
    }
}
