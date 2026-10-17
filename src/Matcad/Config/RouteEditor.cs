using System.Text.RegularExpressions;

namespace Matcad.Config;

/// <summary>
/// The fields of a route as the edit page and the REST API deliver them. The last four are the optional access properties
/// (see <see cref="RouteGuards"/>); a caller with "keep what is saved" semantics resolves them before it gets here, so a
/// header value must be filled in by now.
/// </summary>
public record RouteForm(
    string? Host, bool Wildcard, bool Redirect, string? Upstream, bool InsecureSkipVerify,
    string? FallbackUrl, bool RedirectPermanent, long? AuthenticationId, long? ProviderId,
    string? AcmeEmail, bool Enabled, string? Name, bool AllowEmbedding, int? ListenPort,
    string? UpstreamHost = null, IReadOnlyList<HeaderPair>? UpstreamHeaders = null,
    HeaderPair? RequireHeader = null, IReadOnlyList<string>? AllowedIps = null);

/// <summary>
/// The rules for a manually managed route, in one place for the edit page and the REST API: normalise the host,
/// check that the combination makes sense, and fill the <see cref="RouteConfig"/>.
/// </summary>
public static partial class RouteEditor
{
    /// <summary>Checks the form and copies it onto <paramref name="route"/>. Returns an error message, or null on success (then the route is changed).</summary>
    public static string? Apply(RouteForm form, RouteConfig route)
    {
        var listenPort = form.ListenPort is > 0 ? form.ListenPort : null;
        var host = (form.Host ?? "").Trim().ToLowerInvariant();
        var upstream = form.Upstream?.Trim();
        var providerId = form.ProviderId is > 0 ? form.ProviderId : null;
        var wildcard = listenPort is null && form.Wildcard;

        if (listenPort is null)
        {
            if (host.Length == 0) return "Host is required.";
            if (!IsHostName(host.StartsWith("*.") ? host[2..] : host))
                return "The host must be a plain domain name like app.example.com (no http://, path or port; write international names in their xn-- form).";
        }

        if (listenPort is not null)
        {
            // A port binding listens on its own port without host matching: a host is of no use there.
            host = "";
            if (listenPort > 65535) return "The port must be between 1 and 65535.";
            if (listenPort is 80 or 443 or 2019) return "Ports 80, 443 and 2019 are used by Caddy itself.";
            if (string.IsNullOrWhiteSpace(upstream)) return "A port-bound route requires an upstream.";
            providerId = null;
        }
        else if (wildcard)
        {
            if (!host.StartsWith("*.")) host = "*." + host.TrimStart('.');
            // Wildcards can only be validated through DNS-01, so a provider is mandatory.
            if (host.Count(c => c == '.') < 2) return "A wildcard host must include a domain, e.g. *.example.com.";
            if (providerId is null) return "A wildcard route requires a DNS provider (DNS-01 challenge).";
        }
        else
        {
            if (host.StartsWith("*.")) return "A single domain must not start with '*.'. Choose the 'Wildcard' type instead.";
            providerId = null;
        }

        var redirect = form.Redirect && listenPort is null;
        var redirectUrl = form.FallbackUrl?.Trim();
        if (redirect)
        {
            if (string.IsNullOrWhiteSpace(redirectUrl)) return "A redirect route needs a target URL.";
            if (!IsHttpUrl(redirectUrl)) return "The redirect target must be an http:// or https:// URL.";
        }
        else if (!string.IsNullOrEmpty(upstream) && !IsUpstream(upstream))
        {
            return "The upstream must look like backend:8080 or http(s)://backend:8080.";
        }

        var acmeEmail = form.AcmeEmail?.Trim();
        if (wildcard && !string.IsNullOrEmpty(acmeEmail) && !acmeEmail.Contains('@'))
            return "Please enter a valid ACME e-mail address.";

        // Guards apply to every kind of route (a redirect too); what is said to the upstream only makes sense for a proxy.
        var proxies = !redirect && !string.IsNullOrEmpty(upstream);
        var (upstreamHost, hostError) = RouteGuards.NormalizeUpstreamHost(form.UpstreamHost);
        if (hostError != null) return hostError;
        var upstreamHeaders = (form.UpstreamHeaders ?? [])
            .Select(h => new HeaderPair { Name = (h.Name ?? "").Trim(), Value = h.Value ?? "" }).ToList();
        var headersError = RouteGuards.CheckUpstreamHeaders(upstreamHeaders);
        if (headersError != null) return headersError;
        if (!proxies && (upstreamHost != null || upstreamHeaders.Count > 0))
            return "The Host header and the headers for the upstream only apply to a reverse proxy route that has an upstream.";
        var requireHeader = form.RequireHeader is null ? null
            : new HeaderPair { Name = (form.RequireHeader.Name ?? "").Trim(), Value = form.RequireHeader.Value ?? "" };
        var requireError = RouteGuards.CheckRequireHeader(requireHeader);
        if (requireError != null) return requireError;
        var clashError = RouteGuards.CheckNameClash(requireHeader, upstreamHeaders);
        if (clashError != null) return clashError;
        var (allowedIps, ipError) = RouteGuards.NormalizeIps(form.AllowedIps);
        if (ipError != null) return ipError;

        route.Name = !string.IsNullOrWhiteSpace(form.Name) ? form.Name.Trim()
                   : host.Length > 0 ? host
                   : $"port {listenPort}";
        route.Host = host;
        route.Wildcard = wildcard;
        route.ListenPort = listenPort;
        // Proxy and redirect are mutually exclusive.
        route.Upstream = redirect || string.IsNullOrEmpty(upstream) ? null : upstream;
        route.InsecureSkipVerify = !redirect && form.InsecureSkipVerify;
        route.AllowEmbedding = form.AllowEmbedding;
        route.FallbackUrl = redirect ? redirectUrl : null;
        route.RedirectPermanent = redirect && form.RedirectPermanent;
        route.AuthenticationId = form.AuthenticationId is > 0 ? form.AuthenticationId : null;
        route.ProviderId = providerId;
        // A per-domain ACME e-mail only applies to wildcard (DNS-01) certificates.
        route.AcmeEmail = wildcard && !string.IsNullOrEmpty(acmeEmail) ? acmeEmail : null;
        route.Enabled = form.Enabled;
        route.UpstreamHost = upstreamHost;
        route.UpstreamHeaders = upstreamHeaders;
        route.RequireHeader = requireHeader;
        route.AllowedIps = allowedIps;
        return null;
    }

    /// <summary>host, host:port, or an http(s) URL - what Caddy can dial.</summary>
    public static bool IsUpstream(string value)
    {
        if (value.Any(char.IsWhiteSpace)) return false;
        if (value.Contains("://"))
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   uri.Scheme is "http" or "https" or "h2c" && !string.IsNullOrEmpty(uri.Host);
        return HostAndPort().IsMatch(value);
    }

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !string.IsNullOrEmpty(uri.Host);

    /// <summary>A plain domain name (or IP address): no scheme, path or port.</summary>
    public static bool IsHostName(string value) => HostName().IsMatch(value);

    // Dot-separated labels of letters, digits, hyphen and underscore; a label starts and ends with a letter or digit.
    [GeneratedRegex(@"^[a-z0-9_]([a-z0-9_-]*[a-z0-9_])?(\.[a-z0-9_]([a-z0-9_-]*[a-z0-9_])?)*$", RegexOptions.IgnoreCase)]
    private static partial Regex HostName();

    [GeneratedRegex(@"^([A-Za-z0-9]([A-Za-z0-9._-]*[A-Za-z0-9])?|\[[0-9A-Fa-f:.]+\])(:\d{1,5})?$")]
    private static partial Regex HostAndPort();
}
