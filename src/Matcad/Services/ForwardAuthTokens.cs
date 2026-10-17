using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;

namespace Matcad.Services;

/// <summary>A valid forward-auth cookie: who signed in, and which password they used (see <see cref="ForwardAuthTokens.Fingerprint"/>).</summary>
public record ForwardAuthTicket(string Username, string PasswordFingerprint);

/// <summary>
/// Issues and validates the stateless forward-auth cookie for the Matcad login
/// portal. The cookie payload (authId | expiry | password fingerprint | username) is protected
/// with Data Protection, so verification needs no database hit - this scales to very
/// high request volume on protected routes. One cookie per authentication, so
/// multiple protected domains don't clobber each other.
/// </summary>
public class ForwardAuthTokens
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly IDataProtector _protector;
    public ForwardAuthTokens(IDataProtectionProvider dp) =>
        _protector = dp.CreateProtector("Matcad.ForwardAuth.v3");

    public static string CookieName(long authId) => $"matcad_auth_{authId}";

    /// <summary>
    /// A short fingerprint of the stored password hash. It goes into the cookie, so changing a password ends the
    /// sessions that were opened with the old one even though the cookie itself is stateless.
    /// </summary>
    public static string Fingerprint(string passwordHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(passwordHash)))[..16];

    public string Issue(long authId, string username, string passwordHash)
    {
        // Username is free-form (may contain '|'), so it goes LAST and is never split.
        var exp = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
        return _protector.Protect($"{authId}|{exp}|{Fingerprint(passwordHash)}|{username}");
    }

    /// <summary>Returns the ticket if the token is genuine, unexpired and was issued for this authentication, else null.</summary>
    public ForwardAuthTicket? Validate(string token, long authId)
    {
        try
        {
            var parts = _protector.Unprotect(token).Split('|', 4);
            if (parts.Length != 4) return null;
            if (!long.TryParse(parts[0], out var a) || a != authId) return null;
            if (!long.TryParse(parts[1], out var exp)) return null;
            if (DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow) return null;
            return string.IsNullOrEmpty(parts[3]) ? null : new ForwardAuthTicket(parts[3], parts[2]);
        }
        catch (CryptographicException) { return null; }
    }
}
