using Matcat.Config;
using Matcat.Data;
using Matcat.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcat.Pages.Settings;

public class IndexModel : PageModel
{
    private readonly ConfigStore _store;
    private readonly CaddyService _caddy;
    private readonly AuthService _auth;
    public IndexModel(ConfigStore store, CaddyService caddy, AuthService auth)
    {
        _store = store; _caddy = caddy; _auth = auth;
    }

    [BindProperty] public string BaseDomain { get; set; } = "";
    [BindProperty] public string AuthPortalUrl { get; set; } = "";
    [BindProperty] public int LogRetentionDays { get; set; } = 30;
    [BindProperty] public string CaddyAdminUrl { get; set; } = "";
    [BindProperty] public string AcmeEmail { get; set; } = "";

    public List<User> Users { get; private set; } = new();
    public string? CaddyStatus { get; private set; }

    public async Task OnGetAsync()
    {
        Load();
        Users = await _auth.ListUsers();
        var running = await _caddy.GetRunningConfigAsync();
        CaddyStatus = running != null ? "erreichbar" : "nicht erreichbar";
    }

    private void Load()
    {
        var s = _store.Settings;
        BaseDomain = s.BaseDomain;
        AuthPortalUrl = s.AuthPortalUrl;
        LogRetentionDays = s.LogRetentionDays;
        CaddyAdminUrl = s.CaddyAdminUrl;
        AcmeEmail = s.AcmeEmail;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        _store.SaveSettings(new MatcatSettings
        {
            BaseDomain = BaseDomain?.Trim() ?? "",
            AuthPortalUrl = AuthPortalUrl?.Trim() ?? "",
            LogRetentionDays = LogRetentionDays < 1 ? 1 : LogRetentionDays,
            CaddyAdminUrl = string.IsNullOrWhiteSpace(CaddyAdminUrl) ? "http://caddy:2019" : CaddyAdminUrl.Trim(),
            AcmeEmail = AcmeEmail?.Trim() ?? ""
        });

        // Settings can change TLS/ACME and the admin URL -> re-push.
        var (ok, error) = await _caddy.ApplyAsync();
        TempData[ok ? "Flash" : "FlashError"] = ok
            ? "Einstellungen gespeichert und Caddy-Konfiguration aktualisiert."
            : $"Einstellungen gespeichert. Caddy-Push fehlgeschlagen: {error}";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostRepushAsync()
    {
        var (ok, error) = await _caddy.ApplyAsync();
        TempData[ok ? "Flash" : "FlashError"] = ok
            ? "Caddy-Konfiguration erneut übertragen."
            : $"Caddy-Push fehlgeschlagen: {error}";
        return RedirectToPage("Index");
    }
}
