using Matcad.Auth;
using Matcad.Config;
using Matcad.Controls;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages;

/// <summary>First-run setup wizard: account, domains/login, an optional
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
    [BindProperty] public string? NewEmail { get; set; }
    [BindProperty] public string? NewPassword { get; set; }
    [BindProperty] public string? ConfirmPassword { get; set; }
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
    public Dictionary<string, string> CustomCredentials { get; } = new();
    public string CustomModule { get; private set; } = "";

    /// <summary>The user still has the default or an administrator-set password and has to choose their own first.</summary>
    public bool MustChange => User.MustChangePassword();

    /// <summary>Whoever has to choose a new password can do nothing else until then.</summary>
    public override void OnPageHandlerExecuting(PageHandlerExecutingContext context)
    {
        var handler = context.HandlerMethod?.MethodInfo.Name;
        if (MustChange && handler is not (nameof(OnGetAsync) or nameof(OnPostAccountAsync)))
            context.Result = RedirectToPage(new { step = "account" });
        base.OnPageHandlerExecuting(context);
    }

    public async Task OnGetAsync()
    {
        if (MustChange) Step = "account";
        var s = _store.Settings;
        if (Step == "account")
        {
            var user = await CurrentUser();
            NewUsername = user?.Username ?? "";
            NewEmail = user?.Email;
        }
        else if (Step == "domains")
        {
            BaseDomain = s.BaseDomain; MatcadHost = s.MatcadHost;
            SystemRouteEnabled = s.SystemRouteEnabled;
            PortalMode = string.IsNullOrWhiteSpace(s.PortalMode) ? "inline" : s.PortalMode;
            AcmeEmail = s.AcmeEmail;
        }
    }

    private Task<Data.User?> CurrentUser() =>
        User.GetUserId() is { } id ? _auth.GetUser(id) : Task.FromResult<Data.User?>(null);

    private IActionResult Stay(string step, string error) { Error = error; Step = step; return Page(); }

    public async Task<IActionResult> OnPostAccountAsync()
    {
        var user = await CurrentUser();
        if (user == null) return Stay("account", "Current user not found.");

        var changePassword = MustChange || !string.IsNullOrEmpty(NewPassword);
        if (changePassword &&
            AuthService.ValidatePasswordChange(user, requireCurrent: false, null, NewPassword, ConfirmPassword) is { } passwordError)
            return Stay("account", passwordError);

        var username = string.IsNullOrWhiteSpace(NewUsername) ? user.Username : NewUsername.Trim();
        var email = AuthService.NormalizeEmail(NewEmail);
        if (await _auth.ValidateIdentity(username, email, user.Id) is { } identityError)
            return Stay("account", identityError);

        user.Username = username;
        user.Email = email;
        await _auth.UpdateUser(user, changePassword ? NewPassword : null, mustChangePassword: false, user.Id, keepSession: Request.GetSessionToken());
        return RedirectToPage(new { step = "domains" });
    }

    public IActionResult OnPostDomains()
    {
        var baseDomain = BaseDomain?.Trim().ToLowerInvariant() ?? "";
        var matcadHost = MatcadHost?.Trim().ToLowerInvariant() ?? "";
        if ((baseDomain.Length > 0 && !RouteEditor.IsHostName(baseDomain)) || (matcadHost.Length > 0 && !RouteEditor.IsHostName(matcadHost)))
            return Stay("domains", "Domains must be plain names like example.com (no http://, path or port).");

        var s = _store.Settings;
        s.BaseDomain = baseDomain;
        s.MatcadHost = matcadHost;
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

    /// <summary>Keeps the entered values when the form is shown again.</summary>
    private void PopulateCreds(string module, Dictionary<string, string> creds)
    {
        if (ProviderType == ProviderTypes.CustomId)
        {
            CustomModule = module;
            foreach (var kv in creds) CustomCredentials[kv.Key] = kv.Value;
        }
        else
        {
            foreach (var kv in creds) CredentialValues[$"{ProviderType}:{kv.Key}"] = kv.Value;
        }
    }

    public ProviderFieldsModel ProviderFields => new()
    {
        TypeFieldName = nameof(ProviderType), Type = ProviderType, CustomModule = CustomModule,
        Values = CredentialValues, CustomCredentials = CustomCredentials
    };

    public async Task<IActionResult> OnPostProviderTestAsync()
    {
        Step = "provider";
        var creds = ReadProviderCreds(out var module);
        PopulateCreds(module, creds);
        TestResult = await _tester.TestAsync(module, creds);
        return Page();
    }

    public async Task<IActionResult> OnPostProviderSaveAsync()
    {
        var creds = ReadProviderCreds(out var module);
        PopulateCreds(module, creds);
        if (string.IsNullOrWhiteSpace(ProviderName) || string.IsNullOrWhiteSpace(module))
            return Stay("provider", "Please give the provider a name and a type.");

        _store.UpsertProvider(new ProviderConfig { Name = ProviderName.Trim(), Type = module, Credentials = creds }, User.GetUserId());

        // Persist the DNS-01 propagation tuning (recommended for slow providers).
        var s = _store.Settings;
        s.AcmePropagationDelaySeconds = PropagationDelay < 0 ? 0 : PropagationDelay;
        s.AcmePropagationTimeoutSeconds = PropagationTimeout < 0 ? -1 : PropagationTimeout;
        _store.SaveSettings(s);

        await MarkComplete();
        return RedirectToPage(new { step = "done" });
    }

    public async Task<IActionResult> OnPostSkipProviderAsync()
    {
        await MarkComplete();
        return RedirectToPage(new { step = "done" });
    }

    /// <summary>Skip the rest of the wizard entirely (keeps whatever is set so far).</summary>
    public async Task<IActionResult> OnPostSkipAsync()
    {
        await MarkComplete();
        TempData["Flash"] = "Setup skipped. You can re-run it any time under Settings.";
        return RedirectToPage("/Index");
    }

    public async Task<IActionResult> OnPostFinishAsync()
    {
        await MarkComplete();
        TempData["Flash"] = "Setup complete. Welcome to Matcad!";
        return RedirectToPage("/Index");
    }

    /// <summary>Mark setup done (so the wizard no longer intercepts navigation) and
    /// push the current config to Caddy. Idempotent.</summary>
    private async Task MarkComplete()
    {
        var s = _store.Settings;
        if (!s.SetupCompleted) { s.SetupCompleted = true; _store.SaveSettings(s); }
        await _caddy.ApplyAsync();
    }
}
