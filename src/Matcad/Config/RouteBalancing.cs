using System.Text.RegularExpressions;

namespace Matcad.Config;

/// <summary>
/// The rules for several upstreams on one route: the extra upstreams, how a request picks one (load balancing) and the active health
/// check Caddy runs against each. Shared by the route page (through <see cref="RouteEditor.Apply"/>), the generator (a stored route that
/// breaks them is not served) and the health monitor.
/// </summary>
public static partial class RouteBalancing
{
    public const string RoundRobin = "round_robin";
    public const string LeastConn = "least_conn";
    public const string First = "first";

    /// <summary>The load-balancing policies offered (Caddy's names).</summary>
    public static readonly string[] Policies = [RoundRobin, LeastConn, First];

    public const int MaxUpstreams = 10;
    public const int MinInterval = 5;
    public const int MaxInterval = 3600;

    /// <summary>Turns "http://host:8080", "https://host" or "host:8080" into a Caddy dial address and whether it speaks TLS.</summary>
    public static (string Dial, bool IsHttps) ParseDial(string upstream)
    {
        upstream = upstream.Trim();
        if (upstream.Contains("://") && Uri.TryCreate(upstream, UriKind.Absolute, out var uri))
        {
            var isHttps = uri.Scheme == "https";
            var port = uri.Port > 0 ? uri.Port : (isHttps ? 443 : 80);
            return ($"{uri.Host}:{port}", isHttps);
        }
        // Assume host:port; default to :80 if no port given.
        return (upstream.Contains(':') ? upstream : $"{upstream}:80", false);
    }

    /// <summary>
    /// Checks the balancing settings of a route. The extra upstreams have to look like an upstream, speak the same protocol as the main one
    /// (one TLS transport serves them all) and differ from each other; the health path is a plain path (no spaces, <c>?</c> only for a query,
    /// no <c>{ }</c>: Caddy fills placeholders in), the interval 5 s to an hour, the status a code 100-599 or a class 1-5 (2 = any 2xx).
    /// </summary>
    public static string? Check(RouteConfig route)
    {
        if (!Policies.Contains(route.LoadBalancing)) return "Load balancing: choose round robin, least connections or first available.";
        var upstreams = route.AllUpstreams;
        if (route.ExtraUpstreams.Count > 0 && upstreams.Count == 0) return "More upstreams need the upstream of the route first.";
        if (upstreams.Count > MaxUpstreams) return $"Upstreams: at most {MaxUpstreams}.";
        foreach (var u in route.ExtraUpstreams)
            if (!RouteEditor.IsUpstream(u)) return $"Upstream '{Short(u)}': it must look like backend:8080 or http(s)://backend:8080.";
        if (upstreams.Count > 1)
        {
            var dials = upstreams.Select(ParseDial).ToList();
            if (dials.Any(d => d.IsHttps != dials[0].IsHttps))
                return "Upstreams: all of them must be http or all https (one connection setting serves them all).";
            var twice = dials.GroupBy(d => d.Dial, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
            if (twice != null) return $"Upstreams: '{Short(twice.Key)}' is listed twice.";
        }
        if (route.HealthPath != null)
        {
            if (upstreams.Count == 0) return "A health check needs an upstream.";
            if (route.HealthPath.Length > 200 || !HealthPathPattern().IsMatch(route.HealthPath))
                return "Health check: the path must start with / and contain no spaces, { or } (like /health).";
            if (route.HealthIntervalSeconds is < MinInterval or > MaxInterval)
                return $"Health check: the interval must be between {MinInterval} and {MaxInterval} seconds.";
            if (!(route.HealthStatus is >= 1 and <= 5 or >= 100 and <= 599))
                return "Health check: the expected status must be a code like 200 or a class like 2xx.";
        }
        return null;
    }

    /// <summary>Reads "200" or "2xx" into the stored form (200 or 2); null when it is neither.</summary>
    public static int? ParseStatus(string? text)
    {
        var t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length == 3 && t.EndsWith("xx") && t[0] is >= '1' and <= '5') return t[0] - '0';
        return int.TryParse(t, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var code) && code is >= 100 and <= 599 ? code : null;
    }

    /// <summary>The stored status as the page shows it: 2 is "2xx".</summary>
    public static string FormatStatus(int status) => status is >= 1 and <= 5 ? $"{status}xx" : status.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whether an answer with this status counts as healthy.</summary>
    public static bool IsExpected(int expected, int actual) => expected is >= 1 and <= 5 ? actual / 100 == expected : actual == expected;

    private static string Short(string? text) => text is { Length: > 40 } ? text[..40] + "..." : text ?? "";

    // Visible ASCII without space, braces, # and backslash: a path and an optional query.
    [GeneratedRegex(@"^/[A-Za-z0-9._~!$&'()*+,;=:@%/?-]*\z")]
    private static partial Regex HealthPathPattern();
}
