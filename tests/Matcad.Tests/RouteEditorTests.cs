using Matcad.Config;

namespace Matcad.Tests;

public class RouteEditorTests
{
    private static RouteForm Form(
        string? host = "App.Example.com", bool wildcard = false, bool redirect = false, string? upstream = "http://backend:8080",
        string? fallbackUrl = null, long? providerId = null, int? listenPort = null, string? name = null, string? acmeEmail = null,
        string? upstreamHost = null, HeaderPair[]? upstreamHeaders = null, HeaderPair? require = null, string[]? ips = null) =>
        new(host, wildcard, redirect, upstream, InsecureSkipVerify: false, fallbackUrl, RedirectPermanent: false,
            AuthenticationId: null, providerId, acmeEmail, Enabled: true, name, AllowEmbedding: false, listenPort,
            upstreamHost, upstreamHeaders, require, ips);

    private const string Secret = "edge-secret-1234567890";
    private static HeaderPair Header(string name, string value) => new() { Name = name, Value = value };

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

    // ---- Guards and upstream properties --------------------------------------------------------------------------

    [Fact]
    public void The_access_properties_are_off_by_default()
    {
        var (error, route) = Apply(Form());

        Assert.Null(error);
        Assert.Null(route.UpstreamHost);
        Assert.Empty(route.UpstreamHeaders);
        Assert.Null(route.RequireHeader);
        Assert.Empty(route.AllowedIps);
        Assert.False(route.HasGuards);
    }

    [Fact]
    public void All_four_access_properties_are_stored_normalised()
    {
        var (error, route) = Apply(Form(upstreamHost: " Site.Example.org ", upstreamHeaders: [Header(" X-Up ", "one"), Header("X-Two", "two")],
            require: Header(" X-Edge ", Secret), ips: ["10.1.2.3/8", "  203.0.113.7 ", "10.0.0.0/8", ""]));

        Assert.Null(error);
        Assert.Equal("site.example.org", route.UpstreamHost);
        Assert.Equal(["X-Up", "X-Two"], route.UpstreamHeaders.Select(h => h.Name));
        Assert.Equal("X-Edge", route.RequireHeader!.Name);
        Assert.Equal(Secret, route.RequireHeader.Value);
        Assert.Equal(["10.0.0.0/8", "203.0.113.7"], route.AllowedIps); // host bits masked, duplicates collapsed, blanks skipped
        Assert.True(route.HasGuards);
    }

    [Theory]
    [InlineData("short-secret", "at least 16")]                       // too short to be worth checking
    [InlineData("secret-with-a-star-*-1234", "*")]                    // Caddy's header match reads * as a wildcard
    [InlineData("secret-with-{http.request.host}", "placeholder")]    // and replaces {...} in the value it compares with
    [InlineData("secret-with-}-brace-1234", "placeholder")]
    [InlineData("secret-with-a-tab\t-1234", "visible ASCII")]
    [InlineData("secret-with-a-nul\0-1234", "visible ASCII")]
    [InlineData("secret-with-a-newline\n-1234", "visible ASCII")]
    [InlineData("secret-with-umlaut-ü-1234", "visible ASCII")]
    [InlineData(" leading-space-secret-12", "no spaces")]       // HTTP trims the edges of a value it receives: nobody could ever match it
    [InlineData("trailing-space-secret-12 ", "no spaces")]
    [InlineData("a space inside the secret", "no spaces")]
    [InlineData("", "empty")]
    public void A_required_header_value_that_would_match_loosely_or_weakly_is_refused_without_repeating_it(string value, string reason)
    {
        var (error, route) = Apply(Form(require: Header("X-Edge", value)));

        Assert.Contains(reason, error);
        Assert.Contains("Required header", error);
        if (value.Length > 4) Assert.DoesNotContain(value.Trim(), error);
        Assert.Null(route.RequireHeader);
    }

    [Fact]
    public void A_required_header_value_may_use_every_visible_ascii_character_except_the_three_that_caddy_reads()
    {
        // ! to ~ without *, { and }: what is left is a fine secret.
        var visible = new string(Enumerable.Range('!', '~' - '!' + 1).Select(c => (char)c).Where(c => c is not ('*' or '{' or '}')).ToArray());

        Assert.Null(Apply(Form(require: Header("X-Edge", visible))).Error);
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("nul\0byte")]
    [InlineData("tab\tinside")]
    [InlineData("\u007fdelete")]
    public void An_upstream_header_value_has_no_control_characters(string value) =>
        Assert.Contains("control characters", Apply(Form(upstreamHeaders: [Header("X-A", value)])).Error);

    [Theory]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    public void An_upstream_header_value_has_no_space_at_its_edges(string value) =>
        Assert.Contains("start or end with a space", Apply(Form(upstreamHeaders: [Header("X-A", value)])).Error);

