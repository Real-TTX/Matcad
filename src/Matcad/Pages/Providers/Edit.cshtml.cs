using Matcad.Auth;
using Matcad.Config;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Providers;

public class EditModel : PageModel
{
    private readonly ConfigStore _store;
    private readonly Matcad.Services.RouteProvider _routes;
    private readonly Matcad.Services.DnsCredentialTester _tester;
    public EditModel(ConfigStore store, Matcad.Services.RouteProvider routes, Matcad.Services.DnsCredentialTester tester)
    { _store = store; _routes = routes; _tester = tester; }

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    /// <summary>Dropdown value: a known type id or "custom".</summary>
    [BindProperty] public string Type { get; set; } = "netcup";
    /// <summary>Caddy DNS module name when Type == "custom".</summary>
    [BindProperty] public string CustomModule { get; set; } = "";

    public bool IsNew => Id is null or 0;
    public string? Error { get; private set; }
    /// <summary>Result of a "Test credentials" run (Ok = true/false, or null = no test for this type).</summary>
    public (bool? Ok, string Message)? TestResult { get; private set; }
    /// <summary>Existing credential values, keyed by "type:key", to prefill known-type fields.</summary>
    public Dictionary<string, string> CredentialValues { get; } = new();
    /// <summary>Existing credentials for the custom key/value editor.</summary>
    public Dictionary<string, string> CustomCredentials { get; } = new();

    public IActionResult OnGet()
    {
        if (!IsNew)
        {
            var p = _store.Providers.FirstOrDefault(x => x.Id == Id);
            if (p == null) return RedirectToPage("Index");
            Name = p.Name;
            if (ProviderTypes.Find(p.Type) != null)
            {
                Type = p.Type;
                foreach (var kv in p.Credentials) CredentialValues[$"{p.Type}:{kv.Key}"] = kv.Value;
            }
            else
            {
                Type = ProviderTypes.CustomId;
                CustomModule = p.Type;
                foreach (var kv in p.Credentials) CustomCredentials[kv.Key] = kv.Value;
            }
        }
        return Page();
    }

    /// <summary>Reads the module name + credentials out of the posted form.</summary>
    private (string Module, Dictionary<string, string> Creds, string? Error) ReadForm()
    {
        var credentials = new Dictionary<string, string>();
        if (Type == ProviderTypes.CustomId)
        {
            var moduleName = CustomModule?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(moduleName))
                return ("", credentials, "Please enter the Caddy DNS module name (e.g. cloudflare).");
            var keys = Request.Form["CustomKeys"];
            var values = Request.Form["CustomValues"];
            for (var i = 0; i < keys.Count; i++)
            {
                var k = keys[i]?.Trim();
                if (string.IsNullOrEmpty(k)) continue;
                credentials[k] = i < values.Count ? values[i] ?? "" : "";
            }
            return (moduleName, credentials, null);
        }
        var type = ProviderTypes.Find(Type);
        if (type == null) return ("", credentials, "Please select a valid type.");
        foreach (var field in type.Fields)
            credentials[field.Key] = Request.Form[$"cred_{type.Id}_{field.Key}"].ToString();
        return (type.Id, credentials, null);
    }

    /// <summary>Re-fills the display fields so a re-rendered form keeps its values.</summary>
    private void PopulateDisplay(Dictionary<string, string> creds)
    {
        if (Type == ProviderTypes.CustomId)
            foreach (var kv in creds) CustomCredentials[kv.Key] = kv.Value;
        else
            foreach (var kv in creds) CredentialValues[$"{Type}:{kv.Key}"] = kv.Value;
    }

    public IActionResult OnPost()
    {
        if (string.IsNullOrWhiteSpace(Name)) { Error = "Please provide a name."; return Page(); }

        var (moduleName, credentials, error) = ReadForm();
        if (error != null) { Error = error; PopulateDisplay(credentials); return Page(); }

        var provider = _store.Providers.FirstOrDefault(x => x.Id == Id) ?? new ProviderConfig();
        provider.Id = Id ?? 0;
        provider.Name = Name;
        provider.Type = moduleName; // Type is the Caddy DNS module name
        provider.Credentials = credentials;
        _store.UpsertProvider(provider, User.GetUserId());

        TempData["Flash"] = $"Provider '{Name}' saved.";
        return RedirectToPage("Index");
    }

    /// <summary>Tests the entered credentials against the provider's API (where supported).</summary>
    public async Task<IActionResult> OnPostTestAsync()
    {
        var (moduleName, credentials, error) = ReadForm();
        PopulateDisplay(credentials);
        if (error != null) { Error = error; return Page(); }
        TestResult = await _tester.TestAsync(moduleName, credentials);
        return Page();
    }

    public IActionResult OnPostDelete()
    {
        if (Id is > 0)
        {
            var name = _store.Providers.FirstOrDefault(x => x.Id == Id)?.Name;
            if (_routes.All().Any(r => r.ProviderId == Id))
            {
                TempData["FlashError"] = "Provider is used by at least one route and cannot be deleted.";
                return RedirectToPage("Edit", new { id = Id });
            }
            _store.DeleteProvider(Id.Value);
            TempData["Flash"] = $"Provider '{name}' deleted.";
        }
        return RedirectToPage("Index");
    }
}
