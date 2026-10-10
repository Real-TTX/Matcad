using Matcad.Auth;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Account;

/// <summary>The signed-in user's own account: username, e-mail address and password.</summary>
public class ProfileModel : PageModel
{
    private readonly AuthService _auth;
    public ProfileModel(AuthService auth) => _auth = auth;

    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? CurrentPassword { get; set; }
    [BindProperty] public string? NewPassword { get; set; }
    [BindProperty] public string? ConfirmPassword { get; set; }

    public bool MustChange => User.MustChangePassword();
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var user = await CurrentUser();
        if (user == null) return RedirectToPage("/Login");
        Username = user.Username;
        Email = user.Email;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var user = await CurrentUser();
        if (user == null) return RedirectToPage("/Login");

        var username = (Username ?? "").Trim();
        var email = AuthService.NormalizeEmail(Email);
        var changePassword = !string.IsNullOrEmpty(NewPassword);

        // Whoever has to pick a new password has just signed in with the old one; everyone else proves it.
        if (MustChange && !changePassword)
            Error = "Please choose a new password to go on.";
        else if (changePassword)
            Error = AuthService.ValidatePasswordChange(user, requireCurrent: !MustChange, CurrentPassword, NewPassword, ConfirmPassword);
        Error ??= await _auth.ValidateIdentity(username, email, user.Id);
        if (Error != null) return Page();

        user.Username = username;
        user.Email = email;
        await _auth.UpdateUser(user, changePassword ? NewPassword : null, mustChangePassword: false, user.Id, keepSession: Request.GetSessionToken());

        TempData["Flash"] = "Account saved.";
        // The session cookie stays; its claims are rebuilt on the next request (a cleared must-change flag lets the user on).
        return RedirectToPage("/Index");
    }

    private Task<Data.User?> CurrentUser() =>
        User.GetUserId() is { } id ? _auth.GetUser(id) : Task.FromResult<Data.User?>(null);
}
