using Matcad.Auth;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages;

public class LoginModel : PageModel
{
    private readonly AuthService _auth;
    private readonly LoginAttemptTracker _attempts;
    public LoginModel(AuthService auth, LoginAttemptTracker attempts) { _auth = auth; _attempts = attempts; }

    /// <summary>Username or e-mail address.</summary>
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string Password { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    public string? Error { get; private set; }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync()
    {
        // The attempt is booked before the password is checked: parallel guesses cannot all slip through.
        if (_attempts.Begin("admin", Username ?? "", HttpContext.ClientIp()) is not { } attempt)
        {
            Error = "Too many failed attempts. Please wait a few minutes and try again.";
            return Page();
        }

        var user = await _auth.ValidateCredentials(Username ?? "", Password ?? "");
        if (user == null)
        {
            Error = "Incorrect username or password.";
            return Page();
        }
        _attempts.Succeeded(attempt);

        var session = await _auth.CreateSession(user);
        Response.Cookies.Append(AuthService.CookieName, session.Token.ToString(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttpsClient(),
            Expires = session.ExpiryDate,
            Path = "/"
        });

        // Only allow local redirects to avoid open-redirect via ?returnUrl=.
        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
            return Redirect(ReturnUrl);
        return RedirectToPage("/Index");
    }
}
