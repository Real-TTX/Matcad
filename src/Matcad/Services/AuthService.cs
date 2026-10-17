using System.Collections.Concurrent;
using Matcad.Config;
using Matcad.Data;
using Microsoft.EntityFrameworkCore;

namespace Matcad.Services;

/// <summary>
/// Local user management, password hashing, and session handling.
/// Sessions are stored in SQLite (survive container restarts); the cookie only
/// carries the session <see cref="UserSession.Token"/>.
/// </summary>
public class AuthService
{
    public const string CookieName = "matcad_session";
    public const int MinPasswordLength = 8;
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    // Short-lived validated-session cache so high-traffic forward-auth checks
    // don't hit the database on every single proxied request. Entries are
    // evicted on logout / user deletion, so the effective staleness is only the
    // TTL for out-of-band invalidation (expiry is measured in days).
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private const int CacheSoftLimit = 1000;
    private static readonly ConcurrentDictionary<Guid, (User User, DateTime At)> SessionCache = new();

    // Verified when the login does not exist, so the response time does not reveal which logins exist.
    private static readonly Lazy<string> TimingEqualizerHash = new(() => BCrypt.Net.BCrypt.HashPassword("matcad-timing-equalizer"));

    private readonly MatcadDbContext _db;

    public AuthService(MatcadDbContext db) => _db = db;

    // --- Users --------------------------------------------------------------

    /// <summary>Finds a user by username or e-mail address (case-insensitive).</summary>
    public Task<User?> FindByLogin(string login)
    {
        login = login.Trim();
        return _db.Users.FirstOrDefaultAsync(u => u.Username == login || u.Email == login);
    }

