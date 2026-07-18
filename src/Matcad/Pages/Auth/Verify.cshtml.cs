using Matcad.Config;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Auth;

/// <summary>
/// Forward-auth verification endpoint called by Caddy for every request to a
/// route protected by a "Matcad" authentication. Caddy passes the authentication
/// id via X-Matcad-Auth. A valid per-authentication cookie (stateless, Data
/// Protection) yields 2xx; otherwise a 302 to that authentication's login portal.
/// </summary>
public class VerifyModel : PageModel
{
    private readonly ForwardAuthTokens _tokens;
    private readonly ConfigStore _store;
    public VerifyModel(ForwardAuthTokens tokens, ConfigStore store) { _tokens = tokens; _store = store; }

    public IActionResult OnGet()
    {
        long.TryParse(Request.Headers["X-Matcad-Auth"].FirstOrDefault(), out var authId);

        if (authId > 0 &&
            Request.Cookies.TryGetValue(ForwardAuthTokens.CookieName(authId), out var token) &&
            !string.IsNullOrEmpty(token))
        {
            var user = _tokens.Validate(token, authId);
            if (user != null)
            {
                Response.Headers["X-Matcad-User"] = user;
                return StatusCode(200);
            }
        }

        // Reconstruct the original URL from the headers Caddy forwards.
        var proto = Request.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? "https";
        var host = Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? "";
        var uri = Request.Headers["X-Forwarded-Uri"].FirstOrDefault() ?? "/";
        var original = $"{proto}://{host}{uri}";

        var portal = _store.Settings.EffectivePortalUrl();
        var basePath = string.IsNullOrWhiteSpace(portal) ? "/auth/portal" : $"{portal}/auth/portal";
        var target = $"{basePath}?auth={authId}&rd={Uri.EscapeDataString(original)}";
        return Redirect(target);
    }
}
