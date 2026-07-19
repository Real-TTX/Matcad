using Matcad.Auth;
using Matcad.Config;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Authentications;

public class EditModel : PageModel
{
    private readonly ConfigStore _store;
    private readonly CaddyService _caddy;
    private readonly RouteProvider _routes;
    public EditModel(ConfigStore store, CaddyService caddy, RouteProvider routes)
    { _store = store; _caddy = caddy; _routes = routes; }

    [BindProperty(SupportsGet = true)] public long? Id { get; set; }
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public AuthType Type { get; set; } = AuthType.BasicAuth;
    [BindProperty] public List<string> Usernames { get; set; } = new();
    [BindProperty] public List<string> Passwords { get; set; } = new();

    // Matcad login-portal branding.
    [BindProperty] public string BrandTitle { get; set; } = "";
    [BindProperty] public string BrandColor { get; set; } = "";
    [BindProperty] public string BrandText { get; set; } = "";
    [BindProperty] public string BrandLogo { get; set; } = "";
    [BindProperty] public string BrandLogoLayout { get; set; } = "left";
    [BindProperty] public IFormFile? LogoFile { get; set; }

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
            BrandTitle = a.BrandTitle; BrandColor = a.BrandColor; BrandText = a.BrandText; BrandLogo = a.BrandLogo;
            BrandLogoLayout = string.IsNullOrWhiteSpace(a.BrandLogoLayout) ? "left" : a.BrandLogoLayout;
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            Error = "Name is required.";
            return Page();
        }

        var auth = _store.Authentications.FirstOrDefault(x => x.Id == Id) ?? new AuthenticationConfig();
        var previous = auth.Users.ToDictionary(u => u.Username, u => u.PasswordHash, StringComparer.OrdinalIgnoreCase);

        auth.Id = Id ?? 0;
        auth.Name = Name;
        auth.Type = Type;

        // Both Basic Auth and Matcad keep their login accounts in Users.
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

        if (Type == AuthType.Matcad)
        {
            auth.BrandTitle = BrandTitle?.Trim() ?? "";
            auth.BrandColor = BrandColor?.Trim() ?? "";
            auth.BrandText = BrandText?.Trim() ?? "";
            auth.BrandLogoLayout = BrandLogoLayout == "top" ? "top" : "left";
            if (LogoFile is { Length: > 0 })
            {
                if (LogoFile.Length > 256 * 1024)
                {
                    Error = "Logo is too large (max 256 KB).";
                    ExistingUsers = auth.Users;
                    return Page();
                }
                using var ms = new MemoryStream();
                await LogoFile.CopyToAsync(ms);
                var b64 = Convert.ToBase64String(ms.ToArray());
                auth.BrandLogo = $"data:{LogoFile.ContentType};base64,{b64}";
            }
            else
            {
                auth.BrandLogo = BrandLogo?.Trim() ?? ""; // URL, or keep existing (hidden field carries it)
            }
        }
        else
        {
            auth.BrandTitle = auth.BrandColor = auth.BrandText = auth.BrandLogo = "";
            auth.BrandLogoLayout = "left";
        }

        _store.UpsertAuthentication(auth, User.GetUserId());
        await ApplyAndFlash($"Authentication '{Name}' saved.");
        return RedirectToPage("Index");
    }

    public async Task<IActionResult> OnPostDeleteAsync()
    {
        if (Id is > 0)
        {
            if (_routes.All().Any(r => r.AuthenticationId == Id))
            {
                TempData["FlashError"] = "Authentication is used by at least one route and cannot be deleted.";
                return RedirectToPage("Edit", new { id = Id });
            }
            var name = _store.Authentications.FirstOrDefault(x => x.Id == Id)?.Name;
            _store.DeleteAuthentication(Id.Value);
            await ApplyAndFlash($"Authentication '{name}' deleted.");
        }
        return RedirectToPage("Index");
    }

    private async Task ApplyAndFlash(string success)
    {
        var (ok, error) = await _caddy.ApplyAsync();
        if (ok) TempData["Flash"] = success + " Caddy configuration updated.";
        else TempData["FlashError"] = success + $" But the Caddy push failed: {error}";
    }
}
