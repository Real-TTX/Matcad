using Matcat.Config;
using Matcat.Controls;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcat.Pages.Routes;

public class IndexModel : PageModel
{
    private readonly ConfigStore _store;
    public IndexModel(ConfigStore store) => _store = store;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public string? Dir { get; set; }

    public DataTable Table { get; private set; } = new();

    public void OnGet()
    {
        var items = _store.Routes.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(Q))
            items = items.Where(r =>
                r.Host.Contains(Q, StringComparison.OrdinalIgnoreCase) ||
                r.Name.Contains(Q, StringComparison.OrdinalIgnoreCase));

        if (Status == "enabled") items = items.Where(r => r.Enabled);
        else if (Status == "disabled") items = items.Where(r => !r.Enabled);

        var sort = Sort ?? "host";
        var asc = (Dir ?? "asc") == "asc";
        items = sort switch
        {
            "name" => asc ? items.OrderBy(r => r.Name) : items.OrderByDescending(r => r.Name),
            "status" => asc ? items.OrderBy(r => r.Enabled) : items.OrderByDescending(r => r.Enabled),
            _ => asc ? items.OrderBy(r => r.Host) : items.OrderByDescending(r => r.Host)
        };

        Table = new DataTable
        {
            Id = "routes",
            SearchValue = Q,
            SearchPlaceholder = "Route suchen…",
            Sort = sort,
            Dir = Dir ?? "asc",
            Filters =
            {
                new TableFilter
                {
                    Param = "status", Label = "Status", Selected = Status,
                    Options = { ("", "Alle"), ("enabled", "Aktiv"), ("disabled", "Inaktiv") }
                }
            },
            Columns =
            {
                new DataColumn("Host", "host"),
                new DataColumn("Name", "name"),
                new DataColumn("Ziel"),
                new DataColumn("Auth"),
                new DataColumn("Status", "status")
            },
            Rows = items.Select(r => new DataRow(
                Cell.Raw($"<span class=\"host\">{System.Net.WebUtility.HtmlEncode(r.Host)}</span>" +
                         (r.Wildcard ? " <span class=\"badge badge-warn\">Wildcard</span>" : "")),
                Cell.Text(r.Name),
                Target(r),
                AuthName(r),
                r.Enabled ? Cell.Badge("Aktiv", "success") : Cell.Badge("Inaktiv", "muted")
            ) { EditUrl = $"/Routes/Edit?id={r.Id}" }).ToList()
        };
    }

    private IHtmlContent AuthName(RouteConfig r)
    {
        var auth = _store.Authentications.FirstOrDefault(a => a.Id == r.AuthenticationId);
        return auth == null ? Cell.Muted("—") : Cell.Badge(auth.Name, "info");
    }

    private static IHtmlContent Target(RouteConfig r)
    {
        if (!string.IsNullOrWhiteSpace(r.Upstream)) return Cell.Muted(r.Upstream);
        if (!string.IsNullOrWhiteSpace(r.FallbackUrl)) return Cell.Muted($"↪ {r.FallbackUrl}");
        return Cell.Muted("—");
    }
}
