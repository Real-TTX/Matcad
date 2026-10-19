using Matcad.Config;
using Matcad.Controls;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Certificates;

/// <summary>
/// The certificates Caddy manages for the active routes, with what the last check saw (<see cref="ProxyMonitor"/>: a TLS handshake with
/// Caddy per certificate, every six hours and on request). Caddy renews on its own a month before the end; the page shows whether it did.
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>Under this many days left a certificate is shown as a warning (Caddy renews at about 30).</summary>
    public const int WarnDays = 14;

    private readonly ConfigStore _store;
    private readonly RouteProvider _routes;
    private readonly ProxyMonitor _monitor;
    public IndexModel(ConfigStore store, RouteProvider routes, ProxyMonitor monitor) { _store = store; _routes = routes; _monitor = monitor; }

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? State { get; set; }

    /// <summary>One certificate: what it is for, and how the last check found it.</summary>
    public sealed record Row(string Subject, bool Wildcard, string? Provider, IReadOnlyList<string> Covers, CertificateCheck? Check)
    {
        public int? DaysLeft => Check?.NotAfter is { } end ? (int)Math.Floor((end - DateTimeOffset.Now).TotalDays) : null;
        /// <summary>ok, warn, error or unknown (not checked yet).</summary>
        public string Tone => Check == null ? "unknown" : Check.Error != null ? "error" : DaysLeft < WarnDays ? "warn" : "ok";
    }

    public List<Row> Rows { get; private set; } = new();
    public List<string> Internal { get; private set; } = new();
    public DateTimeOffset? CheckedAt => _monitor.CertificatesCheckedAt;
    public bool Checking => _monitor.CheckingCertificates;
    public ToolbarModel Toolbar { get; private set; } = null!;

    public void OnGet()
    {
        var plan = CertificatePlanner.Plan(_routes.All(), _store.Providers);
        var checks = _monitor.Certificates;
        var all = new List<Row>();
        foreach (var w in plan.Wildcards)
        {
            var domain = w.WildcardHost[2..];
            // The monitor asks for one name below the wildcard (a covered route, else a made-up one).
            var check = checks.FirstOrDefault(c => c.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)
                                                   && !c.Host[..^(domain.Length + 1)].Contains('.'));
            all.Add(new Row(w.Subjects, true, w.Provider, w.CoveredHosts, check));
        }
        foreach (var host in plan.Individual)
            all.Add(new Row(host, false, null, [], checks.FirstOrDefault(c => c.Host.Equals(host, StringComparison.OrdinalIgnoreCase))));
        Internal = plan.Internal;

        IEnumerable<Row> shown = all;
        if (!string.IsNullOrWhiteSpace(Q))
            shown = shown.Where(r => r.Subject.Contains(Q, StringComparison.OrdinalIgnoreCase) || r.Covers.Any(h => h.Contains(Q, StringComparison.OrdinalIgnoreCase)));
        if (State is "ok" or "warn" or "error" or "unknown") shown = shown.Where(r => r.Tone == State);
        Rows = shown.OrderBy(r => r.Tone switch { "error" => 0, "warn" => 1, "unknown" => 2, _ => 3 }).ThenBy(r => r.DaysLeft ?? int.MaxValue).ThenBy(r => r.Subject).ToList();

        Toolbar = new ToolbarModel
        {
            Id = "certificates",
            Action = "/Certificates",
            Label = "Certificates",
            Search = Q,
            SearchPlaceholder = "Search certificates",
            SearchField = true,
            Noun = "certificates",
            Total = all.Count,
            Matching = Rows.Count,
            Filters = [new FilterField("state", "State", [new("error", "Problem"), new("warn", "Expires soon"), new("ok", "Valid"), new("unknown", "Not checked")], State)],
        };
    }

    public IActionResult OnPostCheck()
    {
        TempData["Flash"] = _monitor.RequestCertificateCheck()
            ? "The check is running. Reload the page in a moment."
            : "A check is running already.";
        return RedirectToPage();
    }
}
