using Matcad.Config;
using Matcad.Data;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Matcad.Pages;

public class IndexModel : PageModel
{
    private readonly MatcadDbContext _db;
    private readonly RouteProvider _routes;
    public IndexModel(MatcadDbContext db, RouteProvider routes) { _db = db; _routes = routes; }

    public int TotalRequests { get; private set; }
    public int Requests24h { get; private set; }
    public int ClientErrors24h { get; private set; }
    public int ServerErrors24h { get; private set; }
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
        var since = DateTime.UtcNow.AddHours(-24);
        TotalRequests = await _db.RequestLogs.CountAsync();
        var last24h = await _db.RequestLogs
            .Where(r => r.Timestamp >= since)
            .GroupBy(r => r.Status / 100)
            .Select(g => new { StatusClass = g.Key, Count = g.Count() })
            .ToListAsync();
        Requests24h = last24h.Sum(x => x.Count);
        ClientErrors24h = last24h.Where(x => x.StatusClass == 4).Sum(x => x.Count);
        ServerErrors24h = last24h.Where(x => x.StatusClass >= 5).Sum(x => x.Count);

        // Port bindings have no host and no place in the domain tree (they are listed on the Routes page).
        var allRoutes = _routes.All().Where(r => !string.IsNullOrWhiteSpace(r.Host)).ToList();
        _wildcardParents = allRoutes
            .Where(r => r.Enabled && r.Wildcard && r.Host.StartsWith("*."))
            .GroupBy(r => r.Host[2..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Host, StringComparer.OrdinalIgnoreCase);

        // Latest access time per host - a single grouped query (scales to many hosts).
        var hosts = allRoutes.Select(r => r.Host).Distinct().ToList();
        var lastByHost = await _db.RequestLogs
            .Where(r => hosts.Contains(r.Host))
            .GroupBy(r => r.Host)
            .Select(g => new { Host = g.Key, Last = g.Max(x => x.Timestamp) })
            .ToDictionaryAsync(x => x.Host, x => x.Last);

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
