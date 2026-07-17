using Matcad.Auth;
using Matcad.Config;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Routes;

public class EditModel : PageModel
{
    private readonly ConfigStore _store;
    private readonly CaddyService _caddy;
    public EditModel(ConfigStore store, CaddyService caddy) { _store = store; _caddy = caddy; }

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string Host { get; set; } = "";
    /// <summary>"single" or "wildcard" — chosen explicitly when creating a route.</summary>
    [BindProperty] public string RouteType { get; set; } = "single";
    [BindProperty] public string? Upstream { get; set; }
    [BindProperty] public string? FallbackUrl { get; set; }
    [BindProperty] public long? AuthenticationId { get; set; }
    [BindProperty] public long? ProviderId { get; set; }
    [BindProperty] public bool Enabled { get; set; } = true;

    public bool IsNew => Id is null or 0;
    public string? Error { get; private set; }

    public List<(string Value, string Text)> AuthOptions { get; private set; } = new();
    public List<(string Value, string Text)> ProviderOptions { get; private set; } = new();

    public IActionResult OnGet()
    {
        if (!IsNew)
        {
            var r = _store.Routes.FirstOrDefault(x => x.Id == Id);
            if (r == null) return RedirectToPage("Index");
            Name = r.Name; Host = r.Host;
            RouteType = r.Wildcard ? "wildcard" : "single";
            Upstream = r.Upstream; FallbackUrl = r.FallbackUrl;
            AuthenticationId = r.AuthenticationId; ProviderId = r.ProviderId; Enabled = r.Enabled;
        }
        BuildOptions();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var host = (Host ?? "").Trim().ToLowerInvariant();
        var wildcard = RouteType == "wildcard";

        IActionResult Fail(string message)
        {
            Error = message;
            BuildOptions();
            return Page();
        }

        if (string.IsNullOrWhiteSpace(host))
            return Fail("Host ist erforderlich.");

        if (wildcard)
        {
            // Normalize to the *.domain form and enforce a DNS provider (wildcards
            // can only be validated via DNS-01, so a provider is mandatory).
            if (!host.StartsWith("*.")) host = "*." + host.TrimStart('.');
            if (host.Count(c => c == '.') < 2)
                return Fail("Wildcard-Host muss eine Domain enthalten, z. B. *.example.com.");
            if (ProviderId is not > 0)
                return Fail("Für eine Wildcard-Route ist ein DNS-Provider erforderlich (DNS-01-Challenge).");
        }
        else
        {
            if (host.StartsWith("*."))
                return Fail("Eine Einzeldomain darf nicht mit „*.“ beginnen. Wähle stattdessen den Typ „Wildcard“.");
            ProviderId = null; // not applicable for single domains
        }

        var route = _store.Routes.FirstOrDefault(x => x.Id == Id) ?? new RouteConfig();
        route.Id = Id ?? 0;
        route.Name = string.IsNullOrWhiteSpace(Name) ? host : Name.Trim();
        route.Host = host;
        route.Wildcard = wildcard;
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
        AuthOptions.Add(("", "— keine —"));
        foreach (var a in _store.Authentications.OrderBy(a => a.Name))
            AuthOptions.Add((a.Id.ToString(), $"{a.Name} ({a.Type})"));

        ProviderOptions.Add(("", "— keine —"));
        foreach (var p in _store.Providers.OrderBy(p => p.Name))
            ProviderOptions.Add((p.Id.ToString(), p.Name));
    }
}
