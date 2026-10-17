using System.Collections.Concurrent;
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
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    // Short-lived validated-session cache so high-traffic forward-auth checks
    // don't hit the database on every single proxied request. Entries are
    // evicted on logout / user deletion, so the effective staleness is only the
    // TTL for out-of-band invalidation (expiry is measured in days).
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(10);
    private static readonly ConcurrentDictionary<Guid, (User User, DateTime At)> SessionCache = new();

    private readonly MatcadDbContext _db;
    private readonly ILogger<AuthService> _log;

    public AuthService(MatcadDbContext db, ILogger<AuthService> log)
    {
        _db = db;
        _log = log;
    }

    // --- Users --------------------------------------------------------------

    public Task<User?> FindUser(string username) =>
        _db.Users.FirstOrDefaultAsync(u => u.Username == username);

    public Task<User?> GetUser(long id) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id);

    public Task<List<User>> ListUsers() =>
        _db.Users.OrderBy(u => u.Username).ToListAsync();

    public async Task<User> CreateUser(string username, string password, UserRole role, long? actorId)
    {
        var user = new User
        {
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = role,
            CreateDate = DateTime.UtcNow,
            CreateUserId = actorId
        };
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        return user;
    }

    public async Task UpdateUser(User user, string? newPassword, long? actorId)
    {
        if (!string.IsNullOrEmpty(newPassword))
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.UpdateDate = DateTime.UtcNow;
        user.UpdateUserId = actorId;
        await _db.SaveChangesAsync();
    }

    /// <summary>Updates username and/or password of an existing (tracked) user.</summary>
    public async Task UpdateAccount(User user, string? newUsername, string? newPassword, long? actorId)
    {
        if (!string.IsNullOrWhiteSpace(newUsername)) user.Username = newUsername.Trim();
        if (!string.IsNullOrEmpty(newPassword)) user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword);
        user.UpdateDate = DateTime.UtcNow;
        user.UpdateUserId = actorId;
        await _db.SaveChangesAsync();
        SessionCache.Clear();
    }

    public async Task DeleteUser(long id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return;
        _db.UserSessions.RemoveRange(_db.UserSessions.Where(s => s.UserId == id));
        _db.Users.Remove(user);
        await _db.SaveChangesAsync();
        SessionCache.Clear(); // drop any cached sessions of the removed user
    }

    /// <summary>Replaces all users (backup restore). Password hashes are taken as-is.</summary>
    public async Task ReplaceAllUsers(IEnumerable<(string Username, string PasswordHash, UserRole Role)> users, long? actorId)
    {
        _db.UserSessions.RemoveRange(_db.UserSessions);
        _db.Users.RemoveRange(_db.Users);
        await _db.SaveChangesAsync();
        foreach (var u in users)
            _db.Users.Add(new User
            {
                Username = u.Username, PasswordHash = u.PasswordHash, Role = u.Role,
                CreateDate = DateTime.UtcNow, CreateUserId = actorId
            });
        await _db.SaveChangesAsync();
        SessionCache.Clear();
    }

    /// <summary>Returns the user if credentials are valid, otherwise null.</summary>
    public async Task<User?> ValidateCredentials(string username, string password)
    {
        var user = await FindUser(username);
        // A user created by MatIdentity has no password: it cannot sign in locally until an admin gives it one.
        if (user == null || string.IsNullOrEmpty(user.PasswordHash)) return null;
        return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash) ? user : null;
    }

    // --- Sign-in through MatIdentity ------------------------------------------

    /// <summary>The roles MatIdentity gives in the claim <c>role</c> (the <c>roles</c> of Matcad's MatApiDefinition).</summary>
    public const string IdentityAdminRole = "admin";
    public const string IdentityUserRole = "user";

    /// <summary>
    /// The local user for a person MatIdentity signed in: (1) the user with its <c>sub</c>; (2) else, when exactly one unlinked account with a
    /// password has the person's e-mail address as its user name (without a match: the preferred user name), it is NOT linked - a name proves
    /// nothing, the person signs in once with that account's password (<see cref="LinkExternal"/>) - the answer names it in
    /// <see cref="ExternalSignIn.LinkUserId"/>; (3) else a new user without a password. Refused: several matches, a match linked to another subject.
    /// The role follows the claim when MatIdentity decides it (never demoting the last admin).
    /// </summary>
    public async Task<ExternalSignIn> SignInExternal(ExternalIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Subject))
            return ExternalSignIn.Refused("MatIdentity sent no subject (sub).");

        var subject = identity.Subject.Trim();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.ExternalSubject == subject);
        if (user != null)
        {
            await ApplyExternalRole(user, identity.Role, created: false);
            await _db.SaveChangesAsync();
            return new ExternalSignIn(user.Id);
        }

        // Matcad's accounts have a user name only: it is compared with the e-mail address, then with the preferred user name.
        var matches = await MatchByName(identity.Email);
        if (matches.Count == 0) matches = await MatchByName(identity.PreferredUserName);

        if (matches.Count > 1)
        {
            _log.LogWarning("MatIdentity subject {Subject} matches {Count} accounts; nobody is signed in", subject, matches.Count);
            return ExternalSignIn.Refused("Several accounts match this person.");
        }

        if (matches.Count == 1)
        {
            var match = matches[0];
            if (match.ExternalSubject != null)
            {
                _log.LogWarning("MatIdentity subject {Subject} matches user {UserId}, which is linked with another subject", subject, match.Id);
                return ExternalSignIn.Refused("The matching account is linked with another person.");
            }

            if (!string.IsNullOrEmpty(match.PasswordHash))
            {
                // Not linked by the name alone: the person proves the account with its password first.
                _log.LogInformation("MatIdentity subject {Subject} matches user {UserId}; the link waits for its password", subject, match.Id);
                return new ExternalSignIn(null, match.Id);
            }
        }

        user = new User
        {
            Username = await UniqueUsername(identity.PreferredUserName ?? identity.Email?.Split('@')[0] ?? identity.Name ?? "user"),
            // No password: the sign-in goes through MatIdentity (an admin can give the user one in Settings -> Users).
            PasswordHash = "",
            Role = UserRole.User,
            ExternalSubject = subject,
            CreateDate = DateTime.UtcNow
        };
        await ApplyExternalRole(user, identity.Role, created: true);
        _db.Users.Add(user);
        await _db.SaveChangesAsync();
        _log.LogInformation("User {UserId} ({Username}) was created at the first sign-in through MatIdentity", user.Id, user.Username);
        return new ExternalSignIn(user.Id);
    }

    /// <summary>
    /// Links the user <paramref name="userId"/> with the person of <paramref name="identity"/> after the user proved the account with its password
    /// (the pending link of <see cref="SignInExternal"/>): sets the subject and follows the role claim like a sign-in through MatIdentity.
    /// Refused when the account is gone or linked meanwhile, or when another user has the subject by now.
    /// </summary>
    public async Task<(User? User, string? Error)> LinkExternal(long userId, ExternalIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (string.IsNullOrWhiteSpace(identity.Subject)) return (null, "MatIdentity sent no subject (sub).");

        var subject = identity.Subject.Trim();
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null) return (null, "The account does not exist any more.");
        if (user.ExternalSubject != null || await _db.Users.AnyAsync(u => u.ExternalSubject == subject))
        {
            _log.LogWarning("User {UserId} was not linked with MatIdentity subject {Subject}: one of them is linked already", userId, subject);
            return (null, "The account is linked with MatIdentity already.");
        }

        user.ExternalSubject = subject;
        user.UpdateDate = DateTime.UtcNow;
        user.UpdateUserId = user.Id;
        await ApplyExternalRole(user, identity.Role, created: false);
        await _db.SaveChangesAsync();
        SessionCache.Clear();
        _log.LogInformation("User {UserId} ({Username}) linked with MatIdentity subject {Subject} after its password", user.Id, user.Username, subject);
        return (user, null);
    }

    /// <summary>The claim <c>admin</c> or <c>user</c> sets the role; the last admin is never demoted; anything else leaves the role alone.</summary>
    private async Task ApplyExternalRole(User user, string? claim, bool created)
    {
        UserRole? wanted = claim?.Trim().ToLowerInvariant() switch
        {
            IdentityAdminRole => UserRole.Admin,
            IdentityUserRole => UserRole.User,
            _ => null
        };
        if (wanted is not { } role || user.Role == role) return;

        if (!created && user.Role == UserRole.Admin && await _db.Users.CountAsync(u => u.Role == UserRole.Admin) <= 1)
        {
            _log.LogWarning("MatIdentity asks for role {Role} for user {UserId}, the last admin; the role stays", role, user.Id);
            return;
        }

        user.Role = role;
        if (!created)
        {
            user.UpdateDate = DateTime.UtcNow;
            SessionCache.Clear();
        }
    }

    private async Task<List<User>> MatchByName(string? name)
    {
        var wanted = name?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(wanted)) return [];
        return await _db.Users.Where(u => u.Username.ToLower() == wanted).ToListAsync();
    }

    private async Task<string> UniqueUsername(string wish)
    {
        var name = new string(wish.Trim().Where(c => !char.IsControl(c)).ToArray());
        if (name.Length == 0) name = "user";
        if (name.Length > 64) name = name[..64];
        var candidate = name;
        for (var i = 2; await _db.Users.AnyAsync(u => u.Username.ToLower() == candidate.ToLower()); i++)
            candidate = $"{name}{i}";
        return candidate;
    }

    // --- Health ---------------------------------------------------------------

    /// <summary>True when the database answers (the health check of the platform).</summary>
    public async Task<bool> CanReadDatabase(CancellationToken ct = default)
    {
        try
        {
            await _db.Users.AnyAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "The database cannot be read");
            return false;
        }
    }

    public Task<int> CountAdmins(CancellationToken ct = default) =>
        _db.Users.CountAsync(u => u.Role == UserRole.Admin, ct);

    // --- Sessions -----------------------------------------------------------

    /// <summary>A new session; <paramref name="idToken"/> is MatIdentity's ID token when the session began there (for the sign-out).</summary>
    public async Task<UserSession> CreateSession(User user, string? idToken = null)
    {
        var session = new UserSession
        {
            Token = Guid.NewGuid(),
            UserId = user.Id,
            ExpiryDate = DateTime.UtcNow.Add(SessionLifetime),
            IdToken = string.IsNullOrEmpty(idToken) ? null : idToken,
            CreateDate = DateTime.UtcNow,
            CreateUserId = user.Id
        };
        _db.UserSessions.Add(session);
        await _db.SaveChangesAsync();
        return session;
    }

    /// <summary>The ID token of MatIdentity kept with the session, or null.</summary>
    public Task<string?> GetSessionIdToken(Guid token) =>
        _db.UserSessions.AsNoTracking().Where(s => s.Token == token).Select(s => s.IdToken).FirstOrDefaultAsync();

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

        if (session.User != null) SessionCache[token] = (session.User, DateTime.UtcNow);
        return session.User;
    }

    public async Task InvalidateSession(Guid token)
    {
        SessionCache.TryRemove(token, out _);
        await _db.UserSessions.Where(s => s.Token == token).ExecuteDeleteAsync();
    }

    /// <summary>Creates the default admin on first start if no users exist: with <paramref name="adminPassword"/>
    /// (env <c>MATCAD_ADMIN_PASSWORD</c>, for a headless install) when that is set and acceptable, otherwise
    /// admin/admin. Returns true only when the admin was seeded with the given password, so the caller can
    /// skip the first-run wizard that would otherwise force a password change.</summary>
    public async Task<bool> EnsureSeedAdmin(ILogger logger, string? adminPassword = null)
    {
        if (await _db.Users.AnyAsync()) return false;
        if (!string.IsNullOrEmpty(adminPassword))
        {
            // Same rules as the setup wizard's account step.
            if (adminPassword.Length < 6 || adminPassword == "admin")
                logger.LogWarning("MATCAD_ADMIN_PASSWORD is ignored: it must be at least 6 characters and not 'admin'. " +
                    "Falling back to the default admin login.");
            else
            {
                await CreateUser("admin", adminPassword, UserRole.Admin, actorId: null);
                logger.LogInformation("Seeded admin user 'admin' with the password from MATCAD_ADMIN_PASSWORD.");
                return true;
            }
        }
        await CreateUser("admin", "admin", UserRole.Admin, actorId: null);
        logger.LogWarning("Seeded default admin user 'admin' with password 'admin'. Change it immediately.");
        return false;
    }
}
