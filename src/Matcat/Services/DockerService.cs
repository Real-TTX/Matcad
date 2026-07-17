using System.Text.RegularExpressions;
using Docker.DotNet;
using Docker.DotNet.Models;
using Matcat.Config;

namespace Matcat.Services;

/// <summary>
/// Discovers routes from a Docker host: every eligible running container becomes
/// a route <c>&lt;containername&gt;.&lt;BaseDomain&gt;</c> (or an explicit
/// matcat.host label) proxied to the container. Polls periodically and re-pushes
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
            LastError = null;
            return;
        }

        try
        {
            var routes = await Discover(settings, ct);
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

    private async Task<List<RouteConfig>> Discover(DockerSettings settings, CancellationToken ct)
    {
        var baseDomain = string.IsNullOrWhiteSpace(settings.BaseDomain)
            ? _store.Settings.BaseDomain : settings.BaseDomain;

        using var client = new DockerClientConfiguration(new Uri(settings.Endpoint)).CreateClient();
        var containers = await client.Containers.ListContainersAsync(
            new ContainersListParameters { All = false }, ct);

        var routes = new List<RouteConfig>();
        foreach (var c in containers)
        {
            var labels = c.Labels ?? new Dictionary<string, string>();
            if (settings.RequireEnableLabel &&
                !(labels.TryGetValue(DockerLabels.Enable, out var en) && en == "true"))
                continue;

            var name = (c.Names?.FirstOrDefault() ?? "").TrimStart('/');
            if (string.IsNullOrEmpty(name)) continue;

            var host = labels.TryGetValue(DockerLabels.Host, out var h) && !string.IsNullOrWhiteSpace(h)
                ? h.Trim()
                : $"{Sanitize(name)}.{baseDomain}";
            if (string.IsNullOrWhiteSpace(baseDomain) && !labels.ContainsKey(DockerLabels.Host))
                continue; // no base domain and no explicit host -> cannot name it

            var port = ResolvePort(labels, c);
            if (port == 0) continue; // nothing to proxy to

            long? authId = null;
            if (labels.TryGetValue(DockerLabels.Auth, out var authName) && !string.IsNullOrWhiteSpace(authName))
                authId = _store.Authentications
                    .FirstOrDefault(a => a.Name.Equals(authName.Trim(), StringComparison.OrdinalIgnoreCase))?.Id;

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
        return routes;
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
