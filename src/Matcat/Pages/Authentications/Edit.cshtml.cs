using Matcat.Auth;
using Matcat.Config;
using Matcat.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcat.Pages.Authentications;

public class EditModel : PageModel
{
    private readonly ConfigStore _store;
    private readonly CaddyService _caddy;
    public EditModel(ConfigStore store, CaddyService caddy) { _store = store; _caddy = caddy; }

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public AuthType Type { get; set; } = AuthType.BasicAuth;
    [BindProperty] public List<string> Usernames { get; set; } = new();
    [BindProperty] public List<string> Passwords { get; set; } = new();

    public bool IsNew => Id is null or 0;
    public string? Error { get; private set; }
    public List<BasicAuthUser> ExistingUsers { get; private set; } = new();

    public IActionResult OnGet()
    {
        if (!IsNew)
        {
            var a = _store.Authentications.FirstOrDefault(x => x.Id == Id);
            if (a == null) return RedirectToPage("Index");
            Name = a.Name; Type = a.Type; ExistingUsers = a.Users;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = "Name ist erforderlich.";
            return Page();
        }

        var auth = _store.Authentications.FirstOrDefault(x => x.Id == Id) ?? new AuthenticationConfig();
        var previous = auth.Users.ToDictionary(u => u.Username, u => u.PasswordHash);

        auth.Id = Id ?? 0;
        auth.Name = Name;
        auth.Type = Type;

        if (Type == AuthType.BasicAuth)
        {
            var users = new List<BasicAuthUser>();
            for (var i = 0; i < Usernames.Count; i++)
            {
                var name = Usernames[i]?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                var pass = i < Passwords.Count ? Passwords[i] : null;
                string hash;
                if (!string.IsNullOrEmpty(pass))
                    hash = BCrypt.Net.BCrypt.HashPassword(pass);
                else if (previous.TryGetValue(name, out var old))
                    hash = old; // keep existing password when left blank
                else
                    continue; // new user without a password -> skip
                users.Add(new BasicAuthUser { Username = name, PasswordHash = hash });
            }
            auth.Users = users;
        }
        else
        {
            auth.Users = new();
        }

        _store.UpsertAuthentication(auth, User.GetUserId());
        await ApplyAndFlash($"Authentication „{Name}“ gespeichert.");
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id is > 0)
        {
            if (_store.Routes.Any(r => r.AuthenticationId == Id))
            {
                TempData["FlashError"] = "Authentication wird von mindestens einer Route verwendet und kann nicht gelöscht werden.";
                return RedirectToPage("Edit", new { id = Id });
            }
            var name = _store.Authentications.FirstOrDefault(x => x.Id == Id)?.Name;
            _store.DeleteAuthentication(Id.Value);
            await ApplyAndFlash($"Authentication „{name}“ gelöscht.");
        }
        return RedirectToPage("Index");
    }

    private async Task ApplyAndFlash(string success)
    {
        var (ok, error) = await _caddy.ApplyAsync();
        if (ok) TempData["Flash"] = success + " Caddy-Konfiguration aktualisiert.";
        else TempData["FlashError"] = success + $" Aber Caddy-Push fehlgeschlagen: {error}";
    }
}
