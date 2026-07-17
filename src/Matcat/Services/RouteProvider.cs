using Matcat.Config;

namespace Matcat.Services;

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
/// (routes.json) merged with routes derived from Docker. On a host collision
/// the manual route wins. Used by both the UI and the Caddy config generator.
/// </summary>
public class RouteProvider
{
    private readonly ConfigStore _store;
    private readonly DockerRouteCache _docker;

    public RouteProvider(ConfigStore store, DockerRouteCache docker)
    {
        _store = store;
        _docker = docker;
    }

    public List<RouteConfig> All()
    {
        var byHost = new Dictionary<string, RouteConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _store.Routes)
            if (!string.IsNullOrWhiteSpace(r.Host)) byHost[r.Host] = r;
        foreach (var r in _docker.Routes)
            if (!string.IsNullOrWhiteSpace(r.Host) && !byHost.ContainsKey(r.Host)) byHost[r.Host] = r;
        return byHost.Values.ToList();
    }
}
