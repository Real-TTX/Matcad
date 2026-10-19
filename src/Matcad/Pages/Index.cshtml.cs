using Matcad.Config;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages;

public class IndexModel : PageModel
{
    private readonly RequestLogQueries _log;
    private readonly RouteProvider _routes;
    private readonly CaddyService _caddy;
    public IndexModel(RequestLogQueries log, RouteProvider routes, CaddyService caddy) { _log = log; _routes = routes; _caddy = caddy; }

    /// <summary>The last push to Caddy when it failed (the routes on this page are then not what Caddy serves); null when it worked or none ran yet.</summary>
    public CaddyApplyResult? FailedPush => _caddy.LastApply is { Ok: false } last ? last : null;

    public int TotalRequests { get; private set; }
    public int Requests24h { get; private set; }
    public int ClientErrors24h { get; private set; }
    public int ServerErrors24h { get; private set; }
    public int ActiveRoutes { get; private set; }
    public int InactiveRoutes { get; private set; }
    public List<DomainGroup> Groups { get; private set; } = new();
    private Dictionary<string, string> _wildcardParents = new();

    public CertificatePlanner.Coverage Cert(RouteConfig r) =>
        CertificatePlanner.ForHost(r.Host, _wildcardParents);

    public class DomainGroup
    {
        public string BaseDomain { get; init; } = "";
        public List<RouteRow> Routes { get; } = new();
    }

    public class RouteRow
    {
        public RouteConfig Route { get; init; } = null!;
        public string Label { get; init; } = "";
        public DateTime? LastAccess { get; set; }
    }

    public async Task OnGetAsync()
    {
        var every = _routes.All();
        ActiveRoutes = every.Count(r => r.Enabled);
        InactiveRoutes = every.Count - ActiveRoutes;

        // Port bindings have no host and no place in the domain tree (they are listed on the Routes page).
        var allRoutes = every.Where(r => !string.IsNullOrWhiteSpace(r.Host)).ToList();
        _wildcardParents = allRoutes
            .Where(r => r.Enabled && r.Wildcard && r.Host.StartsWith("*."))
            .GroupBy(r => r.Host[2..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Host, StringComparer.OrdinalIgnoreCase);

        var traffic = await _log.DashboardAsync(allRoutes.Select(r => r.Host).Distinct().ToList(), HttpContext.RequestAborted);
        TotalRequests = traffic.Total;
        Requests24h = traffic.Last24h;
        ClientErrors24h = traffic.ClientErrors24h;
        ServerErrors24h = traffic.ServerErrors24h;
        var lastByHost = traffic.LastAccessByHost;

        // Build the domain tree automatically from the host names.
        foreach (var group in RouteTree.Build(allRoutes))
        {
            var domain = new DomainGroup { BaseDomain = group.BaseDomain };
            foreach (var route in group.Routes)
            {
                var row = new RouteRow { Route = route, Label = RouteTree.SubLabel(route.Host) };
                if (lastByHost.TryGetValue(route.Host, out var lastAccess)) row.LastAccess = lastAccess;
                domain.Routes.Add(row);
            }
            Groups.Add(domain);
        }
    }
}
