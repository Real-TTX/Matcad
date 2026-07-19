using Matcad.Auth;
using Matcad.Config;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Settings;

/// <summary>
/// Imports an existing Caddyfile: adapts it to JSON (Caddy's own adapter), maps
/// the recognizable parts to Matcad routes/providers/basic-auth, and keeps the
/// rest as raw passthrough. Preview first, then apply.
/// </summary>
public class ImportModel : PageModel
{
    private readonly CaddyfileAdapter _adapter;
    private readonly CaddyfileImporter _importer;
    private readonly ConfigStore _store;
    private readonly CaddyService _caddy;
    public ImportModel(CaddyfileAdapter adapter, CaddyfileImporter importer, ConfigStore store, CaddyService caddy)
    { _adapter = adapter; _importer = importer; _store = store; _caddy = caddy; }

    [BindProperty] public string Caddyfile { get; set; } = "";
    public string? Error { get; private set; }
    public string? Warnings { get; private set; }
    public CaddyfileImporter.Plan? Preview { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(Caddyfile)) { Error = "Please paste a Caddyfile."; return Page(); }
        var res = await _adapter.AdaptAsync(Caddyfile);
        if (res.Json == null) { Error = res.Error; return Page(); }
        Warnings = res.Warnings;
        Preview = _importer.Analyze(res.Json);
        return Page();
    }

    public async Task<IActionResult> OnPostApplyAsync()
    {
        var res = await _adapter.AdaptAsync(Caddyfile);
        if (res.Json == null) { Error = res.Error; return Page(); }
        var plan = _importer.Analyze(res.Json);
        var actor = User.GetUserId();

        // Providers: reuse an existing provider only when its type AND credentials
        // match; otherwise import the one from the Caddyfile (it carries the real
        // credentials). This prevents silently attaching imported wildcard routes
        // to the seeded example provider — or any same-type provider whose secrets
        // differ — which would break the DNS-01 challenge and thus HTTPS.
        var providerIdByType = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in plan.Providers)
        {
            var match = _store.Providers.FirstOrDefault(e =>
                e.Type.Equals(p.Type, StringComparison.OrdinalIgnoreCase)
                && SameCredentials(e.Credentials, p.Credentials));
            if (match != null) { providerIdByType[p.Type] = match.Id; continue; }
            _store.UpsertProvider(p, actor);
            providerIdByType[p.Type] = p.Id;
        }

        var existingHosts = _store.Routes.Select(r => r.Host).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int imported = 0, skipped = 0;
        foreach (var ir in plan.Routes)
        {
            if (existingHosts.Contains(ir.Route.Host)) { skipped++; continue; }

            if (ir.BasicUsers.Count > 0)
            {
                var auth = new AuthenticationConfig
                {
                    Name = $"Import: {ir.Route.Host}", Type = AuthType.BasicAuth, Users = ir.BasicUsers
                };
                _store.UpsertAuthentication(auth, actor);
                ir.Route.AuthenticationId = auth.Id;
            }
            if (ir.ProviderName != null && providerIdByType.TryGetValue(ir.ProviderName, out var pid))
                ir.Route.ProviderId = pid;

            _store.UpsertRoute(ir.Route, actor);
            existingHosts.Add(ir.Route.Host);
            imported++;
        }

        // Raw remainder -> merge into the raw passthrough (only if it's currently empty).
        var rawNote = "";
        if (!string.IsNullOrWhiteSpace(plan.RawRemainder))
        {
            if (string.IsNullOrWhiteSpace(_store.Settings.RawCaddyJson))
            {
                var s = _store.Settings;
                s.RawCaddyJson = plan.RawRemainder;
                _store.SaveSettings(s);
                rawNote = " Unmapped parts were stored as raw passthrough.";
            }
            else
            {
                rawNote = " Note: unmapped parts were NOT applied because the raw config field is already in use — merge them manually.";
            }
        }

        var (ok, error) = await _caddy.ApplyAsync();
        TempData[ok ? "Flash" : "FlashError"] =
            $"Imported {imported} route(s), skipped {skipped} (host already exists)." + rawNote +
            (ok ? "" : $" Caddy push failed: {error}");
        return RedirectToPage("/Routes/Index");
    }

    private static bool SameCredentials(Dictionary<string, string> a, Dictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var (k, v) in a)
            if (!b.TryGetValue(k, out var bv) || bv != v) return false;
        return true;
    }
}