    [Fact]
    public void An_upstream_header_value_may_contain_inner_spaces_and_text_beyond_ascii()
    {
        var (error, route) = Apply(Form(upstreamHeaders: [Header("X-A", "two words"), Header("X-B", "Grüße")]));

        Assert.Null(error);
        Assert.Equal(["two words", "Grüße"], route.UpstreamHeaders.Select(h => h.Value));
    }

    [Fact]
    public void The_required_header_and_an_upstream_header_cannot_share_a_name()
    {
        // The required header is removed before the request goes upstream, and Caddy deletes after it sets: the set value would vanish.
        var error = Apply(Form(require: Header("X-Edge", Secret), upstreamHeaders: [Header("x-edge", "another-value-1234")])).Error;

        Assert.Contains("cannot share a name", error);
        Assert.Contains("X-Edge", error);
        Assert.DoesNotContain(Secret, error);
        Assert.Null(Apply(Form(require: Header("X-Edge", Secret), upstreamHeaders: [Header("X-Other", "another-value-1234")])).Error);
    }

    [Fact]
    public void A_secret_of_exactly_the_minimum_length_is_accepted()
    {
        Assert.Null(Apply(Form(require: Header("X-Edge", new string('a', RouteGuards.MinSecretLength)))).Error);
        Assert.NotNull(Apply(Form(require: Header("X-Edge", new string('a', RouteGuards.MinSecretLength - 1)))).Error);
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("host")]
    [InlineData("Content-Length")]
    [InlineData("content-length")]
    [InlineData("Transfer-Encoding")]
    [InlineData("CONNECTION")]
    [InlineData("")]
    [InlineData("X Edge")]
    [InlineData("X-Edge:")]
    [InlineData("-X-Edge")]
    [InlineData("+X-Edge")]
    [InlineData("X-Edge{x}")]
    [InlineData("X-Ümlaut")]
    public void Header_names_that_are_not_usable_are_refused_everywhere(string name)
    {
        Assert.NotNull(Apply(Form(require: Header(name, Secret))).Error);
        Assert.NotNull(Apply(Form(upstreamHeaders: [Header(name, "value")])).Error);
    }

    [Theory]
    [InlineData("X-Edge")]
    [InlineData("x_edge")]
    [InlineData("X-Edge.1")]
    [InlineData("Authorization")]
    public void Ordinary_header_names_are_accepted(string name)
    {
        // Each on its own: the two cannot share a name (see below).
        Assert.Null(Apply(Form(upstreamHeaders: [Header(name, "value")])).Error);
        Assert.Null(Apply(Form(require: Header(name, Secret))).Error);
    }

    [Fact]
    public void The_host_header_has_a_field_of_its_own_and_the_message_says_so()
    {
        Assert.Contains("Host header field of the route", Apply(Form(upstreamHeaders: [Header("Host", "x.example")])).Error);
    }

    [Fact]
    public void Upstream_headers_must_be_unique_and_without_placeholders()
    {
        Assert.Contains("listed twice", Apply(Form(upstreamHeaders: [Header("X-A", "1"), Header("x-a", "2")])).Error);
        Assert.Contains("placeholder", Apply(Form(upstreamHeaders: [Header("X-A", "{http.request.remote.host}")])).Error);
        Assert.Contains("empty", Apply(Form(upstreamHeaders: [Header("X-A", "")])).Error);
        Assert.Contains("at most", Apply(Form(upstreamHeaders: Enumerable.Range(0, 21).Select(i => Header("X-" + i, "v")).ToArray())).Error);
        // A star is fine here: only a value that is compared with the request has to be exact.
        Assert.Null(Apply(Form(upstreamHeaders: [Header("X-A", "*")])).Error);
    }

