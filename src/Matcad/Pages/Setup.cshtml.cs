using Matcad.Auth;
using Matcad.Config;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages;

/// <summary>First-run setup wizard: account password, domains/login, an optional
/// DNS provider (only needed for wildcard certificates), then finish.</summary>
public class SetupModel : PageModel
{
    private readonly ConfigStore _store;
    private readonly AuthService _auth;
    private readonly CaddyService _caddy;
    private readonly DnsCredentialTester _tester;
    public SetupModel(ConfigStore store, AuthService auth, CaddyService caddy, DnsCredentialTester tester)
    { _store = store; _auth = auth; _caddy = caddy; _tester = tester; }

    /// <summary>account | domains | provider | done</summary>
    [BindProperty(SupportsGet = true)] public string Step { get; set; } = "account";

    // --- account ---
    [BindProperty] public string NewUsername { get; set; } = "";
    [BindProperty] public string NewPassword { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";
    // --- domains ---
    [BindProperty] public string BaseDomain { get; set; } = "";
    [BindProperty] public string MatcadHost { get; set; } = "";
    [BindProperty] public bool SystemRouteEnabled { get; set; } = true;
    [BindProperty] public string PortalMode { get; set; } = "inline";
    [BindProperty] public string AcmeEmail { get; set; } = "";
    // --- provider ---
    [BindProperty] public string ProviderName { get; set; } = "";
    [BindProperty] public string ProviderType { get; set; } = "netcup";
    [BindProperty] public int PropagationDelay { get; set; } = 120;
    [BindProperty] public int PropagationTimeout { get; set; } = -1;

    public string? Error { get; private set; }
    public (bool? Ok, string Message)? TestResult { get; private set; }
    public Dictionary<string, string> CredentialValues { get; } = new();
    public string CurrentUsername => User.Identity?.Name ?? "admin";

    public void OnGet()
    {
        NewUsername = CurrentUsername;
        var s = _store.Settings;
        if (Step == "domains")
        {
            BaseDomain = s.BaseDomain; MatcadHost = s.MatcadHost;
            SystemRouteEnabled = s.SystemRouteEnabled;
            PortalMode = string.IsNullOrWhiteSpace(s.PortalMode) ? "inline" : s.PortalMode;
            AcmeEmail = s.AcmeEmail;
        }
    }

    private IActionResult Stay(string step, string error) { Error = error; Step = step; return Page(); }

    public async Task<IActionResult> OnPostAccountAsync()
    {
        if (string.IsNullOrEmpty(NewPassword) || NewPassword.Length < 6)
            return Stay("account", "The password must be at least 6 characters.");
        if (NewPassword != ConfirmPassword)
            return Stay("account", "The passwords do not match.");
        if (NewPassword == "admin")
            return Stay("account", "Please choose a password other than the default.");

        var uid = User.GetUserId();
        var user = uid is > 0 ? await _auth.GetUser(uid.Value) : await _auth.FindUser("admin");
        if (user == null) return Stay("account", "Current user not found.");

        var newName = string.IsNullOrWhiteSpace(NewUsername) ? user.Username : NewUsername.Trim();
        if (!newName.Equals(user.Username, StringComparison.OrdinalIgnoreCase) && await _auth.FindUser(newName) != null)
            return Stay("account", "That username is already taken.");

        await _auth.UpdateAccount(user, newName, NewPassword, uid);
        return RedirectToPage(new { step = "domains" });
    }

    public IActionResult OnPostDomains()
    {
        var s = _store.Settings;
        s.BaseDomain = BaseDomain?.Trim() ?? "";
        s.MatcadHost = MatcadHost?.Trim() ?? "";
        s.SystemRouteEnabled = SystemRouteEnabled;
        s.PortalMode = PortalMode is "inline" or "redirect" or "unauthorized" ? PortalMode : "inline";
        s.AcmeEmail = AcmeEmail?.Trim() ?? "";
        _store.SaveSettings(s);
        return RedirectToPage(new { step = "provider" });
    }

    private Dictionary<string, string> ReadProviderCreds(out string module)
    {
        var creds = new Dictionary<string, string>();
        if (ProviderType == ProviderTypes.CustomId)
        {
            module = Request.Form["CustomModule"].ToString().Trim();
            var keys = Request.Form["CustomKeys"];
            var vals = Request.Form["CustomValues"];
            for (var i = 0; i < keys.Count; i++)
            {
                var k = keys[i]?.Trim();
                if (string.IsNullOrEmpty(k)) continue;
                creds[k] = i < vals.Count ? vals[i] ?? "" : "";
            }
        }
        else
        {
            var t = ProviderTypes.Find(ProviderType);
            module = t?.Id ?? ProviderType;
            if (t != null)
                foreach (var f in t.Fields)
                    creds[f.Key] = Request.Form[$"cred_{t.Id}_{f.Key}"].ToString();
        }
        return creds;
    }

    private void PopulateCreds(Dictionary<string, string> creds)
    {
        foreach (var kv in creds) CredentialValues[$"{ProviderType}:{kv.Key}"] = kv.Value;
    }

    public async Task<IActionResult> OnPostProviderTestAsync()
    {
        Step = "provider";
        var creds = ReadProviderCreds(out var module);
        PopulateCreds(creds);
        TestResult = await _tester.TestAsync(module, creds);
        return Page();
    }

    public IActionResult OnPostProviderSave()
    {
        var creds = ReadProviderCreds(out var module);
        PopulateCreds(creds);
        if (string.IsNullOrWhiteSpace(ProviderName) || string.IsNullOrWhiteSpace(module))
            return Stay("provider", "Please give the provider a name and a type.");

        _store.UpsertProvider(new ProviderConfig { Name = ProviderName.Trim(), Type = module, Credentials = creds }, User.GetUserId());

        // Persist the DNS-01 propagation tuning (recommended for slow providers).
        var s = _store.Settings;
        s.AcmePropagationDelaySeconds = PropagationDelay < 0 ? 0 : PropagationDelay;
        s.AcmePropagationTimeoutSeconds = PropagationTimeout < 0 ? -1 : PropagationTimeout;
        _store.SaveSettings(s);

        return RedirectToPage(new { step = "done" });
    }

    public IActionResult OnPostSkipProvider() => RedirectToPage(new { step = "done" });

    public async Task<IActionResult> OnPostFinishAsync()
    {
        var s = _store.Settings;
        s.SetupCompleted = true;
        _store.SaveSettings(s);
        await _caddy.ApplyAsync();
        TempData["Flash"] = "Setup complete. Welcome to Matcad!";
        return RedirectToPage("/Index");
    }
}
