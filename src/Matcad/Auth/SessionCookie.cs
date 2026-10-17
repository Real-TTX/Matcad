using Matcad.Data;
using Matcad.Services;

namespace Matcad.Auth;

/// <summary>The cookie of a Matcad session (<see cref="AuthService.CookieName"/>), written by the local sign-in and by the sign-in through MatIdentity.
/// The request side is <see cref="ClaimsPrincipalExtensions.GetSessionToken"/>.</summary>
public static class SessionCookie
{
    public static void Append(HttpContext http, UserSession session) =>
        http.Response.Cookies.Append(AuthService.CookieName, session.Token.ToString(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            // Behind Caddy the connection to Matcad is plain HTTP: X-Forwarded-Proto decides.
            Secure = http.Request.IsHttpsClient(),
            Expires = session.ExpiryDate,
            Path = "/"
        });
}
