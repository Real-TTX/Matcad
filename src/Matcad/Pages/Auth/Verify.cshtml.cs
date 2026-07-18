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

        var mode = (_store.Settings.PortalMode ?? "inline").ToLowerInvariant();
        var query = $"?auth={authId}&rd={Uri.EscapeDataString(original)}";

        return mode switch
        {
            // Plain 401 — reveals nothing, no login form.
            "unauthorized" => new ContentResult
            {
                StatusCode = 401,
                ContentType = "text/html; charset=utf-8",
                Content = "<!doctype html><meta charset=utf-8><title>401 Unauthorized</title>" +
                          "<body style=\"font-family:system-ui;text-align:center;padding:3rem\">" +
                          "<h1>401</h1><p>Unauthorized.</p></body>"
            },
            // Redirect to the configured portal host (may reveal the Matcad host).
            "redirect" => Redirect(
                (string.IsNullOrWhiteSpace(_store.Settings.EffectivePortalUrl())
                    ? "/auth/portal" : $"{_store.Settings.EffectivePortalUrl()}/auth/portal") + query),
            // Inline (default): relative redirect -> login is served on the protected
            // host itself (Caddy routes /auth/* there to Matcad). Matcad host stays hidden.
            _ => Redirect("/auth/portal" + query)
        };
    }
}
