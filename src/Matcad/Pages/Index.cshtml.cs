using System.Reflection;
using Matcad.Config;
using Matcad.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Matcad.Pages;

public class IndexModel : PageModel
{
    private readonly MatcadDbContext _db;
    private readonly ConfigStore _store;
    private readonly Matcad.Services.RouteProvider _routes;
    public IndexModel(MatcadDbContext db, ConfigStore store, Matcad.Services.RouteProvider routes)
    { _db = db; _store = store; _routes = routes; }

    public string Version { get; private set; } = "";
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
        Version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "local";

        var since = DateTime.UtcNow.AddHours(-24);
        TotalRequests = await _db.RequestLogs.CountAsync();
        Requests24h = await _db.RequestLogs.CountAsync(r => r.Timestamp >= since);
        ClientErrors24h = await _db.RequestLogs.CountAsync(r => r.Timestamp >= since && r.Status >= 400 && r.Status < 500);
        ServerErrors24h = await _db.RequestLogs.CountAsync(r => r.Timestamp >= since && r.Status >= 500);

        var allRoutes = _routes.All();
        _wildcardParents = allRoutes
            .Where(r => r.Enabled && r.Wildcard && r.Host.StartsWith("*."))
            .GroupBy(r => r.Host[2..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Host, StringComparer.OrdinalIgnoreCase);

        // Latest access time per host - a single grouped query (scales to many hosts).
        var hosts = allRoutes.Select(r => r.Host)
            .Where(h => !string.IsNullOrWhiteSpace(h)).Distinct().ToList();
        var lastByHost = await _db.RequestLogs
            .Where(r => hosts.Contains(r.Host))
            .GroupBy(r => r.Host)
            .Select(g => new { Host = g.Key, Last = g.Max(x => x.Timestamp) })
            .ToDictionaryAsync(x => x.Host, x => x.Last);

        // Build the domain tree automatically from the host names.
        foreach (var group in RouteTree.Build(allRoutes))
        {
            var g = new DomainGroup { BaseDomain = group.BaseDomain };
            foreach (var route in group.Routes)
            {
                var row = new RouteRow { Route = route, Label = RouteTree.SubLabel(route.Host) };
                if (lastByHost.TryGetValue(route.Host, out var la)) row.LastAccess = la;
                g.Routes.Add(row);
            }
            Groups.Add(g);
        }
    }
}
