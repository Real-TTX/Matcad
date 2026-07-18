using Matcad.Config;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Auth;

/// <summary>
/// Forward-auth verification endpoint called by Caddy for every request to a
/// route protected by a "Matcad" authentication. Returns 2xx when the forward
/// session cookie is valid (Caddy then lets the request through), otherwise a
/// 302 to the login portal that Caddy relays to the browser.
/// </summary>
public class VerifyModel : PageModel
{
    private readonly AuthService _auth;
    private readonly ConfigStore _store;
    public VerifyModel(AuthService auth, ConfigStore store) { _auth = auth; _store = store; }

    public async Task<IActionResult> OnGet()
    {
        if (Request.Cookies.TryGetValue(AuthService.ForwardCookieName, out var raw) &&
            Guid.TryParse(raw, out var token))
        {
            var user = await _auth.GetUserBySession(token);
            if (user != null)
            {
                Response.Headers["X-Matcad-User"] = user.Username;
                return StatusCode(200);
            }
        }

        // Reconstruct the original URL from the headers Caddy forwards.
        var proto = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? "https";
        var host = Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? "";
        var uri = Request.Headers["X-Forwarded-Uri"].FirstOrDefault() ?? "/";
        var original = $"{proto}://{host}{uri}";

        var portal = _store.Settings.EffectivePortalUrl();
        var target = string.IsNullOrWhiteSpace(portal)
            ? $"/auth/portal?rd={Uri.EscapeDataString(original)}"
            : $"{portal}/auth/portal?rd={Uri.EscapeDataString(original)}";

        return Redirect(target);
    }
}
