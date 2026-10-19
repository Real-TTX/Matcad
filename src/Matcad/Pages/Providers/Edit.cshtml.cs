using Matcad.Auth;
using Matcad.Config;
using Matcad.Controls;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Providers;

public class EditModel : PageModel
{
    private readonly ConfigStore _store;
    private readonly RouteProvider _routes;
    private readonly DnsCredentialTester _tester;
    private readonly CaddyService _caddy;
    public EditModel(ConfigStore store, RouteProvider routes, DnsCredentialTester tester, CaddyService caddy)
    { _store = store; _routes = routes; _tester = tester; _caddy = caddy; }

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    /// <summary>Dropdown value: a known type id or "custom".</summary>
    [BindProperty] public string Type { get; set; } = "netcup";

    public bool IsNew => Id is null or 0;
    public string? Error { get; private set; }
    /// <summary>Result of a "Test credentials" run (Ok = true/false, or null = no test for this type).</summary>
    public (bool? Ok, string Message)? TestResult { get; private set; }
    /// <summary>The type picker and the credential fields (stored secrets are never put into the page).</summary>
    public ProviderFieldsModel Fields { get; private set; } = new() { TypeFieldName = nameof(Type), Type = "netcup" };

    private ProviderConfig? Stored => IsNew ? null : _store.Providers.FirstOrDefault(x => x.Id == Id);

    public IActionResult OnGet()
    {
        if (!IsNew)
        {
            var p = Stored;
            if (p == null) return RedirectToPage("Index");
            Name = p.Name;
            Fields = ProviderFieldsModel.ForStored(nameof(Type), p, Type);
            Type = Fields.Type;
        }
        return Page();
    }

    /// <summary>The module and the credentials of the form; a blank secret keeps the stored value.</summary>
    private (string Module, Dictionary<string, string> Credentials, string? Error) ReadForm(ProviderConfig? stored)
    {
        var (module, posted, error) = ProviderFieldsModel.Read(Request.Form, Type);
        Fields = ProviderFieldsModel.ForPosted(nameof(Type), Type, module, posted, stored);
        return (module, ProviderRules.KeepStored(module, posted, stored), error);
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var stored = Stored;
        if (!IsNew && stored == null) return RedirectToPage("Index");
        var (module, credentials, error) = ReadForm(stored);
        if (string.IsNullOrWhiteSpace(Name)) error ??= "Please give the provider a name.";
        if (error != null) { Error = error; return Page(); }

        // A provider that another tool (Mattiverse) writes keeps its mark when it is edited by hand.
        var provider = new ProviderConfig { Id = Id ?? 0, Name = Name.Trim(), Type = module, Credentials = credentials, ManagedBy = stored?.ManagedBy };
        _store.UpsertProvider(provider, User.GetUserId());

        // A wildcard route that uses the provider needs the new credentials in Caddy.
        var (ok, applyError) = await _caddy.ApplyAsync();
        if (ok) TempData["Flash"] = $"Provider '{provider.Name}' saved.";
        else TempData["FlashError"] = $"Provider '{provider.Name}' saved, but the Caddy push failed: {applyError}";
        return RedirectToPage("Index");
    }

    /// <summary>Tests the entered credentials (a field left empty uses the stored value) against the provider's API, where there is a test.</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        var (module, credentials, error) = ReadForm(Stored);
        if (error != null) { Error = error; return Page(); }
        TestResult = await _tester.TestAsync(module, credentials);
        return Page();
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id is > 0)
        {
            var name = Stored?.Name;
            if (_routes.All().Any(r => r.ProviderId == Id))
            {
                TempData["FlashError"] = "The provider is used by a route and cannot be deleted.";
                return RedirectToPage("Edit", new { id = Id });
            }
            _store.DeleteProvider(Id.Value);
            await _caddy.ApplyAsync();
            TempData["Flash"] = $"Provider '{name}' deleted.";
        }
        return RedirectToPage("Index");
    }
}
