using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Matcad.Config;

namespace Matcad.Services;

/// <summary>The certificate Caddy shows for one host, as <see cref="ProxyMonitor"/> saw it.</summary>
/// <param name="Host">The name that was asked for (SNI); for a wildcard certificate a name below it.</param>
/// <param name="NotAfter">When the certificate expires; null when none could be read.</param>
/// <param name="Error">What is wrong (no TLS answer, not trusted, wrong name, expired); null = fine.</param>
public sealed record CertificateCheck(string Host, DateTimeOffset? NotAfter, string? Error);

/// <summary>
/// Watches the proxy for the platform status (<c>/api/state</c>, <c>/api/health</c>), so that those answers are cheap and never wait for Caddy:
/// every minute whether Caddy's admin API answers, and every six hours (and on request) the certificates Caddy really serves - a TLS handshake
/// with Caddy for every certificate of <see cref="CertificatePlanner"/> (a short timeout each), reading the expiry and whether it is trusted.
/// Hosts on Caddy's internal CA (localhost, IPs) and port-bound routes are left out. Caddy is reached at <c>Matcad:Caddy:TlsAddress</c>
/// (<c>host:port</c>), by default the host of the admin API on port 443.
/// </summary>
public sealed class ProxyMonitor : BackgroundService
{
    private static readonly TimeSpan CaddyInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CertificateInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly CaddyService _caddy;
    private readonly RouteProvider _routes;
    private readonly ConfigStore _store;
    private readonly IConfiguration _cfg;
    private readonly ILogger<ProxyMonitor> _log;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private volatile bool _certificatesRequested = true;
    private int _checking;

    public ProxyMonitor(CaddyService caddy, RouteProvider routes, ConfigStore store, IConfiguration cfg, ILogger<ProxyMonitor> log)
    {
        _caddy = caddy;
        _routes = routes;
        _store = store;
        _cfg = cfg;
        _log = log;
    }

    /// <summary>Whether Caddy's admin API answered at the last check; null = not checked yet.</summary>
    public bool? CaddyReachable { get; private set; }

    /// <summary>The certificates at the last check (empty before the first one).</summary>
    public IReadOnlyList<CertificateCheck> Certificates { get; private set; } = [];

    /// <summary>When the certificates were last checked; null = not yet.</summary>
    public DateTimeOffset? CertificatesCheckedAt { get; private set; }

    public bool CheckingCertificates => Volatile.Read(ref _checking) == 1;

    /// <summary>Checks the certificates now (in the background). False when a check is running already.</summary>
    public bool RequestCertificateCheck()
    {
        if (CheckingCertificates) return false;
        _certificatesRequested = true;
        Wake();
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give Caddy a moment after a common start.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (OperationCanceledException) { return; }

        var nextCertificates = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CaddyReachable = await _caddy.IsReachableAsync(stoppingToken);
                if (_certificatesRequested || DateTimeOffset.Now >= nextCertificates)
                {
                    _certificatesRequested = false;
                    await CheckCertificatesAsync(stoppingToken);
                    nextCertificates = DateTimeOffset.Now + CertificateInterval;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "The proxy check failed");
            }

            try { await _wake.WaitAsync(CaddyInterval, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }

    private void Wake()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    /// <summary>The names to ask Caddy for: one per certificate it manages.</summary>
    public static IReadOnlyList<string> ProbeHosts(IReadOnlyList<RouteConfig> routes, List<ProviderConfig> providers)
    {
        var portBound = routes.Where(r => r.ListenPort > 0).Select(r => r.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan = CertificatePlanner.Plan(routes.Where(r => !(r.ListenPort > 0)), providers);
        var hosts = new List<string>();
        foreach (var w in plan.Wildcards)
        {
            // A name the wildcard certificate covers: a route below it, else a made-up one.
            hosts.Add(w.CoveredHosts.FirstOrDefault(h => !portBound.Contains(h)) ?? "matcad-check." + w.WildcardHost[2..]);
        }
        hosts.AddRange(plan.Individual.Where(h => !portBound.Contains(h)));
        return hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task CheckCertificatesAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _checking, 1) == 1) return;
        try
        {
            var hosts = ProbeHosts(_routes.All(), _store.Providers);
            var (server, port) = TlsEndpoint();
            var results = new CertificateCheck[hosts.Count];
            using var limit = new SemaphoreSlim(4);
            await Task.WhenAll(hosts.Select(async (host, i) =>
            {
                await limit.WaitAsync(ct);
                try { results[i] = await ProbeAsync(server, port, host, ct); }
                finally { limit.Release(); }
            }));
            Certificates = results;
            CertificatesCheckedAt = DateTimeOffset.Now;
            _log.LogInformation("Checked {Count} certificates at {Server}:{Port}, {Errors} with a problem",
                results.Length, server, port, results.Count(r => r.Error != null));
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private (string Server, int Port) TlsEndpoint()
    {
        var configured = _cfg["Matcad:Caddy:TlsAddress"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var at = configured.LastIndexOf(':');
            return at > 0 && int.TryParse(configured[(at + 1)..], out var p) ? (configured[..at], p) : (configured.Trim(), 443);
        }

        return Uri.TryCreate(_caddy.AdminUrl, UriKind.Absolute, out var admin) ? (admin.Host, 443) : ("caddy", 443);
    }

    private static async Task<CertificateCheck> ProbeAsync(string server, int port, string host, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(server, port, timeout.Token);
            var errors = SslPolicyErrors.None;
            X509Certificate2? certificate = null;
            await using var tls = new SslStream(tcp.GetStream(), false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                // Read whatever Caddy shows; judge it below instead of failing the handshake.
                RemoteCertificateValidationCallback = (_, cert, _, policy) =>
                {
                    errors = policy;
                    if (cert != null) certificate = new X509Certificate2(cert);
                    return true;
                }
            }, timeout.Token);

            using (certificate)
            {
                if (certificate == null) return new CertificateCheck(host, null, "No certificate");
                var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero).ToLocalTime();
                string? error = notAfter <= DateTimeOffset.Now ? "Expired"
                    : errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ? "Wrong name"
                    : errors != SslPolicyErrors.None ? "Not trusted"
                    : null;
                return new CertificateCheck(host, notAfter, error);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new CertificateCheck(host, null, "No answer");
        }
        catch (Exception ex) when (ex is SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            return new CertificateCheck(host, null, "No TLS answer");
        }
    }

    public override void Dispose()
    {
        _wake.Dispose();
        base.Dispose();
    }
}
