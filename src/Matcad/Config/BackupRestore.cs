using Matcad.Data;
using Matcad.Services;

namespace Matcad.Config;

/// <summary>A user as it is written back from a backup. <c>ExternalSubject</c> is the link with MatIdentity
/// (a user that MatIdentity created has no password: it signs in there).</summary>
public record RestoredUser(string Username, string? Email, string PasswordHash, UserRole Role, bool MustChangePassword,
    string? ExternalSubject = null);

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
            var subject = string.IsNullOrWhiteSpace(u.ExternalSubject) ? null : u.ExternalSubject.Trim();
            // A user that MatIdentity created has no password; every other user needs one.
            var noPassword = string.IsNullOrEmpty(u.PasswordHash) && subject != null;
            if (string.IsNullOrWhiteSpace(u.Username) || (string.IsNullOrWhiteSpace(u.PasswordHash) && !noPassword))
                return (users, "The backup contains a user without a name or password.");
            if (!noPassword && !PasswordHashing.IsBcrypt(u.PasswordHash))
                return (users, $"The password of '{u.Username}' is not a bcrypt hash; restoring it would lock that user out.");

            // A password that is still the default has to be changed, whatever the backup says.
            var mustChange = u.MustChangePassword || PasswordHashing.Verify("admin", u.PasswordHash);
            users.Add(new RestoredUser(u.Username.Trim(), AuthService.NormalizeEmail(u.Email), noPassword ? "" : u.PasswordHash,
                Enum.TryParse<UserRole>(u.Role, out var role) ? role : UserRole.User, mustChange, subject));
        }

        if (users.Count == 0) return (users, null);

        // Username and e-mail share one namespace (both sign in). Per user they may be the same text.
        var logins = users.SelectMany(u => new[] { u.Username, u.Email }
            .Where(l => !string.IsNullOrEmpty(l)).Distinct(StringComparer.OrdinalIgnoreCase)).ToList();
        if (logins.Count != logins.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            return (users, "The backup contains user names or e-mail addresses that are used twice.");
        var subjects = users.Where(u => u.ExternalSubject != null).Select(u => u.ExternalSubject).ToList();
        if (subjects.Count != subjects.Distinct(StringComparer.Ordinal).Count())
            return (users, "The backup links two users with the same MatIdentity account.");
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
        var unknownHost = settings.UnknownHost?.Trim().ToLowerInvariant() ?? "";
        settings.UnknownHost = MatcadSettings.UnknownHostModes.Contains(unknownHost) ? unknownHost : "";
        var unknownRedirect = settings.UnknownHostRedirect?.Trim() ?? "";
        settings.UnknownHostRedirect = RouteEditor.CheckRedirectTarget(unknownRedirect) == null ? unknownRedirect : "";
    }

    private static string ValidHostOrEmpty(string? value)
    {
        var host = value?.Trim().ToLowerInvariant() ?? "";
        return RouteEditor.IsHostName(host) ? host : "";
    }
}
