using System.Reflection;
using Matcat.Config;
using Matcat.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Matcat.Pages;

public class IndexModel : PageModel
{
    private readonly MatcatDbContext _db;
    private readonly ConfigStore _store;
    public IndexModel(MatcatDbContext db, ConfigStore store) { _db = db; _store = store; }

    public string Version { get; private set; } = "";
    public int TotalRequests { get; private set; }
    public int Requests24h { get; private set; }
    public int ClientErrors24h { get; private set; }
    public int ServerErrors24h { get; private set; }
    public List<RouteNode> Tree { get; private set; } = new();

    public class RouteNode
    {
        public RouteConfig Route { get; init; } = null!;
        public List<RouteNode> Children { get; } = new();
        public DateTime? LastAccess { get; set; }
        public string? LastIp { get; set; }
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

        // Latest access per host (one small query per configured route).
        var lastByHost = new Dictionary<string, (DateTime Ts, string Ip)>();
        foreach (var host in _store.Routes.Select(r => r.Host).Distinct())
        {
            var last = await _db.RequestLogs.Where(r => r.Host == host)
                .OrderByDescending(r => r.Timestamp).FirstOrDefaultAsync();
            if (last != null) lastByHost[host] = (last.Timestamp, last.RemoteIp);
        }

        // Build the hierarchical tree.
        var nodes = _store.Routes.ToDictionary(r => r.Id, r => new RouteNode { Route = r });
        foreach (var node in nodes.Values)
        {
            if (lastByHost.TryGetValue(node.Route.Host, out var la))
            {
                node.LastAccess = la.Ts;
                node.LastIp = la.Ip;
            }
        }
        foreach (var node in nodes.Values)
        {
            if (node.Route.ParentId is long pid && nodes.TryGetValue(pid, out var parent))
                parent.Children.Add(node);
            else
                Tree.Add(node);
        }
    }
}