    [Theory]
    [InlineData("Site.Example.org", "site.example.org")]
    [InlineData("site.example.org:8443", "site.example.org:8443")]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData("[::1]:8080", "[::1]:8080")]
    [InlineData("  ", null)]
    public void The_upstream_host_is_a_plain_host_name_with_an_optional_port(string value, string? expected)
    {
        var (error, route) = Apply(Form(upstreamHost: value));

        Assert.Null(error);
        Assert.Equal(expected, route.UpstreamHost);
    }

    [Theory]
    [InlineData("https://site.example.org")]
    [InlineData("site.example.org/path")]
    [InlineData("site example.org")]
    [InlineData("*.example.org")]
    [InlineData("site.example.org:0")]
    [InlineData("site.example.org:99999")]
    [InlineData("site.example.org:abc")]
    [InlineData("a:b:c")]
    [InlineData("{http.request.host}")]
    public void An_upstream_host_that_is_not_a_host_is_refused(string value) =>
        Assert.Contains("Upstream host", Apply(Form(upstreamHost: value)).Error);

    [Fact]
    public void What_is_said_to_the_upstream_needs_a_reverse_proxy_but_a_guard_does_not()
    {
        Assert.Contains("only apply to a reverse proxy", Apply(Form(redirect: true, fallbackUrl: "https://x.example.org", upstreamHost: "site.example.org")).Error);
        Assert.Contains("only apply to a reverse proxy", Apply(Form(upstream: null, upstreamHeaders: [Header("X-A", "1")])).Error);

        var (error, route) = Apply(Form(redirect: true, fallbackUrl: "https://x.example.org", require: Header("X-Edge", Secret), ips: ["10.0.0.0/8"]));
        Assert.Null(error);
        Assert.True(route.HasGuards);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("10.0.0.0/8", "10.0.0.0/8")]
    [InlineData("10.1.2.3/8", "10.0.0.0/8")]
    [InlineData("192.168.1.5/32", "192.168.1.5/32")]
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]
    [InlineData("::1", "::1")]
    [InlineData("FD00::/8", "fd00::/8")]
    [InlineData("2001:db8::1234/32", "2001:db8::/32")]
    [InlineData("::ffff:127.0.0.1", "127.0.0.1")] // the form Caddy compares for an IPv4 peer
    public void Allowed_ips_are_ip_addresses_or_cidr_ranges_in_their_canonical_form(string entry, string expected)
    {
        var (error, route) = Apply(Form(ips: [entry]));

        Assert.Null(error);
        Assert.Equal([expected], route.AllowedIps);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("10")]
    [InlineData("1.2.3")]
    [InlineData("256.1.1.1")]
    [InlineData("10.0.0.0/33")]
    [InlineData("::1/129")]
    [InlineData("10.0.0.0/8/9")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/")]
    [InlineData("fe80::1%eth0")]
    [InlineData("[::1]")]
    [InlineData("::ffff:10.0.0.0/104")]
    [InlineData("example.com")]
    public void An_entry_that_is_not_an_address_or_range_is_refused_and_named(string entry) =>
        Assert.Contains($"'{entry}'", Apply(Form(ips: ["10.0.0.0/8", entry])).Error);

    [Fact]
    public void Splitting_the_ip_box_accepts_lines_commas_and_spaces()
    {
        Assert.Equal(["10.0.0.0/8", "1.2.3.4", "fd00::/8", "5.6.7.8"], RouteGuards.SplitIps("10.0.0.0/8\r\n1.2.3.4, fd00::/8;\n\n  5.6.7.8 "));
        Assert.Empty(RouteGuards.SplitIps(null));
    }

    [Fact]
    public void A_rejected_form_leaves_the_access_properties_untouched_too()
    {
        var route = new RouteConfig { Host = "keep.example.com", UpstreamHost = "keep.example.org", RequireHeader = Header("X-Keep", Secret), AllowedIps = ["10.0.0.0/8"] };

        var error = RouteEditor.Apply(Form(upstreamHost: "other.example.org", require: Header("X-Edge", "short")), route);

        Assert.NotNull(error);
        Assert.Equal("keep.example.org", route.UpstreamHost);
        Assert.Equal("X-Keep", route.RequireHeader!.Name);
        Assert.Equal(["10.0.0.0/8"], route.AllowedIps);
    }

    [Theory]
    [InlineData("x-edge", "X-Edge")]
    [InlineData("X-EDGE", "X-Edge")]
    [InlineData("x-forwarded-for", "X-Forwarded-For")]
    [InlineData("authorization", "Authorization")]
    [InlineData("x_edge", "X_edge")] // only a hyphen starts a new word, as in Go
    public void Header_names_are_written_the_way_the_http_stack_does(string name, string expected) =>
        Assert.Equal(expected, RouteGuards.CanonicalName(name));

    [Fact]
    public void A_header_left_empty_keeps_the_saved_value_but_only_for_a_name_that_has_one()
    {
        var stored = new[] { Header("X-Keep", "kept-value-123"), Header("X-Other", "other-value") };
        var posted = new[] { Header("x-keep", ""), Header("X-New", "typed") };

        Assert.Null(RouteGuards.KeepStoredValues(posted, stored, "Upstream header"));
        Assert.Equal("kept-value-123", posted[0].Value);
        Assert.Equal("typed", posted[1].Value);

        var unknown = RouteGuards.KeepStoredValues([Header("X-Unknown", "")], stored, "Upstream header");
        Assert.Contains("X-Unknown", unknown);
        Assert.Contains("enter a value", unknown);
        Assert.NotNull(RouteGuards.KeepStoredValues([Header("X-Keep", "")], null, "Upstream header"));
    }
}
