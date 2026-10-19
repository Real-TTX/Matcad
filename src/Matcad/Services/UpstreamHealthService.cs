using System.Collections.Concurrent;
using Matcad.Config;

namespace Matcad.Services;

/// <summary>The answer of one upstream to the health check: healthy, the status it gave, or why there was none.</summary>
public sealed record UpstreamHealth(string Upstream, bool Healthy, int? Status, string? Error);

/// <summary>The last health check of a route with an active check.</summary>
public sealed record RouteHealth(IReadOnlyList<UpstreamHealth> Upstreams, DateTimeOffset CheckedAt)
{
    public int HealthyCount => Upstreams.Count(u => u.Healthy);
}

/// <summary>
/// Runs the active health checks of the routes once more, from Matcad, for the routes list. Caddy runs them too (and is the one that leaves
/// an unhealthy upstream out), but its admin API does not tell which upstream it considers healthy; so Matcad asks the same path with the
/// same interval and expected status. It sees the upstreams from its own container: on the network it shares with Caddy that is the same.
/// </summary>
public sealed class UpstreamHealthService : BackgroundService
{
    public const string Client = "health";
    public const string InsecureClient = "health-insecure";
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly ConfigStore _store;
    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _time;
    private readonly ILogger<UpstreamHealthService> _log;
    private readonly ConcurrentDictionary<long, RouteHealth> _results = new();
    // Which settings a result belongs to: a route whose upstreams or check changed starts over.
    private readonly ConcurrentDictionary<long, string> _keys = new();

    public UpstreamHealthService(ConfigStore store, IHttpClientFactory http, TimeProvider time, ILogger<UpstreamHealthService> log)
    {
        _store = store;
        _http = http;
        _time = time;
        _log = log;
    }

    /// <summary>The last result for a route, or null (no active check, or not checked yet since the start or the last change).</summary>
    public RouteHealth? For(RouteConfig route) =>
        route.HealthPath != null && _results.TryGetValue(route.Id, out var health) && _keys.TryGetValue(route.Id, out var key) && key == KeyOf(route)
            ? health : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, _time);
        do
        {
            try { await CheckDueAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogWarning(ex, "Upstream health check failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Checks every route whose interval has passed (or that is new or changed).</summary>
    public async Task CheckDueAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var routes = _store.Routes.Where(r => r.Enabled && r.HealthPath != null && r.AllUpstreams.Count > 0 && RouteBalancing.Check(r) == null).ToList();
        foreach (var id in _results.Keys.Except(routes.Select(r => r.Id)).ToList())
        {
            _results.TryRemove(id, out _);
            _keys.TryRemove(id, out _);
        }
        foreach (var route in routes)
        {
            var key = KeyOf(route);
            var fresh = _keys.TryGetValue(route.Id, out var old) && old == key && _results.TryGetValue(route.Id, out var last)
                        && now - last.CheckedAt < TimeSpan.FromSeconds(route.HealthIntervalSeconds);
            if (fresh) continue;
            var results = await Task.WhenAll(route.AllUpstreams.Select(u => ProbeAsync(route, u, ct)));
            _results[route.Id] = new RouteHealth(results, now);
            _keys[route.Id] = key;
        }
    }

    private async Task<UpstreamHealth> ProbeAsync(RouteConfig route, string upstream, CancellationToken ct)
    {
        var (dial, isHttps) = RouteBalancing.ParseDial(upstream);
        var client = _http.CreateClient(isHttps && route.InsecureSkipVerify ? InsecureClient : Client);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await client.GetAsync($"{(isHttps ? "https" : "http")}://{dial}{route.HealthPath}", HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var status = (int)response.StatusCode;
            return new UpstreamHealth(upstream, RouteBalancing.IsExpected(route.HealthStatus, status), status, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new UpstreamHealth(upstream, false, null, "no answer within 5 s");
        }
        catch (HttpRequestException ex)
        {
            return new UpstreamHealth(upstream, false, null, ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "unknown host",
                HttpRequestError.SecureConnectionError => "TLS failed",
                _ => "unreachable"
            });
        }
    }

    private static string KeyOf(RouteConfig r) =>
        string.Join('\n', r.AllUpstreams) + '\n' + r.HealthPath + '\n' + r.HealthStatus + '\n' + r.InsecureSkipVerify;
}
