using Matcad.Data;
using Matcad.Services;

namespace Matcad.Config;

/// <summary>A user as it is written back from a backup.</summary>
public record RestoredUser(string Username, string? Email, string PasswordHash, UserRole Role, bool MustChangePassword);

/// <summary>Checks what a backup file brings along before anything on this system is replaced.</summary>
public static class BackupRestore
{
    /// <summary>
    /// Turns the users of a backup into accounts, or says what is wrong with them: a backup must not lock everyone out
    /// (no administrator, hashes that cannot be verified) or break the unique logins.
    /// </summary>
    public static (List<RestoredUser> Users, string? Error) CheckUsers(IEnumerable<BackupUser> backupUsers)
    {
        var users = new List<RestoredUser>();
        foreach (var u in backupUsers)
        {
            if (string.IsNullOrWhiteSpace(u.Username) || string.IsNullOrWhiteSpace(u.PasswordHash))
                return (users, "The backup contains a user without a name or password.");
            if (!PasswordHashing.IsBcrypt(u.PasswordHash))
                return (users, $"The password of '{u.Username}' is not a bcrypt hash; restoring it would lock that user out.");

            // A password that is still the default has to be changed, whatever the backup says.
            var mustChange = u.MustChangePassword || PasswordHashing.Verify("admin", u.PasswordHash);
            users.Add(new RestoredUser(u.Username.Trim(), AuthService.NormalizeEmail(u.Email), u.PasswordHash,
                Enum.TryParse<UserRole>(u.Role, out var role) ? role : UserRole.User, mustChange));
        }

        if (users.Count == 0) return (users, null);

        // Username and e-mail share one namespace (both sign in). Per user they may be the same text.
        var logins = users.SelectMany(u => new[] { u.Username, u.Email }
            .Where(l => !string.IsNullOrEmpty(l)).Distinct(StringComparer.OrdinalIgnoreCase)).ToList();
        if (logins.Count != logins.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            return (users, "The backup contains user names or e-mail addresses that are used twice.");
        if (users.All(u => u.Role != UserRole.Admin))
            return (users, "The backup contains no administrator. Restoring it would lock everyone out.");
        return (users, null);
    }

    /// <summary>Settings from a file may hold values the UI would never save; fall back to safe ones.</summary>
    public static void SanitizeSettings(MatcadSettings settings)
    {
        settings.BaseDomain = ValidHostOrEmpty(settings.BaseDomain);
        settings.MatcadHost = ValidHostOrEmpty(settings.MatcadHost);
        if (settings.PortalMode?.Trim().ToLowerInvariant() is not ("inline" or "redirect" or "unauthorized"))
            settings.PortalMode = "inline";
        else
            settings.PortalMode = settings.PortalMode.Trim().ToLowerInvariant();
    }

    private static string ValidHostOrEmpty(string? value)
    {
        var host = value?.Trim().ToLowerInvariant() ?? "";
        return RouteEditor.IsHostName(host) ? host : "";
    }
}
