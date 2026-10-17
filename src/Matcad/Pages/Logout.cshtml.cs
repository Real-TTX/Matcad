using Matcad.Auth;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages;

/// <summary>Sign-out is a POST (with antiforgery token), so a foreign page cannot sign the user out with an image or link.</summary>
public class LogoutModel : PageModel
{
    private readonly AuthService _auth;
    public LogoutModel(AuthService auth) => _auth = auth;

    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        if (Request.GetSessionToken() is { } token)
            await _auth.InvalidateSession(token);
        Response.Cookies.Delete(AuthService.CookieName);
        return RedirectToPage("/Login");
    }
}
