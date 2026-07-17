namespace Matcad.Config;

/// <summary>
/// Groups routes into a domain tree derived automatically from the host name:
/// every route is nested under its base domain (registrable domain), and the
/// leftmost part of the host is shown as the indented sub-label.
///
///   example.com
///     app        app.example.com        http://…
///     api        api.example.com        http://…
///     *          *.example.com          http://fallback
///
/// The base domain is approximated as the last two labels (no public-suffix
/// list); wildcard hosts (*.domain) group under "domain" and sort last.
/// </summary>
public static class RouteTree
{
    public record Group(string BaseDomain, List<RouteConfig> Routes);

    /// <summary>Registrable base domain, e.g. app.example.com -&gt; example.com.</summary>
    public static string BaseDomain(string host)
    {
        var h = host.StartsWith("*.") ? host[2..] : host;
        var labels = h.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length >= 2 ? string.Join('.', labels[^2..]) : h;
    }

    /// <summary>The indented label shown under the base domain
    /// ("@" for the apex, "*" for the wildcard).</summary>
    public static string SubLabel(string host)
    {
        var baseDomain = BaseDomain(host);
        if (string.Equals(host, baseDomain, StringComparison.OrdinalIgnoreCase)) return "@";
        if (host.EndsWith("." + baseDomain, StringComparison.OrdinalIgnoreCase))
            return host[..^(baseDomain.Length + 1)];
        return host;
    }

    /// <summary>Groups and sorts routes: groups by base domain (alphabetical);
    /// within a group the apex first, subdomains alphabetically, wildcard last.</summary>
    public static List<Group> Build(IEnumerable<RouteConfig> routes)
    {
        return routes
            .GroupBy(r => BaseDomain(r.Host), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Group(g.Key, g.OrderBy(SortKey).ThenBy(r => r.Host, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }

    // apex (0) < normal subdomain (1) < wildcard (2)
    private static int SortKey(RouteConfig r)
    {
        var label = SubLabel(r.Host);
        if (label == "@") return 0;
        if (label.StartsWith("*")) return 2;
        return 1;
    }
}
