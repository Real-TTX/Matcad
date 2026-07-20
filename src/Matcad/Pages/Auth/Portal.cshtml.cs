using Matcad.Config;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Auth;

/// <summary>
/// The Matcad login portal for forward-auth. It is scoped to one authentication
/// (?auth=&lt;id&gt;): it validates against that authentication's own user list
/// and renders its branding. On success it issues the per-authentication forward
/// cookie and redirects back to the originally requested URL.
/// </summary>
public class PortalModel : PageModel
{
    private readonly ConfigStore _store;
    private readonly ForwardAuthTokens _tokens;
    public PortalModel(ConfigStore store, ForwardAuthTokens tokens) { _store = store; _tokens = tokens; }

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public long Auth { get; set; }
    [BindProperty(SupportsGet = true)] public string? Rd { get; set; }

    public AuthenticationConfig? AuthConfig { get; private set; }
    public string? Error { get; private set; }

    private AuthenticationConfig? Load() =>
        _store.Authentications.FirstOrDefault(a => a.Id == Auth && a.Type == AuthType.Matcad);

    public void OnGet() => AuthConfig = Load();

    public IActionResult OnPost()
    {
        AuthConfig = Load();
        if (AuthConfig == null)
        {
            Error = "Unknown login.";
            return Page();
        }

        var user = AuthConfig.Users.FirstOrDefault(u =>
            u.Username.Equals(Username?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (user == null || !BCrypt.Net.BCrypt.Verify(Password, user.PasswordHash))
        {
            Error = "Incorrect username or password.";
            return Page();
        }

        var baseDomain = _store.Settings.BaseDomain?.Trim();
        // Behind Caddy the request to Matcad is plain HTTP; trust X-Forwarded-Proto
        // so the cookie is still marked Secure for the real HTTPS client connection.
        var https = Request.IsHttps ||
            string.Equals(Request.Headers["X-Forwarded-Proto"].FirstOrDefault(), "https", StringComparison.OrdinalIgnoreCase);
        // The public host the browser used (Caddy forwards it); host only, no port.
        var currentHost = (Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? Request.Host.Value ?? "")
            .Split(',')[0].Split(':')[0].Trim();

        Response.Cookies.Append(ForwardAuthTokens.CookieName(AuthConfig.Id),
            _tokens.Issue(AuthConfig.Id, user.Username), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = https,
                Expires = DateTimeOffset.UtcNow.Add(ForwardAuthTokens.Lifetime),
                Path = "/",
                Domain = string.IsNullOrEmpty(baseDomain) ? null : baseDomain
            });

        return Redirect(SafeRedirect(Rd, baseDomain, currentHost));
    }

    /// <summary>Avoid open redirects: allow the same host the user is on (covers the
    /// single-host / no-base-domain case), any host within the configured base
    /// domain, or a relative path. Everything else falls back to "/".</summary>
    private static string SafeRedirect(string? rd, string? baseDomain, string? currentHost)
    {
        if (string.IsNullOrWhiteSpace(rd)) return "/";
        if (!Uri.TryCreate(rd, UriKind.Absolute, out var uri))
            return rd.StartsWith('/') && !rd.StartsWith("//") ? rd : "/";
        if (uri.Scheme != "http" && uri.Scheme != "https") return "/";
        var host = uri.Host;
        if (!string.IsNullOrEmpty(currentHost) && host.Equals(currentHost, StringComparison.OrdinalIgnoreCase))
            return rd; // back to the same protected host
        if (!string.IsNullOrEmpty(baseDomain) &&
            (host.Equals(baseDomain, StringComparison.OrdinalIgnoreCase) ||
             host.EndsWith("." + baseDomain, StringComparison.OrdinalIgnoreCase)))
            return rd;
        return "/";
    }
}
