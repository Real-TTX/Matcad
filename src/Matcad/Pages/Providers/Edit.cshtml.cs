using Matcad.Auth;
using Matcad.Config;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Providers;

public class EditModel : PageModel
{
    private readonly ConfigStore _store;
    private readonly Matcad.Services.RouteProvider _routes;
    public EditModel(ConfigStore store, Matcad.Services.RouteProvider routes) { _store = store; _routes = routes; }

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    /// <summary>Dropdown value: a known type id or "custom".</summary>
    [BindProperty] public string Type { get; set; } = "netcup";
    /// <summary>Caddy DNS module name when Type == "custom".</summary>
    [BindProperty] public string CustomModule { get; set; } = "";

    public bool IsNew => Id is null or 0;
    public string? Error { get; private set; }
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

    public IActionResult OnPost()
    {
        if (string.IsNullOrWhiteSpace(Name)) { Error = "Please provide a name."; return Page(); }

        string moduleName;
        var credentials = new Dictionary<string, string>();

        if (Type == ProviderTypes.CustomId)
        {
            moduleName = CustomModule?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                Error = "Please enter the Caddy DNS module name (e.g. cloudflare).";
                return Page();
            }
            var keys = Request.Form["CustomKeys"];
            var values = Request.Form["CustomValues"];
            for (var i = 0; i < keys.Count; i++)
            {
                var k = keys[i]?.Trim();
                if (string.IsNullOrEmpty(k)) continue;
                credentials[k] = i < values.Count ? values[i] ?? "" : "";
            }
        }
        else
        {
            var type = ProviderTypes.Find(Type);
            if (type == null) { Error = "Please select a valid type."; return Page(); }
            moduleName = type.Id;
            foreach (var field in type.Fields)
                credentials[field.Key] = Request.Form[$"cred_{type.Id}_{field.Key}"].ToString();
        }

        var provider = _store.Providers.FirstOrDefault(x => x.Id == Id) ?? new ProviderConfig();
        provider.Id = Id ?? 0;
        provider.Name = Name;
        provider.Type = moduleName; // Type is the Caddy DNS module name
        provider.Credentials = credentials;
        _store.UpsertProvider(provider, User.GetUserId());

        TempData["Flash"] = $"Provider “{Name}” saved.";
        return RedirectToPage("Index");
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
            TempData["Flash"] = $"Provider “{name}” deleted.";
        }
        return RedirectToPage("Index");
    }
}
