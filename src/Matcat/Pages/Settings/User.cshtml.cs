using Matcat.Auth;
using Matcat.Data;
using Matcat.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcat.Pages.Settings;

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
            Error = "Benutzername ist erforderlich.";
            return Page();
        }

        if (IsNew)
        {
            if (string.IsNullOrEmpty(Password))
            {
                Error = "Für einen neuen Benutzer ist ein Passwort erforderlich.";
                return Page();
            }
            if (await _auth.FindUser(Username) != null)
            {
                Error = "Benutzername ist bereits vergeben.";
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
                Error = "Der letzte Admin kann nicht herabgestuft werden.";
                return Page();
            }
            u.Username = Username;
            u.Role = Role;
            await _auth.UpdateUser(u, Password, User.GetUserId());
        }

        TempData["Flash"] = $"Benutzer „{Username}“ gespeichert.";
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
                    TempData["FlashError"] = "Der letzte Admin kann nicht gelöscht werden.";
                    return RedirectToPage("User", new { id = Id });
                }
                await _auth.DeleteUser(Id.Value);
                TempData["Flash"] = $"Benutzer „{u.Username}“ gelöscht.";
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
