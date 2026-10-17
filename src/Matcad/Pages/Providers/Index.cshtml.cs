using Matcad.Config;
using Matcad.Controls;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Providers;

public class IndexModel : PageModel
{
    private readonly ConfigStore _store;
    public IndexModel(ConfigStore store) => _store = store;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Sort { get; set; }
    [BindProperty(SupportsGet = true)] public string? Dir { get; set; }

    /// <summary>The search and sort of the list (rule LIST-03).</summary>
    public ToolbarModel Toolbar { get; private set; } = null!;
    public IReadOnlyList<ProviderConfig> Items { get; private set; } = [];

    public void OnGet()
    {
        var items = _store.Providers.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(Q))
            items = items.Where(p =>
                p.Name.Contains(Q, StringComparison.OrdinalIgnoreCase) ||
                ProviderTypes.DisplayName(p.Type).Contains(Q, StringComparison.OrdinalIgnoreCase));

        var sort = Sort == "type" ? "type" : "name";
        var descending = string.Equals(Dir, "desc", StringComparison.OrdinalIgnoreCase);
        items = sort switch
        {
            "type" => descending ? items.OrderByDescending(p => p.Type).ThenBy(p => p.Name) : items.OrderBy(p => p.Type).ThenBy(p => p.Name),
            _ => descending ? items.OrderByDescending(p => p.Name) : items.OrderBy(p => p.Name)
        };
        Items = items.ToList();

        Toolbar = new ToolbarModel
        {
            Id = "providers",
            Action = "/Providers",
            Label = "Providers",
            Search = Q,
            SearchPlaceholder = "Search providers",
            SearchField = true,
            Noun = "providers",
            Total = _store.Providers.Count,
            Matching = Items.Count,
            SortOptions = [new("name", "Name"), new("type", "Type")],
            DefaultSort = "name",
            Sort = sort,
            Descending = descending,
        };
    }
}
