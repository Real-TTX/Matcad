using Matcad.Config;

namespace Matcad.Services;

/// <summary>
/// Holds the routes currently derived from Docker containers. Written by
/// <see cref="DockerService"/>, read by <see cref="RouteProvider"/>. Assignment
/// is atomic (whole list swapped), so readers never see a partial update.
/// </summary>
public class DockerRouteCache
{
    private volatile List<RouteConfig> _routes = new();
    public IReadOnlyList<RouteConfig> Routes => _routes;
    public void Set(List<RouteConfig> routes) => _routes = routes;
}

/// <summary>
/// Single source of truth for "all routes" = manually managed routes
/// (routes.json) merged with the self-exposing system route and routes derived
/// from Docker. On a host collision the more specific source wins in the order
/// manual &gt; system &gt; docker. Used by the UI and the Caddy config generator.
/// </summary>
public class RouteProvider
{
    private readonly ConfigStore _store;
    private readonly DockerRouteCache _docker;
    private readonly IConfiguration _cfg;

    public RouteProvider(ConfigStore store, DockerRouteCache docker, IConfiguration cfg)
    {
        _store = store;
        _docker = docker;
        _cfg = cfg;
    }

    private string SelfUpstream => _cfg["Matcad:SelfUpstream"] ?? "matcad:4433";

    /// <summary>The read-only route that exposes the Matcad UI + login portal
    /// through Caddy, or null when not configured/enabled.</summary>
    public RouteConfig? SystemRoute()
    {
        var s = _store.Settings;
        if (!s.SystemRouteEnabled || string.IsNullOrWhiteSpace(s.MatcadHost)) return null;
        return new RouteConfig
        {
            Host = s.MatcadHost.Trim(),
            Name = "Matcad UI",
            Upstream = $"http://{SelfUpstream}",
            Enabled = true,
            Source = "system",
            SourceDetail = "Matcad"
        };
    }

    public List<RouteConfig> All()
    {
        var byHost = new Dictionary<string, RouteConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _store.Routes)
            if (!string.IsNullOrWhiteSpace(r.Host)) byHost[r.Host] = r;

        var system = SystemRoute();
        if (system != null && !byHost.ContainsKey(system.Host)) byHost[system.Host] = system;

        foreach (var r in _docker.Routes)
            if (!string.IsNullOrWhiteSpace(r.Host) && !byHost.ContainsKey(r.Host)) byHost[r.Host] = r;
        return byHost.Values.ToList();
    }
}
