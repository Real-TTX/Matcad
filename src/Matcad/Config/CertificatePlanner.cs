using System.Net;

namespace Matcad.Config;

/// <summary>
/// Works out which TLS certificate serves each route, so the UI can make it
/// obvious whether a route is covered by a wildcard cert or would trigger an
/// individual (per-host) certificate. Mirrors how Caddy actually issues certs:
/// a wildcard cert <c>*.domain</c> (DNS-01) covers its single-label subdomains
/// and — when an apex route exists — the apex too; anything else concrete gets
/// its own cert; localhost/IP hosts use Caddy's internal CA.
/// </summary>
public static class CertificatePlanner
{
    public enum CertKind { Wildcard, Covered, Individual, Internal }

    public record Coverage(CertKind Kind, string Subject);

    public record WildcardCert(string WildcardHost, string Subjects, string Provider, int CoveredCount);

    public record CertPlan(List<WildcardCert> Wildcards, List<string> Individual, List<string> Internal);

    private static bool IsInternal(string h) =>
        h.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        h.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(h, out _);

    /// <summary>parent-domain -&gt; wildcard host, for every enabled wildcard route.</summary>
    private static Dictionary<string, string> WildcardParents(IEnumerable<RouteConfig> routes)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in routes.Where(r => r.Enabled && r.Wildcard && r.Host.StartsWith("*.")))
            d[r.Host[2..]] = r.Host;
        return d;
    }

    public static Coverage ForHost(string host, Dictionary<string, string> wildcardParents)
    {
        if (string.IsNullOrWhiteSpace(host)) return new(CertKind.Individual, host);
        if (host.StartsWith("*.")) return new(CertKind.Wildcard, host);
        if (IsInternal(host)) return new(CertKind.Internal, "internal");

        var immediateParent = host.Contains('.') ? host[(host.IndexOf('.') + 1)..] : host;
        if (wildcardParents.TryGetValue(immediateParent, out var w)) return new(CertKind.Covered, w);
        // Apex (example.com) is folded into the *.example.com cert's SANs.
        if (wildcardParents.TryGetValue(host, out var wa)) return new(CertKind.Covered, wa);
        return new(CertKind.Individual, host);
    }

    public static Coverage ForRoute(RouteConfig route, IEnumerable<RouteConfig> all) =>
        ForHost(route.Host, WildcardParents(all));

    /// <summary>The certificates Caddy will actually manage for the enabled routes.</summary>
    public static CertPlan Plan(IEnumerable<RouteConfig> all, List<ProviderConfig> providers)
    {
        var enabled = all.Where(r => r.Enabled && !string.IsNullOrWhiteSpace(r.Host)).ToList();
        var wp = WildcardParents(enabled);

        var wildcards = new List<WildcardCert>();
        foreach (var wr in enabled.Where(r => r.Wildcard && r.Host.StartsWith("*."))
                                   .OrderBy(r => r.Host, StringComparer.OrdinalIgnoreCase))
        {
            var parent = wr.Host[2..];
            var subjects = wr.Host;
            if (enabled.Any(x => x.Host.Equals(parent, StringComparison.OrdinalIgnoreCase)))
                subjects += ", " + parent;
            var provider = providers.FirstOrDefault(p => p.Id == wr.ProviderId)?.Name ?? "(no provider)";
            var covered = enabled.Count(x =>
            {
                var c = ForHost(x.Host, wp);
                return c.Kind == CertKind.Covered && c.Subject.Equals(wr.Host, StringComparison.OrdinalIgnoreCase);
            });
            wildcards.Add(new WildcardCert(wr.Host, subjects, provider, covered));
        }

        var individual = enabled.Where(x => ForHost(x.Host, wp).Kind == CertKind.Individual)
            .Select(x => x.Host).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();
        var internalHosts = enabled.Where(x => ForHost(x.Host, wp).Kind == CertKind.Internal)
            .Select(x => x.Host).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();

        return new CertPlan(wildcards, individual, internalHosts);
    }
}
