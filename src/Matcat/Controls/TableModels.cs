using Microsoft.AspNetCore.Html;

namespace Matcat.Controls;

/// <summary>
/// Data-driven model for the reusable data-table control (_DataTable.cshtml).
/// A page maps its entities into <see cref="Rows"/> and describes the columns;
/// the control renders the toolbar (search + filters + sortable headers),
/// the table, and pagination. Multiple tables per page are supported because
/// each model carries its own <see cref="Id"/> and query-parameter names.
/// </summary>
public class DataTable
{
    public string Id { get; set; } = "table";
    public List<DataColumn> Columns { get; set; } = new();
    public List<DataRow> Rows { get; set; } = new();

    // Toolbar state (bound to the query string so it round-trips on GET).
    public string SearchParam { get; set; } = "q";
    public string? SearchValue { get; set; }
    public string SearchPlaceholder { get; set; } = "Suchen…";
    public List<TableFilter> Filters { get; set; } = new();

    // Sorting.
    public string SortParam { get; set; } = "sort";
    public string DirParam { get; set; } = "dir";
    public string? Sort { get; set; }
    public string Dir { get; set; } = "asc";

    // Pagination (optional; leave TotalPages <= 1 to hide).
    public string PageParam { get; set; } = "page";
    public int Page { get; set; } = 1;
    public int TotalPages { get; set; } = 1;

    public string EmptyText { get; set; } = "Keine Einträge vorhanden.";

    /// <summary>Extra query params (besides search/filter/sort) to preserve in links.</summary>
    public Dictionary<string, string?> ExtraQuery { get; set; } = new();

    /// <summary>Current search/filter/extra state that links must preserve.</summary>
    public Dictionary<string, string?> QueryBase()
    {
        var d = new Dictionary<string, string?>();
        if (!string.IsNullOrEmpty(SearchValue)) d[SearchParam] = SearchValue;
        foreach (var f in Filters)
            if (!string.IsNullOrEmpty(f.Selected)) d[f.Param] = f.Selected;
        foreach (var kv in ExtraQuery)
            if (!string.IsNullOrEmpty(kv.Value)) d[kv.Key] = kv.Value;
        return d;
    }

    /// <summary>Builds a "?a=b&amp;c=d" query string on the current page.</summary>
    public string Link(params (string Key, string? Value)[] overrides)
    {
        var d = QueryBase();
        foreach (var (k, v) in overrides)
        {
            if (string.IsNullOrEmpty(v)) d.Remove(k);
            else d[k] = v;
        }
        if (d.Count == 0) return "?";
        return "?" + string.Join("&", d.Select(kv =>
            $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value ?? "")}"));
    }

    public string SortLink(string key)
    {
        var nextDir = (Sort == key && Dir == "asc") ? "desc" : "asc";
        return Link((SortParam, key), (DirParam, nextDir), (PageParam, null));
    }

    public string? SortIndicator(string key) =>
        Sort == key ? (Dir == "asc" ? "▲" : "▼") : null;
}

public class DataColumn
{
    public string Header { get; set; } = "";
    /// <summary>When set, the header becomes a sort link using this key.</summary>
    public string? SortKey { get; set; }
    public DataColumn() { }
    public DataColumn(string header, string? sortKey = null) { Header = header; SortKey = sortKey; }
}

public class DataRow
{
    public List<IHtmlContent> Cells { get; set; } = new();
    /// <summary>When set, the table renders a trailing "edit" icon button linking here.</summary>
    public string? EditUrl { get; set; }
    public DataRow() { }
    public DataRow(params IHtmlContent[] cells) { Cells = cells.ToList(); }
}

public class TableFilter
{
    public string Param { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Selected { get; set; }
    /// <summary>value -> text. An empty first entry acts as "all".</summary>
    public List<(string Value, string Text)> Options { get; set; } = new();
}
