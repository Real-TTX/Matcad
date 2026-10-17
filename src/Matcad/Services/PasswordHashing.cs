using System.Text.RegularExpressions;

namespace Matcad.Services;

/// <summary>bcrypt helpers shared by the accounts, the login portal, the API and the backup restore.</summary>
public static partial class PasswordHashing
{
    /// <summary>The shape of a bcrypt hash ($2a$, $2b$, $2x$ or $2y$, cost, 53 characters of salt and hash).</summary>
    public static bool IsBcrypt(string? hash) => hash != null && BcryptShape().IsMatch(hash);

    /// <summary>Verifies a password; a stored value that is not a bcrypt hash simply does not match (instead of throwing).</summary>
    public static bool Verify(string password, string? hash)
    {
        if (!IsBcrypt(hash)) return false;
        try { return BCrypt.Net.BCrypt.Verify(password, hash); }
        catch (BCrypt.Net.SaltParseException) { return false; }
    }

    /// <summary>
    /// Hashes a password, or keeps <paramref name="currentHash"/> when it already matches. A fresh hash has a fresh salt, so
    /// re-saving an unchanged password (an API client that re-posts its user list) must not change the stored value:
    /// the sign-ins that were opened with it would end.
    /// </summary>
    public static string HashOrKeep(string password, string? currentHash) =>
        Verify(password, currentHash) ? currentHash! : BCrypt.Net.BCrypt.HashPassword(password);

    [GeneratedRegex(@"^\$2[abxy]\$\d\d\$[./A-Za-z0-9]{53}$")]
    private static partial Regex BcryptShape();
}
