using Matcad.Config;
using Matcad.Data;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Settings;

public class IndexModel : PageModel
{
    private readonly ConfigStore _store;
    private readonly CaddyService _caddy;
    private readonly AuthService _auth;
    private readonly DockerService _docker;
    private readonly DockerRouteCache _dockerRoutes;
    private readonly RouteProvider _routes;

    public IndexModel(ConfigStore store, CaddyService caddy, AuthService auth,
        DockerService docker, DockerRouteCache dockerRoutes, RouteProvider routes)
    {
        _store = store; _caddy = caddy; _auth = auth; _docker = docker; _dockerRoutes = dockerRoutes; _routes = routes;
    }

    [BindProperty] public string BaseDomain { get; set; } = "";
    [BindProperty] public string MatcadHost { get; set; } = "";
    [BindProperty] public bool SystemRouteEnabled { get; set; } = true;
    [BindProperty] public string AuthPortalUrl { get; set; } = "";
    [BindProperty] public int LogRetentionDays { get; set; } = 30;
    [BindProperty] public long LogRetentionMaxRows { get; set; } = 1_000_000;
    [BindProperty] public string CaddyAdminUrl { get; set; } = "";
    [BindProperty] public string AcmeEmail { get; set; } = "";

    [BindProperty] public bool DockerEnabled { get; set; }
    [BindProperty] public string DockerEndpoint { get; set; } = "";
    [BindProperty] public string DockerBaseDomain { get; set; } = "";
    [BindProperty] public bool DockerRequireLabel { get; set; } = true;

    public List<User> Users { get; private set; } = new();
    public string? CaddyStatus { get; private set; }
    public string CaddyConfigJson { get; private set; } = "";
    /// <summary>The config actually running in Caddy (fetched via admin API).</summary>
    public string CaddyLiveJson { get; private set; } = "";
    public IReadOnlyList<RouteConfig> DockerDiscovered => _dockerRoutes.Routes;
    public string? DockerError => _docker.LastError;
    public CertificatePlanner.CertPlan Certificates { get; private set; } = new(new(), new(), new());

    public async Task OnGetAsync()
    {
        Load();
        Users = await _auth.ListUsers();
        Certificates = CertificatePlanner.Plan(_routes.All(), _store.Providers);
        CaddyConfigJson = _caddy.BuildJson();
        var running = await _caddy.GetRunningConfigAsync();
        CaddyStatus = running != null ? "reachable" : "unreachable";
        CaddyLiveJson = running != null ? Prettify(running) : "// Caddy unreachable";
    }

    private static string Prettify(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return System.Text.Json.JsonSerializer.Serialize(doc,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch { return json; }
    }

    private void Load()
    {
        var s = _store.Settings;
        BaseDomain = s.BaseDomain;
        MatcadHost = s.MatcadHost;
        SystemRouteEnabled = s.SystemRouteEnabled;
        AuthPortalUrl = s.AuthPortalUrl;
        LogRetentionDays = s.LogRetentionDays;
        LogRetentionMaxRows = s.LogRetentionMaxRows;
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
        s.MatcadHost = MatcadHost?.Trim() ?? "";
        s.SystemRouteEnabled = SystemRouteEnabled;
        s.AuthPortalUrl = AuthPortalUrl?.Trim() ?? "";
        s.LogRetentionDays = LogRetentionDays < 1 ? 1 : LogRetentionDays;
        s.LogRetentionMaxRows = LogRetentionMaxRows < 0 ? 0 : LogRetentionMaxRows;
        s.CaddyAdminUrl = string.IsNullOrWhiteSpace(CaddyAdminUrl) ? "http://caddy:2019" : CaddyAdminUrl.Trim();
        s.AcmeEmail = AcmeEmail?.Trim() ?? "";
        _store.SaveSettings(s); // Docker sub-settings preserved (edited on their own tab).

        var (ok, error) = await _caddy.ApplyAsync();
        TempData[ok ? "Flash" : "FlashError"] = ok
            ? "Settings saved and Caddy configuration updated."
            : $"Settings saved. Caddy push failed: {error}";
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
        TempData["Flash"] = "Docker settings saved. Containers are being re-scanned.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostRepushAsync()
    {
        var (ok, error) = await _caddy.ApplyAsync();
        TempData[ok ? "Flash" : "FlashError"] = ok
            ? "Caddy configuration re-pushed."
            : $"Caddy-Push fehlgeschlagen: {error}";
        return RedirectToPage("Index");
    }
}
