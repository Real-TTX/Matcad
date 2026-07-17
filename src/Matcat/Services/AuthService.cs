using Matcat.Data;
using Microsoft.EntityFrameworkCore;

namespace Matcat.Services;

/// <summary>
/// Local user management, password hashing, and session handling.
/// Sessions are stored in SQLite (survive container restarts); the cookie only
/// carries the session <see cref="UserSession.Token"/>.
/// </summary>
public class AuthService
{
    public const string CookieName = "matcat_session";
    /// <summary>Cookie used by the forward-auth portal; scoped to the base domain
    /// so one login covers all protected subdomains.</summary>
    public const string ForwardCookieName = "matcat_fwd";
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);

    private readonly MatcatDbContext _db;

    public AuthService(MatcatDbContext db) => _db = db;

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

    public async Task DeleteUser(long id)
    {
        var user = await _db.Users.FindAsync(id);
        if (user == null) return;
        _db.UserSessions.RemoveRange(_db.UserSessions.Where(s => s.UserId == id));
        _db.Users.Remove(user);
        await _db.SaveChangesAsync();
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

    /// <summary>Resolves the user for a valid, unexpired session token.</summary>
    public async Task<User?> GetUserBySession(Guid token)
    {
        var session = await _db.UserSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.Token == token);
        if (session == null) return null;
        if (session.ExpiryDate < DateTime.UtcNow)
        {
            _db.UserSessions.Remove(session);
            await _db.SaveChangesAsync();
            return null;
        }
        return session.User;
    }

    public async Task InvalidateSession(Guid token)
    {
        var session = await _db.UserSessions.FirstOrDefaultAsync(s => s.Token == token);
        if (session != null)
        {
            _db.UserSessions.Remove(session);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>Creates the default admin (admin/admin) on first start if no users exist.</summary>
    public async Task EnsureSeedAdmin(ILogger logger)
    {
        if (await _db.Users.AnyAsync()) return;
        await CreateUser("admin", "admin", UserRole.Admin, actorId: null);
        logger.LogWarning("Seeded default admin user 'admin' with password 'admin'. Change it immediately.");
    }
}
