using System.Globalization;
using Matcad.Auth;
using Matcad.Services;
using MatPlatform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages;

/// <summary>
/// The sign-in. Without MATIDENTITY_AUTHORITY: the local form, as always. With it a visit goes straight on to MatIdentity; the local form stays
/// reachable with <c>?local=1</c> (the way in for the admin when MatIdentity is down - Matcad is often the proxy in front of MatIdentity itself),
/// <c>?signedOut=1</c> stops the automatic sign-in, <c>?ssoError=1</c> says that MatIdentity failed, and when MatIdentity cannot be reached at all
/// the page falls back to the local form. <c>?link=1</c>: MatIdentity signed in a person whose name matches an account that is not linked yet; a
/// successful sign-in of exactly that account links it (<see cref="MatIdentityPendingLink"/>, <see cref="AuthService.LinkExternal"/>), another
/// account links nothing, Cancel deletes the pending link.
/// </summary>
public class LoginModel : PageModel
{
    private readonly AuthService _auth;
    private readonly MatPlatformSettings _platform;
    private readonly ILogger<LoginModel> _log;

    public LoginModel(AuthService auth, MatPlatformSettings platform, ILogger<LoginModel> log)
    {
        _auth = auth;
        _platform = platform;
        _log = log;
    }

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? Error { get; private set; }

    /// <summary><c>?local=1</c>: the local form even with MatIdentity.</summary>
    public bool Local { get; private set; }

    /// <summary><c>?signedOut=1</c>: back from the sign-out at MatIdentity, no automatic sign-in.</summary>
    public bool SignedOut { get; private set; }

    /// <summary><c>?ssoError=1</c> (or MatIdentity cannot be reached): the sign-in at MatIdentity failed.</summary>
    public bool SsoError { get; private set; }

    /// <summary><c>?denied=1</c>: MatIdentity signed the person in, but Matcad refused the account (several matches, linked to another person).</summary>
    public bool Denied { get; private set; }

    /// <summary>The pending link to an account, when MatIdentity sent the person here to prove it (<c>?link=1</c>).</summary>
    public MatIdentityPendingLink? PendingLink { get; private set; }

    /// <summary><c>?link=1</c> without a pending link: it expired (10 minutes) or was cancelled.</summary>
    public bool LinkExpired { get; private set; }

    public bool IdentityEnabled => _platform.IdentityEnabled;

    /// <summary>The address that starts the sign-in at MatIdentity again.</summary>
    public string IdentitySignInUrl => string.IsNullOrWhiteSpace(ReturnUrl) ? "/Login" : "/Login?returnUrl=" + Uri.EscapeDataString(ReturnUrl);

    /// <summary>The local form with the same return address.</summary>
    public string LocalSignInUrl => string.IsNullOrWhiteSpace(ReturnUrl) ? "/Login?local=1" : "/Login?local=1&returnUrl=" + Uri.EscapeDataString(ReturnUrl);

    public bool ShowLocalForm => !IdentityEnabled || Local;

    public async Task<IActionResult> OnGetAsync()
    {
        Prepare();

        // Auto login: straight on to MatIdentity, no local form in the way.
        if (IdentityEnabled && !Local && !SignedOut && !SsoError)
        {
            try
            {
                await HttpContext.ChallengeAsync(MatIdentitySignIn.Scheme, new AuthenticationProperties { RedirectUri = ResolveReturnUrl() });
                return new EmptyResult();
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                // MatIdentity cannot be reached (its configuration cannot be read): the local form is the way in.
                _log.LogWarning(ex, "MatIdentity is not reachable, showing the local sign-in");
                Local = true;
                SsoError = true;
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        // A posted form is the local sign-in; on an error it shows the form again.
        Local = true;
        Prepare();

        var user = await _auth.ValidateCredentials(Username, Password);
        if (user == null)
        {
            Error = "Incorrect username or password.";
            return Page();
        }

        // A pending link of MatIdentity is only ever for the account that matched; it is deleted either way.
        string? idToken = null;
        if (MatIdentityPendingLink.TakeFor(HttpContext, user.Id.ToString(CultureInfo.InvariantCulture)) is { } pending)
        {
            var (linked, error) = await _auth.LinkExternal(user.Id,
                new ExternalIdentity(pending.Subject, pending.Email, pending.Name, pending.PreferredUserName, pending.Role));
            if (linked != null)
            {
                user = linked;
                idToken = pending.IdToken;
            }
            else
            {
                _log.LogWarning("User {UserId} signed in, but was not linked with MatIdentity: {Error}", user.Id, error);
            }
        }

        var session = await _auth.CreateSession(user, idToken);
        SessionCookie.Append(HttpContext, session);
        return Redirect(ResolveReturnUrl());
    }

    /// <summary>Cancel on <c>?link=1</c>: the account is not linked, the pending link is deleted.</summary>
    public IActionResult OnPostCancelLink()
    {
        MatIdentityPendingLink.Clear(HttpContext);
        return Redirect(LocalSignInUrl);
    }

    private void Prepare()
    {
        Local = Local || Flag("local");
        SignedOut = Flag("signedOut");
        SsoError = Flag("ssoError");
        Denied = Flag("denied");
        PendingLink = MatIdentityPendingLink.Peek(HttpContext);
        LinkExpired = PendingLink is null && Flag("link");
        if (PendingLink != null && string.IsNullOrEmpty(Username))
            Username = PendingLink.Email ?? PendingLink.PreferredUserName ?? "";
    }

    /// <summary>A flag in the query: <c>1</c> or <c>true</c>.</summary>
    private bool Flag(string name) => Request.Query[name].ToString() is "1" or "true" or "True";

    /// <summary>Only local addresses, to avoid an open redirect via ?returnUrl=.</summary>
    private string ResolveReturnUrl() =>
        !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) ? ReturnUrl : "/";
}
