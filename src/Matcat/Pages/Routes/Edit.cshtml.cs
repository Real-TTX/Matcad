using Matcat.Auth;
using Matcat.Config;
using Matcat.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcat.Pages.Routes;

public class EditModel : PageModel
{
    private readonly ConfigStore _store;
    private readonly CaddyService _caddy;
    public EditModel(ConfigStore store, CaddyService caddy) { _store = store; _caddy = caddy; }

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string Host { get; set; } = "";
    [BindProperty] public bool Wildcard { get; set; }
    [BindProperty] public long? ParentId { get; set; }
    [BindProperty] public string? Upstream { get; set; }
    [BindProperty] public string? FallbackUrl { get; set; }
    [BindProperty] public long? AuthenticationId { get; set; }
    [BindProperty] public long? ProviderId { get; set; }
    [BindProperty] public bool Enabled { get; set; } = true;

    public bool IsNew => Id is null or 0;
    public string? Error { get; private set; }

    public List<(string Value, string Text)> ParentOptions { get; private set; } = new();
    public List<(string Value, string Text)> AuthOptions { get; private set; } = new();
    public List<(string Value, string Text)> ProviderOptions { get; private set; } = new();

    public IActionResult OnGet()
    {
        if (!IsNew)
        {
            var r = _store.Routes.FirstOrDefault(x => x.Id == Id);
            if (r == null) return RedirectToPage("Index");
            Name = r.Name; Host = r.Host; Wildcard = r.Wildcard; ParentId = r.ParentId;
            Upstream = r.Upstream; FallbackUrl = r.FallbackUrl;
            AuthenticationId = r.AuthenticationId; ProviderId = r.ProviderId; Enabled = r.Enabled;
        }
        BuildOptions();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Host))
        {
            Error = "Host ist erforderlich (z. B. app.example.com oder *.example.com).";
            BuildOptions();
            return Page();
        }
        Wildcard = Host.StartsWith("*.");

        var route = _store.Routes.FirstOrDefault(x => x.Id == Id) ?? new RouteConfig();
        route.Id = Id ?? 0;
        route.Name = string.IsNullOrWhiteSpace(Name) ? Host : Name;
        route.Host = Host.Trim();
        route.Wildcard = Wildcard;
        route.ParentId = ParentId is > 0 ? ParentId : null;
        route.Upstream = string.IsNullOrWhiteSpace(Upstream) ? null : Upstream!.Trim();
        route.FallbackUrl = string.IsNullOrWhiteSpace(FallbackUrl) ? null : FallbackUrl!.Trim();
        route.AuthenticationId = AuthenticationId is > 0 ? AuthenticationId : null;
        route.ProviderId = ProviderId is > 0 ? ProviderId : null;
        route.Enabled = Enabled;
        _store.UpsertRoute(route, User.GetUserId());

        await ApplyAndFlash($"Route „{route.Host}“ gespeichert.");
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id is > 0)
        {
            var host = _store.Routes.FirstOrDefault(x => x.Id == Id)?.Host;
            _store.DeleteRoute(Id.Value);
            await ApplyAndFlash($"Route „{host}“ gelöscht.");
        }
        return RedirectToPage("Index");
    }

    private async Task ApplyAndFlash(string success)
    {
        var (ok, error) = await _caddy.ApplyAsync();
        if (ok) TempData["Flash"] = success + " Caddy-Konfiguration aktualisiert.";
        else TempData["FlashError"] = success + $" Aber Caddy-Push fehlgeschlagen: {error}";
    }

    private void BuildOptions()
    {
        ParentOptions.Add(("", "— keine (Root) —"));
        foreach (var r in _store.Routes.Where(r => r.Id != Id).OrderBy(r => r.Host))
            ParentOptions.Add((r.Id.ToString(), $"{r.Host}"));

        AuthOptions.Add(("", "— keine —"));
        foreach (var a in _store.Authentications.OrderBy(a => a.Name))
            AuthOptions.Add((a.Id.ToString(), $"{a.Name} ({a.Type})"));

        ProviderOptions.Add(("", "— keine —"));
        foreach (var p in _store.Providers.OrderBy(p => p.Name))
            ProviderOptions.Add((p.Id.ToString(), p.Name));
    }
}
