using System.Web;

namespace Matcad.Controls;

public sealed record FilterOption(string Value, string Label);

/// <summary>One filter of the filter dialog: a select whose first option (<see cref="AllLabel"/>) is the value that applies when nothing is chosen.</summary>
public sealed record FilterField(string Name, string Label, IReadOnlyList<FilterOption> Options, string? Selected)
{
    public string AllLabel { get; init; } = "All";
}

public sealed record SortOption(string Value, string Label);

/// <summary>
/// Everything the toolbar partial (<c>_Toolbar.cshtml</c>) needs. Filter and Sort are buttons that open a dialog, and so is the search, unless
/// <see cref="SearchField"/> puts the search into the toolbar as a field with the dialog buttons at the right end (rule LIST-03). All their fields
/// belong to ONE GET form, so every dialog sends the complete state and nothing the user set in another dialog is lost.
/// </summary>
public sealed class ToolbarModel
{
    public required string Id { get; init; }

    /// <summary>The address of the list: the form sends to it and the chips link back to it.</summary>
    public required string Action { get; init; }

    /// <summary>Names the list for screen readers ("Routes tools").</summary>
    public string Label { get; init; } = "List";

    public string? Search { get; init; }

    public string SearchPlaceholder { get; init; } = "Search";

    /// <summary>The search is a field right in the toolbar and Filter and Sort stand at its right end; without it the search is a button that opens a dialog.</summary>
    public bool SearchField { get; init; }

    public IReadOnlyList<FilterField> Filters { get; init; } = [];

    public IReadOnlyList<SortOption> SortOptions { get; init; } = [];

    public string? Sort { get; init; }

    public bool Descending { get; init; }

    public string DefaultSort { get; init; } = string.Empty;

    public int Total { get; init; }

    public int Matching { get; init; }

    public string Noun { get; init; } = "items";

    public bool SearchActive => !string.IsNullOrWhiteSpace(Search);

    public int ActiveFilters => Filters.Count(f => !string.IsNullOrEmpty(f.Selected));

    public bool SortActive => !string.IsNullOrEmpty(Sort) && (Sort != DefaultSort || Descending);

    public string SortLabel => SortOptions.FirstOrDefault(o => o.Value == (Sort ?? DefaultSort))?.Label ?? string.Empty;

    /// <summary>The address of the list with every tool in effect except <paramref name="remove"/> (a filter name, "q" or "sort").</summary>
    public string UrlWithout(string remove)
    {
        var pairs = new List<(string, string?)>();
        if (remove != "q" && SearchActive)
            pairs.Add(("q", Search));

        foreach (var f in Filters.Where(f => !string.IsNullOrEmpty(f.Selected) && f.Name != remove))
            pairs.Add((f.Name, f.Selected));

        if (remove != "sort" && SortActive)
        {
            pairs.Add(("sort", Sort));
            if (Descending)
                pairs.Add(("dir", "desc"));
        }

        var query = string.Join("&", pairs.Where(p => !string.IsNullOrEmpty(p.Item2)).Select(p => $"{HttpUtility.UrlEncode(p.Item1)}={HttpUtility.UrlEncode(p.Item2)}"));
        return query.Length == 0 ? Action : $"{Action}?{query}";
    }
}
