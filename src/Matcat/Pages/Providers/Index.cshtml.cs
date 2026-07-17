using Matcat.Config;
using Matcat.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcat.Pages.Providers;

public class IndexModel : PageModel
{
    private readonly ConfigStore _store;
    public IndexModel(ConfigStore store) => _store = store;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public string? Dir { get; set; }

    public DataTable Table { get; private set; } = new();

    public void OnGet()
    {
        var items = _store.Providers.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(Q))
            items = items.Where(p =>
                p.Name.Contains(Q, StringComparison.OrdinalIgnoreCase) ||
                ProviderTypes.DisplayName(p.Type).Contains(Q, StringComparison.OrdinalIgnoreCase));

        var sort = Sort ?? "name";
        var asc = (Dir ?? "asc") == "asc";
        items = sort switch
        {
            "type" => asc ? items.OrderBy(p => p.Type) : items.OrderByDescending(p => p.Type),
            _ => asc ? items.OrderBy(p => p.Name) : items.OrderByDescending(p => p.Name)
        };

        Table = new DataTable
        {
            Id = "providers",
            SearchValue = Q,
            SearchPlaceholder = "Provider suchen…",
            Sort = sort,
            Dir = Dir ?? "asc",
            Columns =
            {
                new DataColumn("Name", "name"),
                new DataColumn("Typ", "type"),
                new DataColumn("Zugangsdaten")
            },
            Rows = items.Select(p => new DataRow(
                Cell.Text(p.Name),
                Cell.Badge(ProviderTypes.DisplayName(p.Type), "info"),
                Cell.Muted(string.Join(", ", p.Credentials.Keys))
            ) { EditUrl = $"/Providers/Edit?id={p.Id}" }).ToList()
        };
    }
}
