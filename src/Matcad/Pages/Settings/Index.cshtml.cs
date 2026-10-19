using Matcad.Auth;
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

    /// <summary>The tab to open: after a save the page shows the tab it was saved from.</summary>
    [BindProperty(SupportsGet = true)] public string Tab { get; set; } = "general";

    [BindProperty] public string BaseDomain { get; set; } = "";
    [BindProperty] public string MatcadHost { get; set; } = "";
    [BindProperty] public bool SystemRouteEnabled { get; set; } = true;
    [BindProperty] public string PortalMode { get; set; } = "inline";
    [BindProperty] public string AuthPortalUrl { get; set; } = "";
    [BindProperty] public int LogRetentionDays { get; set; } = 30;
    [BindProperty] public long LogRetentionMaxRows { get; set; } = 1_000_000;
    [BindProperty] public string CaddyAdminUrl { get; set; } = "";
    [BindProperty] public string AcmeEmail { get; set; } = "";
    [BindProperty] public int AcmePropagationDelaySeconds { get; set; }
    [BindProperty] public int AcmePropagationTimeoutSeconds { get; set; }
    [BindProperty] public string RawCaddyJson { get; set; } = "";

    [BindProperty] public bool DockerEnabled { get; set; }
    [BindProperty] public string DockerEndpoint { get; set; } = "";
    [BindProperty] public string DockerBaseDomain { get; set; } = "";
    [BindProperty] public bool DockerRequireLabel { get; set; } = true;

    private static readonly string[] Tabs = ["general", "users", "certificates", "docker", "caddy", "system"];

    private const long MaxBackupBytes = 20 * 1024 * 1024;

    [BindProperty] public IFormFile? BackupFile { get; set; }

    public List<User> Users { get; private set; } = new();
    public string? CaddyStatus { get; private set; }
    public string CaddyConfigJson { get; private set; } = "";
    /// <summary>The config actually running in Caddy (fetched via admin API).</summary>
    public string CaddyLiveJson { get; private set; } = "";
    public IReadOnlyList<RouteConfig> DockerDiscovered => _dockerRoutes.Routes;
    public IReadOnlyList<DockerContainerInfo> DockerContainers => _docker.Containers;
    public string? DockerError => _docker.LastError;
    public CertificatePlanner.CertPlan Certificates { get; private set; } = new(new(), new(), new());

    public async Task OnGetAsync()
    {
        if (!Tabs.Contains(Tab)) Tab = "general";
        Load();
        Users = await _auth.ListUsers();
        Certificates = CertificatePlanner.Plan(_routes.All(), _store.Providers);
        CaddyConfigJson = SecretRedaction.Redact(_caddy.BuildJson());
        var running = await _caddy.GetRunningConfigAsync();
        CaddyStatus = running != null ? "reachable" : "unreachable";
        CaddyLiveJson = running != null ? SecretRedaction.Redact(running) : "// Caddy unreachable";
    }

    private void Load()
    {
        var s = _store.Settings;
        BaseDomain = s.BaseDomain;
        MatcadHost = s.MatcadHost;
        SystemRouteEnabled = s.SystemRouteEnabled;
        PortalMode = string.IsNullOrWhiteSpace(s.PortalMode) ? "inline" : s.PortalMode;
        AuthPortalUrl = s.AuthPortalUrl;
        LogRetentionDays = s.LogRetentionDays;
        LogRetentionMaxRows = s.LogRetentionMaxRows;
        CaddyAdminUrl = s.CaddyAdminUrl;
        AcmeEmail = s.AcmeEmail;
        AcmePropagationDelaySeconds = s.AcmePropagationDelaySeconds;
        AcmePropagationTimeoutSeconds = s.AcmePropagationTimeoutSeconds;
        RawCaddyJson = s.RawCaddyJson;
        DockerEnabled = s.Docker.Enabled;
        DockerEndpoint = s.Docker.Endpoint;
        DockerBaseDomain = s.Docker.BaseDomain;
        DockerRequireLabel = s.Docker.RequireEnableLabel;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var baseDomain = BaseDomain?.Trim().ToLowerInvariant() ?? "";
        var matcadHost = MatcadHost?.Trim().ToLowerInvariant() ?? "";
        if ((baseDomain.Length > 0 && !RouteEditor.IsHostName(baseDomain)) || (matcadHost.Length > 0 && !RouteEditor.IsHostName(matcadHost)))
        {
            TempData["FlashError"] = "Domains must be plain names like example.com (no http://, path or port). Nothing was saved.";
            return RedirectToPage("Index", new { tab = "general" });
        }

        var portalUrl = AuthPortalUrl?.Trim() ?? "";
        if (portalUrl.Length > 0 && !(Uri.TryCreate(portalUrl, UriKind.Absolute, out var portal) && portal.Scheme is "http" or "https"
                                      && portal.PathAndQuery == "/" && !portalUrl.Any(char.IsWhiteSpace)))
        {
            TempData["FlashError"] = "The login portal address must be an http(s) address without a path, like https://login.example.com. Nothing was saved.";
            return RedirectToPage("Index", new { tab = "general" });
        }

        var s = _store.Settings;
        s.BaseDomain = baseDomain;
        s.MatcadHost = matcadHost;
        s.SystemRouteEnabled = SystemRouteEnabled;
        s.PortalMode = PortalMode is "inline" or "redirect" or "unauthorized" ? PortalMode : "inline";
        s.AuthPortalUrl = portalUrl;
        s.LogRetentionDays = LogRetentionDays < 1 ? 1 : LogRetentionDays;
        s.LogRetentionMaxRows = LogRetentionMaxRows < 0 ? 0 : LogRetentionMaxRows;
        s.CaddyAdminUrl = string.IsNullOrWhiteSpace(CaddyAdminUrl) ? "http://caddy:2019" : CaddyAdminUrl.Trim();
        s.AcmeEmail = AcmeEmail?.Trim() ?? "";
        s.AcmePropagationDelaySeconds = AcmePropagationDelaySeconds < 0 ? 0 : AcmePropagationDelaySeconds;
        s.AcmePropagationTimeoutSeconds = AcmePropagationTimeoutSeconds < 0 ? -1 : AcmePropagationTimeoutSeconds;
        _store.SaveSettings(s); // Docker sub-settings preserved (edited on their own tab).

        var (ok, error) = await _caddy.ApplyAsync();
        TempData[ok ? "Flash" : "FlashError"] = ok
            ? "Settings saved and Caddy configuration updated."
            : $"Settings saved. Caddy push failed: {error}";
        return RedirectToPage("Index", new { tab = "general" });
    }

    public IActionResult OnPostDocker()
    {
        var dockerDomain = DockerBaseDomain?.Trim().ToLowerInvariant() ?? "";
        if (dockerDomain.Length > 0 && !RouteEditor.IsHostName(dockerDomain))
        {
            TempData["FlashError"] = "The base domain must be a plain name like example.com. Nothing was saved.";
            return RedirectToPage("Index", new { tab = "docker" });
        }
        var s = _store.Settings;

        s.Docker.Enabled = DockerEnabled;
        s.Docker.Endpoint = string.IsNullOrWhiteSpace(DockerEndpoint) ? "unix:///var/run/docker.sock" : DockerEndpoint.Trim();
        s.Docker.BaseDomain = dockerDomain;
        s.Docker.RequireEnableLabel = DockerRequireLabel;
        _store.SaveSettings(s);

        _docker.RequestRefresh(); // pick up the change immediately
        TempData["Flash"] = "Docker settings saved. Containers are being re-scanned.";
        return RedirectToPage("Index", new { tab = "docker" });
    }

    public async Task<IActionResult> OnPostLoadExampleAsync()
    {
        Matcad.Config.ExampleData.Seed(_store, User.GetUserId());
        var (ok, error) = await _caddy.ApplyAsync();
        TempData[ok ? "Flash" : "FlashError"] = ok
            ? "Example data loaded (routes are disabled)."
            : $"Example data loaded. Caddy push failed: {error}";
        return RedirectToPage("Index", new { tab = "system" });
    }

    public IActionResult OnPostRerunSetup()
    {
        var s = _store.Settings;
        s.SetupCompleted = false;
        _store.SaveSettings(s);
        return Redirect("/Setup");
    }

    public async Task<IActionResult> OnPostRepushAsync()
    {
        var (ok, error) = await _caddy.ApplyAsync();
        TempData[ok ? "Flash" : "FlashError"] = ok
            ? "Caddy configuration re-pushed."
            : $"Caddy push failed: {error}";
        return RedirectToPage("Index", new { tab = "general" });
    }

    public async Task<IActionResult> OnGetExportAsync()
    {
        var backup = new BackupData
        {
            ExportedAt = DateTime.UtcNow,
            Settings = _store.Settings,
            Providers = _store.Providers.ToList(),
            Routes = _store.Routes.ToList(),
            Authentications = _store.Authentications.ToList(),
            Sequences = _store.SequenceSnapshot(),
            Users = (await _auth.ListUsers())
                .Select(u => new BackupUser { Username = u.Username, Email = u.Email, PasswordHash = u.PasswordHash, Role = u.Role.ToString(), MustChangePassword = u.MustChangePassword, ExternalSubject = u.ExternalSubject })
                .ToList()
        };
        var json = System.Text.Json.JsonSerializer.Serialize(backup,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        var name = $"matcad-backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json";
        return File(System.Text.Encoding.UTF8.GetBytes(json), "application/json", name);
    }

    public async Task<IActionResult> OnPostRestoreAsync()
    {
        IActionResult Fail(string message)
        {
            TempData["FlashError"] = message;
            return RedirectToPage("Index", new { tab = "system" });
        }

        if (BackupFile is not { Length: > 0 }) return Fail("Please choose a backup file.");
        if (BackupFile.Length > MaxBackupBytes) return Fail("The backup file is too large.");

        BackupData? backup;
        try
        {
            using var reader = new StreamReader(BackupFile.OpenReadStream());
            backup = System.Text.Json.JsonSerializer.Deserialize<BackupData>(await reader.ReadToEndAsync(),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            return Fail("Invalid backup file: " + ex.Message);
        }
        if (backup is not { Version: >= 1, Settings: not null, Providers: not null, Routes: not null, Authentications: not null, Users: not null })
            return Fail("Unrecognized backup format.");

        // Everything is checked before anything is replaced.
        var (users, usersError) = BackupRestore.CheckUsers(backup.Users);
        if (usersError != null) return Fail(usersError);
        BackupRestore.SanitizeSettings(backup.Settings);

        _store.ImportConfig(backup.Settings, backup.Providers, backup.Routes, backup.Authentications, backup.Sequences);
        if (users.Count > 0)
            await _auth.ReplaceAllUsers(users, User.GetUserId());

        var (ok, error) = await _caddy.ApplyAsync();
        TempData[ok ? "Flash" : "FlashError"] =
            $"Backup restored ({backup.Providers.Count} providers, {backup.Routes.Count} routes, " +
            $"{backup.Authentications.Count} authentications, {users.Count} users)." +
            (ok ? "" : $" Caddy push failed: {error}");
        return RedirectToPage("Index", new { tab = "system" });
    }

    public async Task<IActionResult> OnPostRawAsync()
    {
        var raw = RawCaddyJson?.Trim() ?? "";
        if (raw.Length > 0)
        {
            try { System.Text.Json.Nodes.JsonNode.Parse(raw); }
            catch (Exception ex)
            {
                TempData["FlashError"] = "Raw Caddy JSON is not valid JSON: " + ex.Message;
                return RedirectToPage("Index", new { tab = "caddy" });
            }
        }
        var s = _store.Settings;
        s.RawCaddyJson = raw;
        _store.SaveSettings(s);

        var (ok, error) = await _caddy.ApplyAsync();
        TempData[ok ? "Flash" : "FlashError"] = ok
            ? "Raw config saved and merged into Caddy."
            : $"Saved, but Caddy rejected the merged config: {error}";
        return RedirectToPage("Index", new { tab = "caddy" });
    }
}
