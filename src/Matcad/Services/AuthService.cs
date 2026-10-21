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
    private readonly ILogger<AuthService> _log;
    private readonly AuditLog? _audit;

    public AuthService(MatcadDbContext db, ILogger<AuthService> log, AuditLog? audit = null)
    {
        _db = db;
        _log = log;
        _audit = audit;
    }

    /// <summary>Without a logger (unit tests, tools).</summary>
    public AuthService(MatcadDbContext db)
        : this(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthService>.Instance)
    {
    }

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

    public Task<int> CountAdmins(CancellationToken ct = default) =>
        _db.Users.CountAsync(u => u.Role == UserRole.Admin, ct);

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
        _audit?.Record("User", "created", user.Username);
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
        _audit?.Record("User", string.IsNullOrEmpty(newPassword) ? "changed" : "changed (new password)", user.Username);
    }

    public async Task DeleteUser(long id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return;
        _db.Users.Remove(user); // the sessions go with it (cascade)
        await _db.SaveChangesAsync();
        SessionCache.Clear(); // drop any cached sessions of the removed user
        _audit?.Record("User", "deleted", user.Username);
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
                MustChangePassword = u.MustChangePassword, ExternalSubject = u.ExternalSubject, CreateDate = DateTime.UtcNow, CreateUserId = actorId
            });
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        SessionCache.Clear();
        _audit?.Record("User", "restored", $"{_db.Users.Local.Count} users");
    }

    /// <summary>Returns the user if credentials are valid, otherwise null.</summary>
    public async Task<User?> ValidateCredentials(string login, string password)
    {
        var user = await FindByLogin(login);
        // A user created by MatIdentity has no password: it cannot sign in locally until an admin gives it one.
        if (user == null || string.IsNullOrEmpty(user.PasswordHash))
        {
            PasswordHashing.Verify(password, TimingEqualizerHash.Value);
            return null;
        }
        return PasswordHashing.Verify(password, user.PasswordHash) ? user : null;
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

        // A Matcad account signs in with its user name or its e-mail address: both are compared with the person's e-mail address,
        // then with the preferred user name.
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

        // The e-mail address goes with the new account when it is free and well-formed (it is a login too, so it must not clash).
        var email = NormalizeEmail(identity.Email);
        if (email != null && (email.Length > 256 || !email.Contains('@') || email.Any(char.IsWhiteSpace) || await LoginTaken(email)))
            email = null;
        user = new User
        {
            Username = await UniqueUsername(identity.PreferredUserName ?? identity.Email?.Split('@')[0] ?? identity.Name ?? "user"),
            Email = email,
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
        var wanted = name?.Trim();
        if (string.IsNullOrEmpty(wanted)) return [];
        // Username and Email compare without case (NOCASE columns).
        return await _db.Users.Where(u => u.Username == wanted || u.Email == wanted).ToListAsync();
    }

    // User name and e-mail address share one namespace, because both sign in.
    private Task<bool> LoginTaken(string login) => _db.Users.AnyAsync(u => u.Username == login || u.Email == login);

    private async Task<string> UniqueUsername(string wish)
    {
        var name = new string(wish.Trim().Where(c => !char.IsControl(c)).ToArray());
        if (name.Length == 0) name = "user";
        if (name.Length > 64) name = name[..64];
        var candidate = name;
        for (var i = 2; await LoginTaken(candidate); i++)
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

    // --- Sessions -----------------------------------------------------------

    /// <summary>
    /// A new session; <paramref name="idToken"/> is MatIdentity's ID token when the session began there (for the sign-out), <paramref name="externalSessionId"/>
    /// its session (the claim <c>sid</c>, for the back-channel logout).
    /// </summary>
    public async Task<UserSession> CreateSession(User user, string? idToken = null, string? externalSessionId = null)
    {
        var now = DateTime.UtcNow;
        // Expired sessions are only noticed when their cookie shows up again; sweep them on every sign-in.
        await _db.UserSessions.Where(s => s.ExpiryDate < now).ExecuteDeleteAsync();

        var session = new UserSession
        {
            Token = Guid.NewGuid(),
            UserId = user.Id,
            ExpiryDate = now.Add(SessionLifetime),
            IdToken = string.IsNullOrEmpty(idToken) ? null : idToken,
            ExternalSessionId = string.IsNullOrWhiteSpace(externalSessionId) ? null : externalSessionId.Trim(),
            CreateDate = now,
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

    /// <summary>
    /// MatIdentity's back-channel logout: with <paramref name="externalSessionId"/> the sessions that began with that <c>sid</c> end (only of the user
    /// linked to <paramref name="subject"/> when it is given), with <paramref name="subject"/> alone every session of the user linked to it.
    /// An unknown subject or sid ends nothing. Returns how many sessions ended; each user's ended sessions go to the audit log.
    /// </summary>
    public async Task<int> EndExternalSessions(string? subject, string? externalSessionId, CancellationToken ct = default)
    {
        var sub = string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
        var sid = string.IsNullOrWhiteSpace(externalSessionId) ? null : externalSessionId.Trim();
        if (sub == null && sid == null) return 0;

        long? userId = null;
        if (sub != null)
        {
            userId = await _db.Users.Where(u => u.ExternalSubject == sub).Select(u => (long?)u.Id).FirstOrDefaultAsync(ct);
            if (userId == null)
            {
                _log.LogInformation("MatIdentity ended the sessions of subject {Subject}, which is linked with no user here", sub);
                return 0;
            }
        }

        var sessions = _db.UserSessions.AsQueryable();
        if (userId != null) sessions = sessions.Where(s => s.UserId == userId);
        if (sid != null) sessions = sessions.Where(s => s.ExternalSessionId == sid);

        var ended = await sessions.Select(s => new { s.Id, s.Token, Username = s.User!.Username }).ToListAsync(ct);
        if (ended.Count == 0) return 0;

        var ids = ended.Select(e => e.Id).ToList();
        await _db.UserSessions.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync(ct);
        foreach (var e in ended) SessionCache.TryRemove(e.Token, out _);

        foreach (var group in ended.GroupBy(e => e.Username))
        {
            var count = group.Count();
            _audit?.Record("User", "signed out by MatIdentity", $"{group.Key} ({count} session{(count == 1 ? "" : "s")})", "MatIdentity");
        }
        _log.LogInformation("MatIdentity ended {Count} session(s) (subject {Subject}, sid {SessionId})", ended.Count, sub, sid);
        return ended.Count;
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
