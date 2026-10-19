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
    [BindProperty] public bool AllowEmbedding { get; set; }
    [BindProperty] public string? FallbackUrl { get; set; }
    /// <summary>301, 302, 307 or 308.</summary>
    [BindProperty] public int RedirectCode { get; set; } = 302;
    [BindProperty] public bool RedirectKeepPath { get; set; }
    [BindProperty] public bool Hsts { get; set; }
    [BindProperty] public bool Compress { get; set; }
    [BindProperty] public bool BlockExploits { get; set; }
    [BindProperty] public long? AuthenticationId { get; set; }
    [BindProperty] public long? ProviderId { get; set; }
    [BindProperty] public string? AcmeEmail { get; set; }
    [BindProperty] public bool Enabled { get; set; } = true;

    // --- Guards and upstream. The values of the headers are secrets: they are never sent back to the page. A value left empty
    // --- on the form keeps the saved one (the header is found by its name), as an empty password does on a user.
    [BindProperty] public string? UpstreamHost { get; set; }
    [BindProperty] public List<string> HeaderNames { get; set; } = new();
    [BindProperty] public List<string> HeaderValues { get; set; } = new();
    [BindProperty] public string? RequireName { get; set; }
    [BindProperty] public string? RequireValue { get; set; }
    /// <summary>One address or range per line.</summary>
    [BindProperty] public string? AllowedIpsText { get; set; }
    [BindProperty] public string? BlockedIpsText { get; set; }

    // --- Headers of the answer (not secrets: every visitor gets them, so they are shown again). Three parallel lists, one entry per row.
    [BindProperty] public List<string> ResponseNames { get; set; } = new();
    [BindProperty] public List<string> ResponseValues { get; set; } = new();
    /// <summary>"set" or "remove" per row.</summary>
    [BindProperty] public List<string> ResponseActions { get; set; } = new();
    public List<ResponseHeader> ResponseRows { get; private set; } = new();

    // --- Custom locations: three parallel lists, one entry per row. Kind: "proxy", "proxy-strip" or a redirect code ("301" ... "308").
    [BindProperty] public List<string> LocationPaths { get; set; } = new();
    [BindProperty] public List<string> LocationKinds { get; set; } = new();
    [BindProperty] public List<string> LocationTargets { get; set; } = new();
    public record LocationRow(string Path, string Kind, string Target);
    public static readonly (string Value, string Text)[] LocationKindOptions =
    [
        ("proxy", "Proxy"), ("proxy-strip", "Proxy, strip the path"),
        ("302", "Redirect 302"), ("301", "Redirect 301"), ("307", "Redirect 307"), ("308", "Redirect 308"),
    ];
    public List<LocationRow> LocationRows { get; private set; } = new();

    /// <summary>A row of the upstream headers on the page: the name, and whether a value is saved for it.</summary>
    public record HeaderRow(string Name, bool ValueSet);

    public List<HeaderRow> HeaderRows { get; private set; } = new();
    /// <summary>Whether the required header has a saved value (the field then says "set" instead of staying blank).</summary>
    public bool RequireSet { get; private set; }
    /// <summary>The section is folded away unless something in it is set, or the last save was refused.</summary>
    public bool AccessOpen => Error != null
        || !string.IsNullOrWhiteSpace(UpstreamHost) || HeaderRows.Count > 0
        || !string.IsNullOrWhiteSpace(RequireName) || !string.IsNullOrWhiteSpace(AllowedIpsText) || !string.IsNullOrWhiteSpace(BlockedIpsText);

    public bool IsNew => Id is null or 0;
    public string? Error { get; private set; }
    /// <summary>The port of a port-bound route. Those are managed through the API; here they can only be deleted.</summary>
    public int? PortBinding { get; private set; }

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
            PortBinding = r.ListenPort > 0 ? r.ListenPort : null;
            RouteType = r.Wildcard ? "wildcard" : "single";
            Upstream = r.Upstream; InsecureSkipVerify = r.InsecureSkipVerify; AllowEmbedding = r.AllowEmbedding;
            FallbackUrl = r.FallbackUrl; RedirectCode = r.RedirectStatus; RedirectKeepPath = r.RedirectKeepPath;
            Hsts = r.Hsts; Compress = r.Compress; BlockExploits = r.BlockExploits;
            BlockedIpsText = string.Join('\n', r.BlockedIps);
            // A route with a redirect target and no upstream is a redirect.
            Target = string.IsNullOrWhiteSpace(r.Upstream) && !string.IsNullOrWhiteSpace(r.FallbackUrl)
                ? "redirect" : "proxy";
            AuthenticationId = r.AuthenticationId; ProviderId = r.ProviderId; Enabled = r.Enabled;
            AcmeEmail = r.AcmeEmail;
            UpstreamHost = r.UpstreamHost;
            HeaderRows = r.UpstreamHeaders.Select(h => new HeaderRow(h.Name, ValueSet: true)).ToList();
            RequireName = r.RequireHeader?.Name; RequireSet = r.RequireHeader != null;
            AllowedIpsText = string.Join('\n', r.AllowedIps);
            ResponseRows = r.ResponseHeaders.Select(h => new ResponseHeader { Name = h.Name, Value = h.Value, Remove = h.Remove }).ToList();
            LocationRows = r.Locations.Select(l => new LocationRow(l.Path,
                l.Upstream != null ? (l.StripPath ? "proxy-strip" : "proxy") : l.RedirectCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                l.Upstream ?? l.RedirectUrl ?? "")).ToList();
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
        var existing = IsNew ? null : _store.Routes.FirstOrDefault(x => x.Id == Id);
        if (!IsNew && existing == null) return RedirectToPage("Index");
        if (existing?.ListenPort > 0) return Fail("Port bindings are managed through the API. Here they can only be deleted.", existing.ListenPort);

        // Work on a copy: a rejected form must not leave half-applied changes in the stored route.
        // A route that another tool (Mattiverse) writes keeps its mark when it is edited by hand.
        var route = new RouteConfig { Id = existing?.Id ?? 0, ManagedBy = existing?.ManagedBy };
        var (access, accessError) = ReadAccess(existing);
        if (accessError != null) return Fail(accessError);
        var (locations, locationError) = ReadLocations();
        if (locationError != null) return Fail(locationError);
        if (!RouteEditor.TrySplitRedirectCode(RedirectCode, out var permanent, out var keepMethod) && Target == "redirect")
            return Fail("The redirect code must be 301, 302, 307 or 308.");
        var form = new RouteForm(Host, RouteType == "wildcard", Target == "redirect", Upstream, InsecureSkipVerify,
            FallbackUrl, permanent, AuthenticationId, ProviderId, AcmeEmail, Enabled, Name, AllowEmbedding, ListenPort: null,
            // What is said to the upstream belongs to a reverse proxy; the fields are still on the page when the type is switched to a redirect.
            UpstreamHost: Target == "redirect" ? null : UpstreamHost, UpstreamHeaders: Target == "redirect" ? null : access.UpstreamHeaders,
            RequireHeader: access.RequireHeader, AllowedIps: RouteGuards.SplitIps(AllowedIpsText),
            Options: new RouteExtras(RedirectKeepPath, Hsts, Compress, BlockExploits, RouteGuards.SplitIps(BlockedIpsText), keepMethod, ReadResponseHeaders(), locations));
        var error = RouteEditor.Apply(form, route);
        if (error != null) return Fail(error);
        if (!_store.TryUpsertRoute(route, User.GetUserId(), updateSameTarget: false))
            return Fail($"A route for '{route.Host}' already exists.");

        var warnings = new List<string>();

        // Non-blocking heads-up when a single host isn't covered by any wildcard.
        if (!route.Wildcard)
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

    private IActionResult Fail(string message, int? portBinding = null)
    {
        Error = message;
        PortBinding = portBinding;
        ShowPostedAccess();
        ResponseRows = ReadResponseHeaders();
        LocationRows = PostedLocationRows();
        BuildOptions();
        return Page();
    }

    /// <summary>The header rows and the required header the form posted. A row without a value keeps the saved one (matched by name);
    /// an entirely empty row is dropped, and so is the required header when its name is empty (that is how it is removed).</summary>
    private (PostedAccess Access, string? Error) ReadAccess(RouteConfig? existing)
    {
        var headers = new List<HeaderPair>();
        for (var i = 0; i < HeaderNames.Count; i++)
        {
            var name = HeaderNames[i]?.Trim() ?? "";
            var value = i < HeaderValues.Count ? HeaderValues[i] ?? "" : "";
            if (name.Length == 0 && value.Length == 0) continue;
            headers.Add(new HeaderPair { Name = name, Value = value });
        }
        var keep = RouteGuards.KeepStoredValues(headers.Where(h => h.Name.Length > 0), existing?.UpstreamHeaders, "Upstream header");
        if (keep != null) return (new PostedAccess(headers, null), keep);

        HeaderPair? require = null;
        var requireName = RequireName?.Trim() ?? "";
        if (requireName.Length > 0 || !string.IsNullOrEmpty(RequireValue))
        {
            require = new HeaderPair { Name = requireName, Value = RequireValue ?? "" };
            // Without a value the saved one stays, if the header is the one that is saved. (A value without a name is left for Apply to refuse.)
            var kept = requireName.Length == 0 ? null
                : RouteGuards.KeepStoredValues([require], existing?.RequireHeader is { } saved ? new[] { saved } : null, "Required header");
            if (kept != null) return (new PostedAccess(headers, require), kept);
        }
        return (new PostedAccess(headers, require), null);
    }

    /// <summary>The location rows as posted (an entirely empty row is dropped).</summary>
    private List<LocationRow> PostedLocationRows()
    {
        var rows = new List<LocationRow>();
        for (var i = 0; i < LocationPaths.Count; i++)
        {
            var path = LocationPaths[i]?.Trim() ?? "";
            var kind = i < LocationKinds.Count ? LocationKinds[i] ?? "proxy" : "proxy";
            var target = i < LocationTargets.Count ? LocationTargets[i]?.Trim() ?? "" : "";
            if (path.Length == 0 && target.Length == 0) continue;
            rows.Add(new LocationRow(path, kind, target));
        }
        return rows;
    }

    /// <summary>The posted rows as locations; the kind says whether the target is an upstream (stripped or not) or a redirect with its code.</summary>
    private (List<RouteLocation> Locations, string? Error) ReadLocations()
    {
        var locations = new List<RouteLocation>();
        foreach (var row in PostedLocationRows())
        {
            if (row.Kind is "proxy" or "proxy-strip")
                locations.Add(new RouteLocation { Path = row.Path, Upstream = row.Target, StripPath = row.Kind == "proxy-strip" });
            else if (int.TryParse(row.Kind, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var code))
                locations.Add(new RouteLocation { Path = row.Path, RedirectUrl = row.Target, RedirectCode = code });
            else
                return (locations, $"Location '{row.Path}': choose what happens to its requests.");
        }
        return (locations, null);
    }

    /// <summary>The rows of the response headers the form posted; an entirely empty row is dropped, a removed header has no value.</summary>
    private List<ResponseHeader> ReadResponseHeaders()
    {
        var rows = new List<ResponseHeader>();
        for (var i = 0; i < ResponseNames.Count; i++)
        {
            var name = ResponseNames[i]?.Trim() ?? "";
            var remove = i < ResponseActions.Count && ResponseActions[i] == "remove";
            var value = remove || i >= ResponseValues.Count ? "" : ResponseValues[i] ?? "";
            if (name.Length == 0 && value.Length == 0) continue;
            rows.Add(new ResponseHeader { Name = name, Value = value, Remove = remove });
        }
        return rows;
    }

    private record PostedAccess(List<HeaderPair> UpstreamHeaders, HeaderPair? RequireHeader);

    /// <summary>Shows the rows the user had after a refused save. The values are not sent back: a header whose name is saved
    /// shows "set" (and stays as it is when the value is left empty), a new one has to be typed again.</summary>
    private void ShowPostedAccess()
    {
        var existing = IsNew ? null : _store.Routes.FirstOrDefault(x => x.Id == Id);
        HeaderRows = HeaderNames.Select((n, i) => (Name: n?.Trim() ?? "", Value: i < HeaderValues.Count ? HeaderValues[i] : null))
            .Where(r => r.Name.Length > 0 || !string.IsNullOrEmpty(r.Value))
            .Select(r => new HeaderRow(r.Name, existing?.UpstreamHeaders.Any(h => h.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)) == true))
            .ToList();
        RequireSet = existing?.RequireHeader is { } h && h.Name.Equals(RequireName?.Trim(), StringComparison.OrdinalIgnoreCase);
        HeaderValues = new();
        RequireValue = null;
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
