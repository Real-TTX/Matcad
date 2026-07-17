using Matcat.Auth;
using Matcat.Config;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcat.Pages.Providers;

public class EditModel : PageModel
{
    private readonly ConfigStore _store;
    public EditModel(ConfigStore store) => _store = store;

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string Type { get; set; } = "netcup";

    public bool IsNew => Id is null or 0;
    public string? Error { get; private set; }
    /// <summary>Existing credential values, keyed by "type:key", to prefill the form.</summary>
    public Dictionary<string, string> CredentialValues { get; } = new();

    public IActionResult OnGet()
    {
        if (!IsNew)
        {
            var p = _store.Providers.FirstOrDefault(x => x.Id == Id);
            if (p == null) return RedirectToPage("Index");
            Name = p.Name;
            Type = p.Type;
            foreach (var kv in p.Credentials)
                CredentialValues[$"{p.Type}:{kv.Key}"] = kv.Value;
        }
        return Page();
    }

    public IActionResult OnPost()
    {
        var type = ProviderTypes.Find(Type);
        if (string.IsNullOrWhiteSpace(Name) || type == null)
        {
            Error = "Bitte Name und einen gültigen Typ angeben.";
            return Page();
        }

        var credentials = new Dictionary<string, string>();
        foreach (var field in type.Fields)
        {
            var value = Request.Form[$"cred_{type.Id}_{field.Key}"].ToString();
            credentials[field.Key] = value;
        }

        var provider = _store.Providers.FirstOrDefault(x => x.Id == Id) ?? new ProviderConfig();
        provider.Id = Id ?? 0;
        provider.Name = Name;
        provider.Type = type.Id;
        provider.Credentials = credentials;
        _store.UpsertProvider(provider, User.GetUserId());

        TempData["Flash"] = $"Provider „{Name}“ gespeichert.";
        return RedirectToPage("Index");
    }

    public IActionResult OnPostDelete()
    {
        if (Id is > 0)
        {
            var name = _store.Providers.FirstOrDefault(x => x.Id == Id)?.Name;
            if (_store.Routes.Any(r => r.ProviderId == Id))
            {
                TempData["FlashError"] = "Provider wird von mindestens einer Route verwendet und kann nicht gelöscht werden.";
                return RedirectToPage("Edit", new { id = Id });
            }
            _store.DeleteProvider(Id.Value);
            TempData["Flash"] = $"Provider „{name}“ gelöscht.";
        }
        return RedirectToPage("Index");
    }
}
