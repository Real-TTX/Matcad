using Matcat.Config;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcat.Pages.Routes;

public class IndexModel : PageModel
{
    private readonly ConfigStore _store;
    private readonly Matcat.Services.RouteProvider _routes;
    public IndexModel(ConfigStore store, Matcat.Services.RouteProvider routes) { _store = store; _routes = routes; }

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }

    public List<RouteTree.Group> Groups { get; private set; } = new();

    public string SubLabel(string host) => RouteTree.SubLabel(host);

    public string? AuthName(RouteConfig r) =>
        _store.Authentications.FirstOrDefault(a => a.Id == r.AuthenticationId)?.Name;

    public void OnGet()
    {
        var items = _routes.All().AsEnumerable();

        if (!string.IsNullOrWhiteSpace(Q))
            items = items.Where(r =>
                r.Host.Contains(Q, StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains(Q, StringComparison.OrdinalIgnoreCase));

        if (Status == "enabled") items = items.Where(r => r.Enabled);
        else if (Status == "disabled") items = items.Where(r => !r.Enabled);

        Groups = RouteTree.Build(items);
    }
}
