using System.Text.RegularExpressions;
using Matcad.Auth;
using Matcad.Config;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Authentications;

public partial class EditModel : PageModel
{
    private const int MaxLogoBytes = 256 * 1024;
    private static readonly string[] LogoTypes = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/svg+xml", "image/x-icon", "image/vnd.microsoft.icon"];

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
        var existing = IsNew ? null : _store.Authentications.FirstOrDefault(x => x.Id == Id);
        if (!IsNew && existing == null) return RedirectToPage("Index");

        if (string.IsNullOrWhiteSpace(Name)) return Fail("Name is required.");

        // Both Basic Auth and Matcad keep their login accounts in Users. A blank password keeps the existing one.
        var previous = (existing?.Users ?? new()).ToDictionary(u => u.Username, u => u.PasswordHash, StringComparer.OrdinalIgnoreCase);
        var users = new List<BasicAuthUser>();
        for (var i = 0; i < Usernames.Count; i++)
        {
            var name = Usernames[i]?.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (users.Any(u => u.Username.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return Fail($"The username '{name}' is listed twice.");

            var password = i < Passwords.Count ? Passwords[i] : null;
            string hash;
            if (!string.IsNullOrEmpty(password)) hash = PasswordHashing.HashOrKeep(password, previous.GetValueOrDefault(name));
            else if (previous.TryGetValue(name, out var old)) hash = old;
            else continue; // a new user without a password is skipped
            users.Add(new BasicAuthUser { Username = name, PasswordHash = hash });
        }

        // Work on a copy: a rejected form must not leave half-applied changes in the stored authentication.
        var auth = new AuthenticationConfig { Id = existing?.Id ?? 0, Name = Name.Trim(), Type = Type, Users = users, ManagedBy = existing?.ManagedBy };
        if (Type == AuthType.Matcad)
        {
            var brandError = await ApplyBranding(auth);
            if (brandError != null) return Fail(brandError);
        }

        _store.UpsertAuthentication(auth, User.GetUserId());
        await ApplyAndFlash($"Authentication '{auth.Name}' saved.");
        return RedirectToPage("Index");
    }

    private async Task<string?> ApplyBranding(AuthenticationConfig auth)
    {
        var color = BrandColor?.Trim() ?? "";
        if (color.Length > 0 && !HexColor().IsMatch(color)) return "The accent colour must look like #2f6feb.";

        auth.BrandTitle = BrandTitle?.Trim() ?? "";
        auth.BrandText = BrandText?.Trim() ?? "";
        auth.BrandColor = color;
        auth.BrandLogoLayout = BrandLogoLayout == "top" ? "top" : "left";

        if (LogoFile is not { Length: > 0 })
        {
            var logo = BrandLogo?.Trim() ?? ""; // a URL, or the kept data URI (a hidden field carries it)
            if (!IsLogoAddress(logo)) return "The logo must be an http(s) address, a path on this site or an uploaded image.";
            auth.BrandLogo = logo;
            return null;
        }
        if (LogoFile.Length > MaxLogoBytes) return "Logo is too large (max 256 KB).";
        if (!LogoTypes.Contains(LogoFile.ContentType, StringComparer.OrdinalIgnoreCase)) return "The logo must be a PNG, JPEG, GIF, WebP, SVG or ICO image.";

        using var memory = new MemoryStream();
        await LogoFile.CopyToAsync(memory);
        auth.BrandLogo = $"data:{LogoFile.ContentType};base64,{Convert.ToBase64String(memory.ToArray())}";
        return null;
    }

    /// <summary>Shows the form again with the rows the user had (passwords have to be typed again).</summary>
    private IActionResult Fail(string message)
    {
        Error = message;
        ExistingUsers = Usernames.Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => new BasicAuthUser { Username = u.Trim() }).ToList();
        return Page();
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

    /// <summary>What the login page may put into the src of its logo: an http(s) address, a path, or an image this page embedded.</summary>
    public static bool IsLogoAddress(string value)
    {
        if (value.Length == 0) return true;
        if (value.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return false;
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return LogoTypes.Any(t => value.StartsWith($"data:{t};base64,", StringComparison.OrdinalIgnoreCase));
        if (value.StartsWith('/')) return !value.StartsWith("//") && !value.StartsWith("/\\");
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    }

    [GeneratedRegex("^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6})$")]
    private static partial Regex HexColor();
}
