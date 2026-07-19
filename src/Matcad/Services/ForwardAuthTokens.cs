using Microsoft.AspNetCore.DataProtection;

namespace Matcad.Services;

/// <summary>
/// Issues and validates the stateless forward-auth cookie for the Matcad login
/// portal. The cookie payload (authId | username | expiry) is protected with
/// Data Protection, so verification needs no database hit - this scales to very
/// high request volume on protected routes. One cookie per authentication, so
/// multiple protected domains don't clobber each other.
/// </summary>
public class ForwardAuthTokens
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    private readonly IDataProtector _protector;
    public ForwardAuthTokens(IDataProtectionProvider dp) =>
        _protector = dp.CreateProtector("Matcad.ForwardAuth.v1");

    public static string CookieName(long authId) => $"matcad_auth_{authId}";

    public string Issue(long authId, string username)
    {
        var exp = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
        return _protector.Protect($"{authId}|{username}|{exp}");
    }

    /// <summary>Returns the username if the token is valid for this authentication, else null.</summary>
    public string? Validate(string token, long authId)
    {
        try
        {
            var parts = _protector.Unprotect(token).Split('|');
            if (parts.Length != 3) return null;
            if (!long.TryParse(parts[0], out var a) || a != authId) return null;
            if (!long.TryParse(parts[2], out var exp)) return null;
            if (DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow) return null;
            return string.IsNullOrEmpty(parts[1]) ? null : parts[1];
        }
        catch { return null; }
    }
}
