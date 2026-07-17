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
        Response.Cookies.Append(AuthService.CookieName, session.Token.ToString(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            Expires = session.ExpiryDate,
            Path = "/"
        });

        // Only allow local redirects to avoid open-redirect via ?returnUrl=.
        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
            return Redirect(ReturnUrl);
        return RedirectToPage("/Index");
    }
}
