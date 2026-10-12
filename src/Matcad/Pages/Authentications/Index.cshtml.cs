using Matcad.Config;
using Matcad.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Authentications;

public class IndexModel : PageModel
{
    private readonly ConfigStore _store;
    public IndexModel(ConfigStore store) => _store = store;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Type { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public string? Dir { get; set; }

    /// <summary>The search, filter and sort of the list (rule LIST-03).</summary>
    public ToolbarModel Toolbar { get; private set; } = null!;
    public IReadOnlyList<AuthenticationConfig> Items { get; private set; } = [];

    public int RouteCount(AuthenticationConfig a) => _store.Routes.Count(r => r.AuthenticationId == a.Id);

    public void OnGet()
    {
        var items = _store.Authentications.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(Q))
            items = items.Where(a => a.Name.Contains(Q, StringComparison.OrdinalIgnoreCase));
        if (Enum.TryParse<AuthType>(Type, out var t))
            items = items.Where(a => a.Type == t);

        var sort = Sort == "type" ? "type" : "name";
        var descending = string.Equals(Dir, "desc", StringComparison.OrdinalIgnoreCase);
        items = sort switch
        {
            "type" => descending ? items.OrderByDescending(a => a.Type).ThenBy(a => a.Name) : items.OrderBy(a => a.Type).ThenBy(a => a.Name),
            _ => descending ? items.OrderByDescending(a => a.Name) : items.OrderBy(a => a.Name)
        };
        Items = items.ToList();

        Toolbar = new ToolbarModel
        {
            Id = "auths",
            Action = "/Authentications",
            Label = "Authentications",
            Search = Q,
            SearchPlaceholder = "Search authentications",
            SearchField = true,
            Noun = "authentications",
            Total = _store.Authentications.Count,
            Matching = Items.Count,
            Filters = [new FilterField("type", "Type", [new("BasicAuth", "Basic Auth"), new("Matcad", "Matcad")], Type)],
            SortOptions = [new("name", "Name"), new("type", "Type")],
            DefaultSort = "name",
            Sort = sort,
            Descending = descending,
        };
    }
}