    public Task<User?> GetUser(long id) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id);

    public Task<List<User>> ListUsers() =>
        _db.Users.OrderBy(u => u.Username).ToListAsync();

    public Task<int> CountAdmins() =>
        _db.Users.CountAsync(u => u.Role == UserRole.Admin);

    /// <summary>Checks username, e-mail and uniqueness. Username and e-mail share one namespace because both can be used to sign in.</summary>
    public async Task<string?> ValidateIdentity(string username, string? email, long? exceptUserId)
    {
        if (string.IsNullOrWhiteSpace(username)) return "Username is required.";
        if (username.Length > 128) return "The username is too long.";
        if (email is not null)
        {
            if (email.Length > 256 || !email.Contains('@') || email.Any(char.IsWhiteSpace))
                return "Please enter a valid e-mail address.";
        }

        var taken = await _db.Users.AnyAsync(u => u.Id != exceptUserId &&
            (u.Username == username || u.Email == username ||
             (email != null && (u.Username == email || u.Email == email))));
        return taken ? "That username or e-mail address is already in use." : null;
    }

    public static string? ValidatePassword(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinPasswordLength
            ? $"The password must be at least {MinPasswordLength} characters."
            : password.Equals("admin", StringComparison.OrdinalIgnoreCase)
                ? "Please choose a password other than the default."
                : null;

    /// <summary>Checks the fields of a "change my password" form.</summary>
    public static string? ValidatePasswordChange(User user, bool requireCurrent, string? current, string? newPassword, string? confirm)
    {
        if (requireCurrent && !PasswordHashing.Verify(current ?? "", user.PasswordHash))
            return "The current password is not correct.";
        if (ValidatePassword(newPassword) is { } error) return error;
        return newPassword == confirm ? null : "The passwords do not match.";
    }

    public static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim();

    public async Task<User> CreateUser(string username, string? email, string password, UserRole role,
        long? actorId, bool mustChangePassword = false)
    {
        var user = new User
        {
            Username = username.Trim(),
            Email = NormalizeEmail(email),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = role,
            MustChangePassword = mustChangePassword,
            CreateDate = DateTime.UtcNow,
            CreateUserId = actorId
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    /// <summary>
    /// Saves a tracked user whose username, e-mail or role the caller has changed. A new password signs the user
    /// out everywhere else (<paramref name="keepSession"/> is the session that stays).
    /// <paramref name="mustChangePassword"/> null leaves the flag as it is.
    /// </summary>
    public async Task UpdateUser(User user, string? newPassword, bool? mustChangePassword, long? actorId, Guid? keepSession = null)
    {
        if (!string.IsNullOrEmpty(newPassword))
        {
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
            var keep = keepSession ?? Guid.Empty;
            await _db.UserSessions.Where(s => s.UserId == user.Id && s.Token != keep).ExecuteDeleteAsync();
        }
        if (mustChangePassword is { } flag) user.MustChangePassword = flag;
        user.UpdateDate = DateTime.UtcNow;
        user.UpdateUserId = actorId;
        await _db.SaveChangesAsync();
        SessionCache.Clear();
    }

    public async Task DeleteUser(long id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return;
        _db.Users.Remove(user); // the sessions go with it (cascade)
        await _db.SaveChangesAsync();
        SessionCache.Clear(); // drop any cached sessions of the removed user
    }

    /// <summary>Replaces all users (backup restore). Password hashes are taken as-is.</summary>
    public async Task ReplaceAllUsers(IEnumerable<RestoredUser> users, long? actorId)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        await _db.UserSessions.ExecuteDeleteAsync();
        await _db.Users.ExecuteDeleteAsync();
        foreach (var u in users)
            _db.Users.Add(new User
            {
                Username = u.Username, Email = NormalizeEmail(u.Email), PasswordHash = u.PasswordHash, Role = u.Role,
                MustChangePassword = u.MustChangePassword, CreateDate = DateTime.UtcNow, CreateUserId = actorId
            });
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        SessionCache.Clear();
    }

    /// <summary>Returns the user if credentials are valid, otherwise null.</summary>
    public async Task<User?> ValidateCredentials(string login, string password)
    {
        var user = await FindByLogin(login);
        if (user == null)
        {
            PasswordHashing.Verify(password, TimingEqualizerHash.Value);
            return null;
        }
        return PasswordHashing.Verify(password, user.PasswordHash) ? user : null;
    }

    // --- Sessions -----------------------------------------------------------

    public async Task<UserSession> CreateSession(User user)
    {
        var now = DateTime.UtcNow;
        // Expired sessions are only noticed when their cookie shows up again; sweep them on every sign-in.
        await _db.UserSessions.Where(s => s.ExpiryDate < now).ExecuteDeleteAsync();

        var session = new UserSession
        {
            Token = Guid.NewGuid(),
            UserId = user.Id,
            ExpiryDate = now.Add(SessionLifetime),
            CreateDate = now,
            CreateUserId = user.Id
        };
        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync();
        return session;
    }

    /// <summary>Resolves the user for a valid, unexpired session token.
    /// Backed by a short-lived in-memory cache to stay cheap under load.</summary>
    public async Task<User?> GetUserBySession(Guid token)
    {
        if (SessionCache.TryGetValue(token, out var c) && DateTime.UtcNow - c.At < CacheTtl)
            return c.User;

        var session = await _db.UserSessions
            .AsNoTracking()
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Token == token);

        if (session == null) { SessionCache.TryRemove(token, out _); return null; }
        if (session.ExpiryDate < DateTime.UtcNow)
        {
            SessionCache.TryRemove(token, out _);
            await _db.UserSessions.Where(s => s.Token == token).ExecuteDeleteAsync();
            return null;
        }

        if (session.User != null)
        {
            if (SessionCache.Count > CacheSoftLimit) PruneCache();
            SessionCache[token] = (session.User, DateTime.UtcNow);
        }
        return session.User;
    }

    public async Task InvalidateSession(Guid token)
    {
        SessionCache.TryRemove(token, out _);
        await _db.UserSessions.Where(s => s.Token == token).ExecuteDeleteAsync();
    }

    private static void PruneCache()
    {
        var cutoff = DateTime.UtcNow - CacheTtl;
        foreach (var (token, entry) in SessionCache)
            if (entry.At < cutoff) SessionCache.TryRemove(token, out _);
    }

    /// <summary>Creates the default admin on first start if no users exist. With <paramref name="adminPassword"/>
    /// (env <c>MATCAD_ADMIN_PASSWORD</c>, a headless install such as Mattiverse's) that is acceptable, the admin gets that
    /// password without the forced change; otherwise admin/admin, which has to be changed at the first sign-in.
    /// Returns true only when the admin was seeded with the given password, so the caller can skip the first-run
    /// wizard whose first step only exists to replace admin/admin.</summary>
    public async Task<bool> EnsureSeedAdmin(ILogger logger, string? adminPassword = null)
    {
        if (await _db.Users.AnyAsync()) return false;
        if (!string.IsNullOrEmpty(adminPassword))
        {
            // The rules of every other password (wizard, profile, users page), and never the well-known default.
            if (ValidatePassword(adminPassword) != null || adminPassword == "admin")
                logger.LogWarning("MATCAD_ADMIN_PASSWORD is ignored: it must be at least {MinLength} characters and not 'admin'. " +
                    "Falling back to the default admin login.", MinPasswordLength);
            else
            {
                await CreateUser("admin", null, adminPassword, UserRole.Admin, actorId: null, mustChangePassword: false);
                logger.LogInformation("Seeded admin user 'admin' with the password from MATCAD_ADMIN_PASSWORD.");
                return true;
            }
        }
        await CreateUser("admin", null, "admin", UserRole.Admin, actorId: null, mustChangePassword: true);
        logger.LogWarning("Seeded default admin user 'admin' with password 'admin'. It has to be changed at the first sign-in.");
        return false;
    }
}
