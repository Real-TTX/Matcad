using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using Matcad.Config;

namespace Matcad.Services;

/// <summary>A running container as seen by the discovery, whether or not it is
/// bound to a route — so the UI can show the full inventory and explain why a
/// container is (not) exposed.</summary>
public record DockerContainerInfo(
    string Name, string Image, string Ports, bool Bound, string? Host, string? AuthName, string Status);

/// <summary>
/// Discovers routes from a Docker host: every eligible running container becomes
/// a route <c>&lt;containername&gt;.&lt;BaseDomain&gt;</c> (or an explicit
/// matcad.host label) proxied to the container. Polls periodically and re-pushes
/// the Caddy config when the derived set changes.
/// </summary>
public class DockerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    private readonly ConfigStore _store;
    private readonly DockerRouteCache _cache;
    private readonly CaddyService _caddy;
    private readonly ILogger<DockerService> _log;
    private readonly SemaphoreSlim _refresh = new(0);

    public DockerService(ConfigStore store, DockerRouteCache cache, CaddyService caddy, ILogger<DockerService> log)
    {
        _store = store;
        _cache = cache;
        _caddy = caddy;
        _log = log;
    }

    /// <summary>Wake the loop immediately (e.g. after the settings changed).</summary>
    public void RequestRefresh() { try { _refresh.Release(); } catch (SemaphoreFullException) { } }

    public string? LastError { get; private set; }

    /// <summary>All running containers seen on the last scan (bound and unbound).</summary>
    public IReadOnlyList<DockerContainerInfo> Containers { get; private set; } = new List<DockerContainerInfo>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await DiscoverAndApply(stoppingToken);
            // Wait for the poll interval or an explicit refresh request.
            await _refresh.WaitAsync(PollInterval, stoppingToken).ContinueWith(_ => { }, stoppingToken);
        }
    }

    private async Task DiscoverAndApply(CancellationToken ct)
    {
        var settings = _store.Settings.Docker;
        if (!settings.Enabled)
        {
            if (_cache.Routes.Count > 0) { _cache.Set(new()); await _caddy.ApplyAsync(ct); }
            Containers = new List<DockerContainerInfo>();
            LastError = null;
            return;
        }

        try
        {
            var (routes, inventory) = await Discover(settings, ct);
            Containers = inventory;
            LastError = null;
            if (!SameHosts(routes, _cache.Routes))
            {
                _cache.Set(routes);
                await _caddy.ApplyAsync(ct);
                _log.LogInformation("Docker discovery: {Count} route(s).", routes.Count);
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log.LogWarning(ex, "Docker discovery failed ({Endpoint})", settings.Endpoint);
        }
    }

    private async Task<(List<RouteConfig> Routes, List<DockerContainerInfo> Inventory)> Discover(
        DockerSettings settings, CancellationToken ct)
    {
        var baseDomain = string.IsNullOrWhiteSpace(settings.BaseDomain)
            ? _store.Settings.BaseDomain : settings.BaseDomain;

        using var client = new DockerClientConfiguration(new Uri(settings.Endpoint)).CreateClient();
        var containers = await client.Containers.ListContainersAsync(
            new ContainersListParameters { All = false }, ct);

        var routes = new List<RouteConfig>();
        var inventory = new List<DockerContainerInfo>();

        foreach (var c in containers)
        {
            var labels = c.Labels ?? new Dictionary<string, string>();
            var name = (c.Names?.FirstOrDefault() ?? "").TrimStart('/');
            if (string.IsNullOrEmpty(name)) continue;
            var image = c.Image ?? "";
            var portsText = FormatPorts(c);

            // Work out whether this container is bound to a route, and if not, why.
            string? host = null, authName = null, status;
            long? authId = null;
            var port = ResolvePort(labels, c);
            var hasEnable = labels.TryGetValue(DockerLabels.Enable, out var en) && en == "true";
            var hasHostLabel = labels.TryGetValue(DockerLabels.Host, out var h) && !string.IsNullOrWhiteSpace(h);

            if (settings.RequireEnableLabel && !hasEnable)
                status = "Ignored · no matcad.enable=true";
            else if (string.IsNullOrWhiteSpace(baseDomain) && !hasHostLabel)
                status = "Ignored · no host label and no base domain";
            else if (port == 0)
                status = "Ignored · no port to proxy";
            else
            {
                host = hasHostLabel ? h!.Trim() : $"{Sanitize(name)}.{baseDomain}";
                if (labels.TryGetValue(DockerLabels.Auth, out var an) && !string.IsNullOrWhiteSpace(an))
                {
                    authName = an.Trim();
                    authId = _store.Authentications
                        .FirstOrDefault(a => a.Name.Equals(authName, StringComparison.OrdinalIgnoreCase))?.Id;
                }
                status = "Bound";
                routes.Add(new RouteConfig
                {
                    Host = host,
                    Name = name,
                    Wildcard = host.StartsWith("*."),
                    Upstream = $"http://{name}:{port}",
                    AuthenticationId = authId,
                    Enabled = true,
                    Source = "docker",
                    SourceDetail = name
                });
            }

            inventory.Add(new DockerContainerInfo(name, image, portsText,
                status == "Bound", host, authName, status));
        }

        inventory = inventory.OrderByDescending(i => i.Bound).ThenBy(i => i.Name).ToList();
        return (routes, inventory);
    }

    private static string FormatPorts(ContainerListResponse c)
    {
        var ports = (c.Ports ?? new List<Port>())
            .Where(x => string.Equals(x.Type, "tcp", StringComparison.OrdinalIgnoreCase) && x.PrivatePort > 0)
            .Select(x => (int)x.PrivatePort).Distinct().OrderBy(x => x).ToList();
        return ports.Count == 0 ? "—" : string.Join(", ", ports);
    }

    private static int ResolvePort(IDictionary<string, string> labels, ContainerListResponse c)
    {
        if (labels.TryGetValue(DockerLabels.Port, out var p) && int.TryParse(p, out var explicitPort))
            return explicitPort;
        // Otherwise the lowest private TCP port the container exposes.
        var ports = (c.Ports ?? new List<Port>())
            .Where(x => string.Equals(x.Type, "tcp", StringComparison.OrdinalIgnoreCase) && x.PrivatePort > 0)
            .Select(x => (int)x.PrivatePort)
            .OrderBy(x => x)
            .ToList();
        return ports.FirstOrDefault();
    }

    private static string Sanitize(string name) =>
        Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9-]", "-").Trim('-');

    private static bool SameHosts(List<RouteConfig> a, IReadOnlyList<RouteConfig> b)
    {
        if (a.Count != b.Count) return false;
        var sa = a.Select(r => $"{r.Host}|{r.Upstream}|{r.AuthenticationId}").OrderBy(x => x);
        var sb = b.Select(r => $"{r.Host}|{r.Upstream}|{r.AuthenticationId}").OrderBy(x => x);
        return sa.SequenceEqual(sb);
    }
}
