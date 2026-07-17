using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages;

public class LogoutModel : PageModel
{
    private readonly AuthService _auth;
    public LogoutModel(AuthService auth) => _auth = auth;

    public async Task<IActionResult> OnGetAsync()
    {
        if (Request.Cookies.TryGetValue(AuthService.CookieName, out var raw) &&
            Guid.TryParse(raw, out var token))
        {
            await _auth.InvalidateSession(token);
        }
        Response.Cookies.Delete(AuthService.CookieName);
        return RedirectToPage("/Login");
    }
}
