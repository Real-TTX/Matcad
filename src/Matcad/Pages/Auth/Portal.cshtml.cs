using Matcad.Config;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Auth;

/// <summary>
/// The Matcad login portal for forward-auth. After a successful login it issues
/// the forward session cookie (scoped to the configured base domain so it is
/// shared across subdomains) and redirects back to the originally requested URL.
/// </summary>
public class PortalModel : PageModel
{
    private readonly AuthService _auth;
    private readonly ConfigStore _store;
    public PortalModel(AuthService auth, ConfigStore store) { _auth = auth; _store = store; }

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? Rd { get; set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await _auth.ValidateCredentials(Username, Password);
        if (user == null)
        {
            Error = "Incorrect username or password.";
            return Page();
        }

        var session = await _auth.CreateSession(user);
        var baseDomain = _store.Settings.BaseDomain?.Trim();
        Response.Cookies.Append(AuthService.ForwardCookieName, session.Token.ToString(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            Expires = session.ExpiryDate,
            Path = "/",
            Domain = string.IsNullOrEmpty(baseDomain) ? null : baseDomain
        });

        return Redirect(SafeRedirect(Rd, baseDomain));
    }

    /// <summary>Only redirect to URLs within the configured base domain.</summary>
    private static string SafeRedirect(string? rd, string? baseDomain)
    {
        if (string.IsNullOrWhiteSpace(rd)) return "/";
        if (!Uri.TryCreate(rd, UriKind.Absolute, out var uri)) return "/";
        if (uri.Scheme != "http" && uri.Scheme != "https") return "/";
        if (!string.IsNullOrEmpty(baseDomain))
        {
            var host = uri.Host;
            if (!host.Equals(baseDomain, StringComparison.OrdinalIgnoreCase) &&
                !host.EndsWith("." + baseDomain, StringComparison.OrdinalIgnoreCase))
                return "/";
        }
        return rd;
    }
}
