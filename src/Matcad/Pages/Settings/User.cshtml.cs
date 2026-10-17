using Matcad.Auth;
using Matcad.Data;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Settings;

public class UserModel : PageModel
{
    private readonly AuthService _auth;
    public UserModel(AuthService auth) => _auth = auth;

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public string Username { get; set; } = "";
    [BindProperty] public string? Email { get; set; }
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public UserRole Role { get; set; } = UserRole.User;
    /// <summary>An administrator chooses the password, so the user picks their own at the first sign-in.</summary>
    [BindProperty] public bool MustChangePassword { get; set; } = true;

    public bool IsNew => Id is null or 0;
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!IsNew)
        {
            var u = await _auth.GetUser(Id!.Value);
            if (u == null) return RedirectToPage("Index");
            Username = u.Username;
            Email = u.Email;
            Role = u.Role;
            MustChangePassword = u.MustChangePassword;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Username = (Username ?? "").Trim();
        var email = AuthService.NormalizeEmail(Email);
        var user = IsNew ? null : await _auth.GetUser(Id!.Value);
        if (!IsNew && user == null) return RedirectToPage("Index");

        if (!string.IsNullOrEmpty(Password) || IsNew)
            Error = AuthService.ValidatePassword(Password);
        Error ??= await _auth.ValidateIdentity(Username, email, user?.Id);
        // Keep at least one admin.
        if (Error == null && user is { Role: UserRole.Admin } && Role != UserRole.Admin && await IsLastAdmin())
            Error = "The last admin cannot be demoted.";
        if (Error != null) return Page();

        if (user == null)
        {
            await _auth.CreateUser(Username, email, Password!, Role, User.GetUserId(), MustChangePassword);
        }
        else
        {
            user.Username = Username;
            user.Email = email;
            user.Role = Role;
            await _auth.UpdateUser(user, Password, MustChangePassword, User.GetUserId(),
                keepSession: user.Id == User.GetUserId() ? Request.GetSessionToken() : null);
        }

        TempData["Flash"] = $"User '{Username}' saved.";
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id is > 0)
        {
            var u = await _auth.GetUser(Id.Value);
            if (u != null)
            {
                if (u.Role == UserRole.Admin && await IsLastAdmin())
                {
                    TempData["FlashError"] = "The last admin cannot be deleted.";
                    return RedirectToPage("User", new { id = Id });
                }
                await _auth.DeleteUser(Id.Value);
                TempData["Flash"] = $"User '{u.Username}' deleted.";
            }
        }
        return RedirectToPage("Index");
    }

    private async Task<bool> IsLastAdmin() => await _auth.CountAdmins() <= 1;
}
