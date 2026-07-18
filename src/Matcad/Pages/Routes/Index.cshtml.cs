using Matcad.Config;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Routes;

public class IndexModel : PageModel
{
    private readonly ConfigStore _store;
    private readonly Matcad.Services.RouteProvider _routes;
    public IndexModel(ConfigStore store, Matcad.Services.RouteProvider routes) { _store = store; _routes = routes; }

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }

    public List<RouteTree.Group> Groups { get; private set; } = new();
    private Dictionary<string, string> _wildcardParents = new();

    public string SubLabel(string host) => RouteTree.SubLabel(host);

    public string? AuthName(RouteConfig r) =>
        _store.Authentications.FirstOrDefault(a => a.Id == r.AuthenticationId)?.Name;

    /// <summary>Certificate coverage for a route (computed against all routes, not the filtered view).</summary>
    public CertificatePlanner.Coverage Cert(RouteConfig r) =>
        CertificatePlanner.ForHost(r.Host, _wildcardParents);

    public void OnGet()
    {
        var all = _routes.All();
        // Coverage is judged against the full route set, independent of the filter.
        _wildcardParents = all
            .Where(r => r.Enabled && r.Wildcard && r.Host.StartsWith("*."))
            .GroupBy(r => r.Host[2..], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Host, StringComparer.OrdinalIgnoreCase);

        var items = all.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(Q))
            items = items.Where(r =>
                r.Host.Contains(Q, StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains(Q, StringComparison.OrdinalIgnoreCase));

        if (Status == "enabled") items = items.Where(r => r.Enabled);
        else if (Status == "disabled") items = items.Where(r => !r.Enabled);

        Groups = RouteTree.Build(items);
    }
}
