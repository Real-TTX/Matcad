using Matcad.Controls;
using Matcad.Data;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Settings;

/// <summary>Who changed what: routes, providers, authentications, settings, users, restores (admins only, like the whole folder).</summary>
public class AuditModel : PageModel
{
    /// <summary>The newest entries shown; the search and the filter narrow them down.</summary>
    public const int Shown = 200;

    private readonly AuditLog _audit;
    public AuditModel(AuditLog audit) => _audit = audit;

    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }

    public IReadOnlyList<AuditEntry> Entries { get; private set; } = [];
    public int Total { get; private set; }
    public ToolbarModel Toolbar { get; private set; } = null!;

    public async Task OnGetAsync()
    {
        var page = await _audit.ListAsync(Q, Kind, Shown, HttpContext.RequestAborted);
        Entries = page.Entries;
        Total = page.Total;
        Toolbar = new ToolbarModel
        {
            Id = "audit",
            Action = "/Settings/Audit",
            Label = "Audit log",
            Search = Q,
            SearchPlaceholder = "Search names and users",
            SearchField = true,
            Noun = "entries",
            Total = page.Total,
            Matching = page.Total,
            Filters = [new FilterField("kind", "Kind", page.Kinds.Select(k => new FilterOption(k, k)).ToList(), string.IsNullOrEmpty(Kind) ? null : Kind)],
        };
    }
}
