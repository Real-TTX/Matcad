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

    public AuthService(MatcadDbContext db) => _db = db;

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
        if (user == null) return null;
        return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash) ? user : null;
    }

    // --- Sessions -----------------------------------------------------------

    public async Task<UserSession> CreateSession(User user)
    {
        var session = new UserSession
        {
            Token = Guid.NewGuid(),
            UserId = user.Id,
            ExpiryDate = DateTime.UtcNow.Add(SessionLifetime),
            CreateDate = DateTime.UtcNow,
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

        if (session.User != null) SessionCache[token] = (session.User, DateTime.UtcNow);
        return session.User;
    }

    public async Task InvalidateSession(Guid token)
    {
        SessionCache.TryRemove(token, out _);
        await _db.UserSessions.Where(s => s.Token == token).ExecuteDeleteAsync();
    }

    /// <summary>Creates the default admin (admin/admin) on first start if no users exist.</summary>
    public async Task EnsureSeedAdmin(ILogger logger)
    {
        if (await _db.Users.AnyAsync()) return;
        await CreateUser("admin", "admin", UserRole.Admin, actorId: null);
        logger.LogWarning("Seeded default admin user 'admin' with password 'admin'. Change it immediately.");
    }
}
