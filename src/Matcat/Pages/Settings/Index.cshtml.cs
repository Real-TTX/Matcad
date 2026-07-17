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
    private readonly DockerService _docker;
    private readonly DockerRouteCache _dockerRoutes;

    public IndexModel(ConfigStore store, CaddyService caddy, AuthService auth,
        DockerService docker, DockerRouteCache dockerRoutes)
    {
        _store = store; _caddy = caddy; _auth = auth; _docker = docker; _dockerRoutes = dockerRoutes;
    }

    [BindProperty] public string BaseDomain { get; set; } = "";
    [BindProperty] public string AuthPortalUrl { get; set; } = "";
    [BindProperty] public int LogRetentionDays { get; set; } = 30;
    [BindProperty] public string CaddyAdminUrl { get; set; } = "";
    [BindProperty] public string AcmeEmail { get; set; } = "";

    [BindProperty] public bool DockerEnabled { get; set; }
    [BindProperty] public string DockerEndpoint { get; set; } = "";
    [BindProperty] public string DockerBaseDomain { get; set; } = "";
    [BindProperty] public bool DockerRequireLabel { get; set; } = true;

    public List<User> Users { get; private set; } = new();
    public string? CaddyStatus { get; private set; }
    public string CaddyConfigJson { get; private set; } = "";
    public IReadOnlyList<RouteConfig> DockerDiscovered => _dockerRoutes.Routes;
    public string? DockerError => _docker.LastError;

    public async Task OnGetAsync()
    {
        Load();
        Users = await _auth.ListUsers();
        CaddyConfigJson = _caddy.BuildJson();
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
        DockerEnabled = s.Docker.Enabled;
        DockerEndpoint = s.Docker.Endpoint;
        DockerBaseDomain = s.Docker.BaseDomain;
        DockerRequireLabel = s.Docker.RequireEnableLabel;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var s = _store.Settings;
        s.BaseDomain = BaseDomain?.Trim() ?? "";
        s.AuthPortalUrl = AuthPortalUrl?.Trim() ?? "";
        s.LogRetentionDays = LogRetentionDays < 1 ? 1 : LogRetentionDays;
        s.CaddyAdminUrl = string.IsNullOrWhiteSpace(CaddyAdminUrl) ? "http://caddy:2019" : CaddyAdminUrl.Trim();
        s.AcmeEmail = AcmeEmail?.Trim() ?? "";
        _store.SaveSettings(s); // Docker sub-settings preserved (edited on their own tab).

        var (ok, error) = await _caddy.ApplyAsync();
        TempData[ok ? "Flash" : "FlashError"] = ok
            ? "Einstellungen gespeichert und Caddy-Konfiguration aktualisiert."
            : $"Einstellungen gespeichert. Caddy-Push fehlgeschlagen: {error}";
        return RedirectToPage("Index");
    }

    public IActionResult OnPostDocker()
    {
        var s = _store.Settings;
        s.Docker.Enabled = DockerEnabled;
        s.Docker.Endpoint = string.IsNullOrWhiteSpace(DockerEndpoint) ? "unix:///var/run/docker.sock" : DockerEndpoint.Trim();
        s.Docker.BaseDomain = DockerBaseDomain?.Trim() ?? "";
        s.Docker.RequireEnableLabel = DockerRequireLabel;
        _store.SaveSettings(s);

        _docker.RequestRefresh(); // pick up the change immediately
        TempData["Flash"] = "Docker-Einstellungen gespeichert. Container werden neu erfasst.";
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
