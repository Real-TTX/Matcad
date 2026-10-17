using Matcad.Data;
using Matcad.Services;

namespace Matcad.Auth;

/// <summary>The cookie of a Matcad session (<see cref="AuthService.CookieName"/>), written by the local sign-in and by the sign-in through MatIdentity.</summary>
public static class SessionCookie
{
    public static void Append(HttpContext http, UserSession session)
    {
        // Behind Caddy the connection to Matcad is plain HTTP; trust X-Forwarded-Proto
        // so the session cookie is still Secure for the real HTTPS client.
        var https = http.Request.IsHttps ||
            string.Equals(http.Request.Headers["X-Forwarded-Proto"].FirstOrDefault(), "https", StringComparison.OrdinalIgnoreCase);
        http.Response.Cookies.Append(AuthService.CookieName, session.Token.ToString(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = https,
            Expires = session.ExpiryDate,
            Path = "/"
        });
    }

    /// <summary>The session token of the request, or null.</summary>
    public static Guid? Token(HttpContext http) =>
        http.Request.Cookies.TryGetValue(AuthService.CookieName, out var raw) && Guid.TryParse(raw, out var token) ? token : null;
}
