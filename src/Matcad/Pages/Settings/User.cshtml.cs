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
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public UserRole Role { get; set; } = UserRole.User;

    public bool IsNew => Id is null or 0;
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync()
    {
        if (!IsNew)
        {
            var u = await _auth.GetUser(Id!.Value);
            if (u == null) return RedirectToPage("Index");
            Username = u.Username;
            Role = u.Role;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        Username = Username.Trim();
        if (string.IsNullOrEmpty(Username))
        {
            Error = "Username is required.";
            return Page();
        }

        if (IsNew)
        {
            if (string.IsNullOrEmpty(Password))
            {
                Error = "A password is required for a new user.";
                return Page();
            }
            if (await _auth.FindUser(Username) != null)
            {
                Error = "That username is already taken.";
                return Page();
            }
            await _auth.CreateUser(Username, Password, Role, User.GetUserId());
        }
        else
        {
            var u = await _auth.GetUser(Id!.Value);
            if (u == null) return RedirectToPage("Index");
            // Guard: keep at least one admin.
            if (u.Role == UserRole.Admin && Role != UserRole.Admin && await IsLastAdmin(u.Id))
            {
                Error = "The last admin cannot be demoted.";
                return Page();
            }
            u.Username = Username;
            u.Role = Role;
            await _auth.UpdateUser(u, Password, User.GetUserId());
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
                if (u.Role == UserRole.Admin && await IsLastAdmin(u.Id))
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

    private async Task<bool> IsLastAdmin(long userId)
    {
        var users = await _auth.ListUsers();
        return users.Count(x => x.Role == UserRole.Admin) <= 1;
    }
}
