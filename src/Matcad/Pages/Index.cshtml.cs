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

        // Latest access time per host (one small query per configured route host).
        var lastByHost = new Dictionary<string, DateTime>();
        foreach (var host in allRoutes.Select(r => r.Host).Distinct())
        {
            var last = await _db.RequestLogs.Where(r => r.Host == host)
                .OrderByDescending(r => r.Timestamp).FirstOrDefaultAsync();
            if (last != null) lastByHost[host] = last.Timestamp;
        }

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
