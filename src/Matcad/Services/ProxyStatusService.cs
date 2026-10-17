using Matcad.Config;
using Matcad.Data;
using Microsoft.EntityFrameworkCore;

namespace Matcad.Services;

/// <summary>What the proxy looks like right now, in numbers (the tiles of the platform overview).</summary>
public sealed record ProxyStatus(
    int Routes,
    int RoutesDisabled,
    int UpstreamErrorHosts,
    int Certificates,
    DateTimeOffset? SoonestExpiry,
    int CertificateErrors,
    bool CertificatesChecked,
    int Providers,
    bool? CaddyReachable,
    CaddyApplyResult? LastApply);

/// <summary>
/// Puts the platform status together from the config, the request log and <see cref="ProxyMonitor"/>; all of it is cheap (no call to Caddy).
/// </summary>
public sealed class ProxyStatusService
{
    /// <summary>The window in which a host counts as failing when its upstream answered 502 or 504 through Caddy.</summary>
    public static readonly TimeSpan UpstreamErrorWindow = TimeSpan.FromHours(1);

    private readonly ConfigStore _store;
    private readonly RouteProvider _routes;
    private readonly ProxyMonitor _monitor;
    private readonly CaddyService _caddy;
    private readonly MatcadDbContext _db;

    public ProxyStatusService(ConfigStore store, RouteProvider routes, ProxyMonitor monitor, CaddyService caddy, MatcadDbContext db)
    {
        _store = store;
        _routes = routes;
        _monitor = monitor;
        _caddy = caddy;
        _db = db;
    }

    public async Task<ProxyStatus> GetAsync(CancellationToken ct = default)
    {
        var all = _routes.All();
        var plan = CertificatePlanner.Plan(all, _store.Providers);
        var checks = _monitor.Certificates;
        return new ProxyStatus(
            all.Count(r => r.Enabled),
            all.Count(r => !r.Enabled),
            await CountUpstreamErrorHostsAsync(ct),
            plan.Wildcards.Count + plan.Individual.Count,
            checks.Where(c => c.NotAfter != null).Select(c => c.NotAfter).Min(),
            checks.Count(c => c.Error != null),
            _monitor.CertificatesCheckedAt != null,
            _store.Providers.Count,
            _monitor.CaddyReachable,
            _caddy.LastApply);
    }

    /// <summary>Hosts whose upstream could not be reached (Caddy answered 502 Bad Gateway or 504 Gateway Timeout) in the last hour, from the access log.</summary>
    public Task<int> CountUpstreamErrorHostsAsync(CancellationToken ct = default)
    {
        var since = DateTime.UtcNow - UpstreamErrorWindow;
        return _db.RequestLogs
            .Where(r => r.Timestamp >= since && (r.Status == 502 || r.Status == 504))
            .Select(r => r.Host)
            .Distinct()
            .CountAsync(ct);
    }
}
