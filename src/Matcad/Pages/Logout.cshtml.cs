using Matcad.Auth;
using Matcad.Services;
using MatPlatform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages;

/// <summary>
/// The sign-out. A session that began at MatIdentity (or was linked there) ends there too; with MatIdentity the browser then lands on
/// <c>/Login?signedOut=1</c>, so it is not signed in again right away.
/// </summary>
public class LogoutModel : PageModel
{
    private readonly AuthService _auth;
    private readonly MatPlatformSettings _platform;

    public LogoutModel(AuthService auth, MatPlatformSettings platform)
    {
        _auth = auth;
        _platform = platform;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        string? idToken = null;
        if (SessionCookie.Token(HttpContext) is { } token)
        {
            idToken = await _auth.GetSessionIdToken(token);
            await _auth.InvalidateSession(token);
        }
        Response.Cookies.Delete(AuthService.CookieName);
        MatIdentityPendingLink.Clear(HttpContext);

        if (!_platform.IdentityEnabled)
            return RedirectToPage("/Login");

        // Through MatIdentity's end-session endpoint and /signout-callback-oidc back to /Login?signedOut=1.
        return !string.IsNullOrEmpty(idToken)
            ? MatIdentitySignIn.SignOutAtIdentity(HttpContext, idToken)
            : Redirect("/Login?signedOut=1");
    }
}
