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
        Response.Cookies.Append(ForwardAuthTokens.CookieName(AuthConfig.Id),
            _tokens.Issue(AuthConfig.Id, user.Username), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttps,
                Expires = DateTimeOffset.UtcNow.Add(ForwardAuthTokens.Lifetime),
                Path = "/",
                Domain = string.IsNullOrEmpty(baseDomain) ? null : baseDomain
            });

        return Redirect(SafeRedirect(Rd, baseDomain));
    }

    /// <summary>Only redirect to URLs within the configured base domain (and only
    /// relative paths when no base domain is set) to avoid open redirects.</summary>
    private static string SafeRedirect(string? rd, string? baseDomain)
    {
        if (string.IsNullOrWhiteSpace(rd)) return "/";
        if (!Uri.TryCreate(rd, UriKind.Absolute, out var uri))
            return rd.StartsWith('/') && !rd.StartsWith("//") ? rd : "/";
        if (uri.Scheme != "http" && uri.Scheme != "https") return "/";
        if (string.IsNullOrEmpty(baseDomain)) return "/"; // no base domain -> no absolute redirects
        var host = uri.Host;
        if (!host.Equals(baseDomain, StringComparison.OrdinalIgnoreCase) &&
            !host.EndsWith("." + baseDomain, StringComparison.OrdinalIgnoreCase))
            return "/";
        return rd;
    }
}
