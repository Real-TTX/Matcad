using Matcad.Auth;
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
    // Verified when the user does not exist, so the response time does not reveal which users exist.
    private static readonly Lazy<string> TimingEqualizerHash = new(() => BCrypt.Net.BCrypt.HashPassword("matcad-timing-equalizer"));

    private readonly ConfigStore _store;
    private readonly ForwardAuthTokens _tokens;
    private readonly LoginAttemptTracker _attempts;
    public PortalModel(ConfigStore store, ForwardAuthTokens tokens, LoginAttemptTracker attempts)
    { _store = store; _tokens = tokens; _attempts = attempts; }

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

        var username = Username?.Trim() ?? "";
        if (_attempts.Begin($"portal{AuthConfig.Id}", username, HttpContext.ClientIp()) is not { } attempt)
        {
            Error = "Too many failed attempts. Please wait a few minutes and try again.";
            return Page();
        }

        var user = AuthConfig.Users.FirstOrDefault(u => u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
        var valid = PasswordHashing.Verify(Password ?? "", user?.PasswordHash ?? TimingEqualizerHash.Value);
        if (user == null || !valid)
        {
            Error = "Incorrect username or password.";
            return Page();
        }
        _attempts.Succeeded(attempt);

        var baseDomain = _store.Settings.BaseDomain?.Trim();
        // The public host the browser used (Caddy forwards it); host only, no port.
        var currentHost = (Request.Headers["X-Forwarded-Host"].FirstOrDefault() ?? Request.Host.Value ?? "")
            .Split(',')[0].Split(':')[0].Trim();

        Response.Cookies.Append(ForwardAuthTokens.CookieName(AuthConfig.Id),
            _tokens.Issue(AuthConfig.Id, user.Username, user.PasswordHash), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Lax,
                Secure = Request.IsHttpsClient(),
                Expires = DateTimeOffset.UtcNow.Add(ForwardAuthTokens.Lifetime),
                Path = "/",
                Domain = CookieDomain(currentHost, baseDomain)
            });

        return Redirect(SafeRedirect(Rd, baseDomain, currentHost));
    }

    /// <summary>Scope the forward-auth cookie so the browser sends it back on the
    /// protected host. Use the configured base domain only when the current host is
    /// actually within it (enables one sign-in across its subdomains); otherwise a
    /// host-only cookie (Domain=null), which always works - critical when protected
    /// hosts live on domains other than the base domain.</summary>
    public static string? CookieDomain(string? currentHost, string? baseDomain)
    {
        if (string.IsNullOrEmpty(baseDomain) || string.IsNullOrEmpty(currentHost)) return null;
        return IsWithin(currentHost, baseDomain) ? baseDomain : null;
    }

    /// <summary>Avoid open redirects: allow the same host the user is on (covers the
    /// single-host / no-base-domain case), any host within the configured base
    /// domain, or a path on this site. Everything else falls back to "/".</summary>
    public static string SafeRedirect(string? rd, string? baseDomain, string? currentHost)
    {
        // Browsers drop tabs and line breaks inside a URL, so "/<tab>/host" would become "//host": no control characters at all.
        if (string.IsNullOrWhiteSpace(rd) || rd.Any(char.IsControl)) return "/";
        // "//host" and "/\host" are addresses on another site, not paths.
        if (rd[0] == '/') return rd.Length > 1 && rd[1] is '/' or '\\' ? "/" : rd;

        if (!Uri.TryCreate(rd, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return "/";
        var host = uri.Host;
        var sameHost = !string.IsNullOrEmpty(currentHost) && host.Equals(currentHost, StringComparison.OrdinalIgnoreCase);
        var insideBaseDomain = !string.IsNullOrEmpty(baseDomain) && IsWithin(host, baseDomain);
        // What is sent is the address that was checked, in its canonical form, not the raw text.
        return sameHost || insideBaseDomain ? uri.AbsoluteUri : "/";
    }

    private static bool IsWithin(string host, string domain) =>
        host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
}
