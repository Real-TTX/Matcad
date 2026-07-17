using Matcat.Config;
using Matcat.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcat.Pages.Authentications;

public class IndexModel : PageModel
{
    private readonly ConfigStore _store;
    public IndexModel(ConfigStore store) => _store = store;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Type { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public string? Dir { get; set; }

    public DataTable Table { get; private set; } = new();

    public void OnGet()
    {
        var items = _store.Authentications.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(Q))
            items = items.Where(a => a.Name.Contains(Q, StringComparison.OrdinalIgnoreCase));
        if (Enum.TryParse<AuthType>(Type, out var t))
            items = items.Where(a => a.Type == t);

        var sort = Sort ?? "name";
        var asc = (Dir ?? "asc") == "asc";
        items = sort switch
        {
            "type" => asc ? items.OrderBy(a => a.Type) : items.OrderByDescending(a => a.Type),
            _ => asc ? items.OrderBy(a => a.Name) : items.OrderByDescending(a => a.Name)
        };

        Table = new DataTable
        {
            Id = "auths",
            SearchValue = Q,
            SearchPlaceholder = "Authentication suchen…",
            Sort = sort,
            Dir = Dir ?? "asc",
            Filters =
            {
                new TableFilter
                {
                    Param = "type", Label = "Typ", Selected = Type,
                    Options = { ("", "Alle"), ("BasicAuth", "Basic Auth"), ("Matcat", "Matcat") }
                }
            },
            Columns =
            {
                new DataColumn("Name", "name"),
                new DataColumn("Typ", "type"),
                new DataColumn("Details"),
                new DataColumn("Verwendet von")
            },
            Rows = items.Select(a => new DataRow(
                Cell.Text(a.Name),
                a.Type == AuthType.BasicAuth ? Cell.Badge("Basic Auth", "info") : Cell.Badge("Matcat", "warn"),
                a.Type == AuthType.BasicAuth ? Cell.Muted($"{a.Users.Count} Benutzer") : Cell.Muted("Login-Portal"),
                Cell.Muted($"{_store.Routes.Count(r => r.AuthenticationId == a.Id)} Route(n)")
            ) { EditUrl = $"/Authentications/Edit?id={a.Id}" }).ToList()
        };
    }
}
