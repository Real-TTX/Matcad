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
    private readonly RouteProvider _routes;
    public EditModel(ConfigStore store, CaddyService caddy, RouteProvider routes)
    { _store = store; _caddy = caddy; _routes = routes; }

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    /// <summary>Base domain to prefill when adding a subdomain from the Routes list
    /// ("+ Add subdomain"). Only used when creating a new route.</summary>
    [BindProperty(SupportsGet = true)] public string? Sub { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public string Host { get; set; } = "";
    /// <summary>"single" or "wildcard" - chosen explicitly when creating a route.</summary>
    [BindProperty] public string RouteType { get; set; } = "single";
    /// <summary>"proxy" (reverse-proxy to an upstream) or "redirect" (302/301 to a URL).</summary>
    [BindProperty] public string Target { get; set; } = "proxy";
    [BindProperty] public string? Upstream { get; set; }
    [BindProperty] public bool InsecureSkipVerify { get; set; }
    [BindProperty] public string? FallbackUrl { get; set; }
    [BindProperty] public bool RedirectPermanent { get; set; }
    [BindProperty] public long? AuthenticationId { get; set; }
    [BindProperty] public long? ProviderId { get; set; }
    [BindProperty] public string? AcmeEmail { get; set; }
    [BindProperty] public bool Enabled { get; set; } = true;

    public bool IsNew => Id is null or 0;
    public string? Error { get; private set; }

    public List<(string Value, string Text)> AuthOptions { get; private set; } = new();
    public List<(string Value, string Text)> ProviderOptions { get; private set; } = new();
    /// <summary>Existing route hosts offered as redirect targets (as full URLs).</summary>
    public List<string> RedirectTargets { get; private set; } = new();

    /// <summary>Enabled wildcard parent domains (e.g. "example.com" for *.example.com),
    /// used by the form to warn when a single host would need its own certificate.</summary>
    public List<string> WildcardParents { get; private set; } = new();
    public string WildcardParentsJson => System.Text.Json.JsonSerializer.Serialize(WildcardParents);

    public IActionResult OnGet()
    {
        if (!IsNew)
        {
            var r = _store.Routes.FirstOrDefault(x => x.Id == Id);
            if (r == null) return RedirectToPage("Index");
            Name = r.Name; Host = r.Host;
            RouteType = r.Wildcard ? "wildcard" : "single";
            Upstream = r.Upstream; InsecureSkipVerify = r.InsecureSkipVerify;
            FallbackUrl = r.FallbackUrl; RedirectPermanent = r.RedirectPermanent;
            // A route with a redirect target and no upstream is a redirect.
            Target = string.IsNullOrWhiteSpace(r.Upstream) && !string.IsNullOrWhiteSpace(r.FallbackUrl)
                ? "redirect" : "proxy";
            AuthenticationId = r.AuthenticationId; ProviderId = r.ProviderId; Enabled = r.Enabled;
            AcmeEmail = r.AcmeEmail;
        }
        else if (!string.IsNullOrWhiteSpace(Sub))
        {
            // Prefill ".<base>" so the user just types the subdomain label in front.
            Host = "." + Sub.Trim().TrimStart('.');
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
            return Fail("Host is required.");

        if (wildcard)
        {
            // Normalize to the *.domain form and enforce a DNS provider (wildcards
            // can only be validated via DNS-01, so a provider is mandatory).
            if (!host.StartsWith("*.")) host = "*." + host.TrimStart('.');
            if (host.Count(c => c == '.') < 2)
                return Fail("A wildcard host must include a domain, e.g. *.example.com.");
            if (ProviderId is not > 0)
                return Fail("A wildcard route requires a DNS provider (DNS-01 challenge).");
        }
        else
        {
            if (host.StartsWith("*."))
                return Fail("A single domain must not start with '*.'. Choose the 'Wildcard' type instead.");
            ProviderId = null; // not applicable for single domains
        }

        var redirect = Target == "redirect";
        if (redirect && string.IsNullOrWhiteSpace(FallbackUrl))
            return Fail("A redirect route needs a target URL.");

        var route = _store.Routes.FirstOrDefault(x => x.Id == Id) ?? new RouteConfig();
        route.Id = Id ?? 0;
        route.Name = string.IsNullOrWhiteSpace(Name) ? host : Name.Trim();
        route.Host = host;
        route.Wildcard = wildcard;
        // Proxy and redirect are mutually exclusive.
        route.Upstream = redirect || string.IsNullOrWhiteSpace(Upstream) ? null : Upstream!.Trim();
        route.InsecureSkipVerify = !redirect && InsecureSkipVerify;
        route.FallbackUrl = redirect && !string.IsNullOrWhiteSpace(FallbackUrl) ? FallbackUrl!.Trim() : null;
        route.RedirectPermanent = redirect && RedirectPermanent;
        route.AuthenticationId = AuthenticationId is > 0 ? AuthenticationId : null;
        route.ProviderId = ProviderId is > 0 ? ProviderId : null;
        // Per-domain ACME email only applies to wildcard (DNS-01) certificates.
        route.AcmeEmail = wildcard && !string.IsNullOrWhiteSpace(AcmeEmail) ? AcmeEmail.Trim() : null;
        route.Enabled = Enabled;
        _store.UpsertRoute(route, User.GetUserId());

        var warnings = new List<string>();

        // Non-blocking heads-up when a single host isn't covered by any wildcard.
        if (!wildcard)
        {
            var coverage = CertificatePlanner.ForRoute(route, _routes.All());
            if (coverage.Kind == CertificatePlanner.CertKind.Individual)
                warnings.Add($"'{route.Host}' is not covered by a wildcard - Caddy will request an " +
                    "individual certificate for it. Consider a *." + ParentOf(route.Host) + " wildcard route instead.");
        }

        // Matcad forward-auth needs a reachable login portal, otherwise the login
        // page can't load for the protected host.
        if (route.AuthenticationId is > 0)
        {
            var a = _store.Authentications.FirstOrDefault(x => x.Id == route.AuthenticationId);
            if (a?.Type == AuthType.Matcad && string.IsNullOrWhiteSpace(_store.Settings.EffectivePortalUrl()))
                warnings.Add("This route uses Matcad authentication but no login portal is configured - " +
                    "set 'Matcad host' (or a portal URL) under Settings, otherwise the login page won't load.");
        }

        if (warnings.Count > 0) TempData["FlashWarn"] = string.Join(" ", warnings);

        await ApplyAndFlash($"Route '{route.Host}' saved.");
        return RedirectToPage("Index");
    }

    private static string ParentOf(string host) =>
        host.Contains('.') ? host[(host.IndexOf('.') + 1)..] : host;

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id is > 0)
        {
            var host = _store.Routes.FirstOrDefault(x => x.Id == Id)?.Host;
            _store.DeleteRoute(Id.Value);
            await ApplyAndFlash($"Route '{host}' deleted.");
        }
        return RedirectToPage("Index");
    }

    private async Task ApplyAndFlash(string success)
    {
        var (ok, error) = await _caddy.ApplyAsync();
        if (ok) TempData["Flash"] = success + " Caddy configuration updated.";
        else TempData["FlashError"] = success + $" But the Caddy push failed: {error}";
    }

    private void BuildOptions()
    {
        AuthOptions.Add(("", "- none -"));
        foreach (var a in _store.Authentications.OrderBy(a => a.Name))
            AuthOptions.Add((a.Id.ToString(), $"{a.Name} ({a.Type})"));

        ProviderOptions.Add(("", "- none -"));
        foreach (var p in _store.Providers.OrderBy(p => p.Name))
            ProviderOptions.Add((p.Id.ToString(), p.Name));

        WildcardParents = _routes.All()
            .Where(r => r.Enabled && r.Wildcard && r.Host.StartsWith("*."))
            .Select(r => r.Host[2..])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Existing non-wildcard hosts (other than this route) as ready-made
        // redirect targets. The editor also allows a free-text "other" target.
        var current = (Host ?? "").Trim().ToLowerInvariant();
        RedirectTargets = _routes.All()
            .Where(r => !string.IsNullOrWhiteSpace(r.Host) && !r.Host.StartsWith("*.")
                        && !r.Host.Equals(current, StringComparison.OrdinalIgnoreCase))
            .Select(r => $"https://{r.Host}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(u => u, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
