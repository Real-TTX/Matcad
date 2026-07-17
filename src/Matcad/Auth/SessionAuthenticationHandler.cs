using System.Security.Claims;
using System.Text.Encodings.Web;
using Matcad.Data;
using Matcad.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Matcad.Auth;

/// <summary>
/// Authenticates requests from the <c>matcad_session</c> cookie by validating
/// the session token against SQLite. This integrates with [Authorize] and the
/// role checks (Admin/User). Because sessions live in the database they remain
/// valid across container restarts.
/// </summary>
public class SessionAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "MatcadSession";

    private readonly AuthService _auth;

    public SessionAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AuthService auth) : base(options, logger, encoder)
    {
        _auth = auth;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(AuthService.CookieName, out var raw) ||
            !Guid.TryParse(raw, out var token))
        {
            return AuthenticateResult.NoResult();
        }

        var user = await _auth.GetUserBySession(token);
        if (user == null)
            return AuthenticateResult.NoResult();

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role.ToString())
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return AuthenticateResult.Success(ticket);
    }

    // Unauthenticated browser requests are redirected to the login page.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var returnUrl = properties.RedirectUri ?? Request.Path + Request.QueryString;
        Response.Redirect($"/Login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        return Task.CompletedTask;
    }
}

/// <summary>Convenience accessors for the current user's claims.</summary>
public static class ClaimsPrincipalExtensions
{
    public static long? GetUserId(this ClaimsPrincipal principal)
    {
        var raw = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(raw, out var id) ? id : null;
    }

    public static bool IsAdmin(this ClaimsPrincipal principal) =>
        principal.IsInRole(nameof(UserRole.Admin));
}
