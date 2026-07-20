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
        _protector = dp.CreateProtector("Matcad.ForwardAuth.v2");

    public static string CookieName(long authId) => $"matcad_auth_{authId}";

    public string Issue(long authId, string username)
    {
        // Username is free-form (may contain '|'), so it goes LAST and is never split.
        var exp = DateTimeOffset.UtcNow.Add(Lifetime).ToUnixTimeSeconds();
        return _protector.Protect($"{authId}|{exp}|{username}");
    }

    /// <summary>Returns the username if the token is valid for this authentication, else null.</summary>
    public string? Validate(string token, long authId)
    {
        try
        {
            var parts = _protector.Unprotect(token).Split('|', 3);
            if (parts.Length != 3) return null;
            if (!long.TryParse(parts[0], out var a) || a != authId) return null;
            if (!long.TryParse(parts[1], out var exp)) return null;
            if (DateTimeOffset.FromUnixTimeSeconds(exp) < DateTimeOffset.UtcNow) return null;
            return string.IsNullOrEmpty(parts[2]) ? null : parts[2];
        }
        catch { return null; }
    }
}
