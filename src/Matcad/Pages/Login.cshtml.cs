using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages;

public class LoginModel : PageModel
{
    private readonly AuthService _auth;
    public LoginModel(AuthService auth) => _auth = auth;

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
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
        // Behind Caddy the connection to Matcad is plain HTTP; trust X-Forwarded-Proto
        // so the session cookie is still Secure for the real HTTPS client.
        var https = Request.IsHttps ||
            string.Equals(Request.Headers["X-Forwarded-Proto"].FirstOrDefault(), "https", StringComparison.OrdinalIgnoreCase);
        Response.Cookies.Append(AuthService.CookieName, session.Token.ToString(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = https,
            Expires = session.ExpiryDate,
            Path = "/"
        });

        // Only allow local redirects to avoid open-redirect via ?returnUrl=.
        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
            return Redirect(ReturnUrl);
        return RedirectToPage("/Index");
    }
}
