using Matcad.Config;
using Matcad.Data;
using Matcad.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

public class AuthServiceTests : IDisposable
{
    private readonly TempDatabase _database = new();
    private readonly MatcadDbContext _db;
    private readonly AuthService _auth;

    public AuthServiceTests()
    {
        _db = _database.CreateContext();
        DatabaseBootstrapper.InitializeAsync(_db, NullLogger.Instance).GetAwaiter().GetResult();
        _auth = new AuthService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task The_seeded_admin_has_to_change_the_default_password()
    {
        await _auth.EnsureSeedAdmin(NullLogger.Instance);

        var admin = await _auth.ValidateCredentials("admin", "admin");

        Assert.NotNull(admin);
        Assert.True(admin.MustChangePassword);
        Assert.Equal(UserRole.Admin, admin.Role);
        await _auth.EnsureSeedAdmin(NullLogger.Instance); // only on an empty database
        Assert.Equal(1, await _db.Users.CountAsync());
    }

    [Fact]
    public async Task A_user_signs_in_with_the_username_or_the_email_in_any_case()
    {
        await _auth.CreateUser("Alice", "alice@example.org", "correct-horse", UserRole.User, null);

        Assert.NotNull(await _auth.ValidateCredentials("alice", "correct-horse"));
        Assert.NotNull(await _auth.ValidateCredentials(" ALICE@example.ORG ", "correct-horse"));
        Assert.Null(await _auth.ValidateCredentials("alice", "wrong"));
        Assert.Null(await _auth.ValidateCredentials("nobody", "correct-horse"));
    }

    [Fact]
    public async Task Username_and_email_share_one_namespace()
    {
        var alice = await _auth.CreateUser("alice", "alice@example.org", "correct-horse", UserRole.User, null);

        Assert.NotNull(await _auth.ValidateIdentity("ALICE", null, null));                    // same username
        Assert.NotNull(await _auth.ValidateIdentity("bob", "alice@example.org", null));       // same email
        Assert.NotNull(await _auth.ValidateIdentity("alice@example.org", null, null));        // a username that is someone's email
        Assert.Null(await _auth.ValidateIdentity("alice", "alice@example.org", alice.Id));    // herself
        Assert.Null(await _auth.ValidateIdentity("bob", "bob@example.org", null));
        Assert.NotNull(await _auth.ValidateIdentity("", null, null));
        Assert.NotNull(await _auth.ValidateIdentity("bob", "not an email", null));
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("admin", false)]
    [InlineData("ADMIN", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("long-enough", true)]
    public void The_password_policy(string? password, bool valid) =>
        Assert.Equal(valid, AuthService.ValidatePassword(password) is null);

    [Fact]
    public async Task A_new_password_signs_the_user_out_everywhere_else()
    {
        var user = await _auth.CreateUser("dave", null, "first-password", UserRole.User, null);
        var phone = await _auth.CreateSession(user);
        var laptop = await _auth.CreateSession(user);

        await _auth.UpdateUser(user, "second-password", mustChangePassword: false, actorId: user.Id, keepSession: laptop.Token);

        Assert.Null(await _auth.GetUserBySession(phone.Token));
        Assert.NotNull(await _auth.GetUserBySession(laptop.Token));
        Assert.NotNull(await _auth.ValidateCredentials("dave", "second-password"));
        Assert.Null(await _auth.ValidateCredentials("dave", "first-password"));
    }

    [Fact]
    public async Task Changing_the_role_keeps_the_password_and_the_sessions()
    {
        var user = await _auth.CreateUser("erin", null, "first-password", UserRole.User, null);
        var session = await _auth.CreateSession(user);

        user.Role = UserRole.Admin;
        await _auth.UpdateUser(user, null, mustChangePassword: null, actorId: null);

        Assert.NotNull(await _auth.ValidateCredentials("erin", "first-password"));
        Assert.NotNull(await _auth.GetUserBySession(session.Token));
    }

    [Fact]
    public async Task An_administrator_can_force_a_password_change_without_resetting_the_password()
    {
        var user = await _auth.CreateUser("judy", null, "her-password", UserRole.User, null);

        user.Role = UserRole.User;
        await _auth.UpdateUser(user, null, mustChangePassword: true, actorId: null);

        Assert.True((await _auth.ValidateCredentials("judy", "her-password"))!.MustChangePassword);
        await _auth.UpdateUser(user, null, mustChangePassword: null, actorId: null); // unchanged
        Assert.True((await _auth.GetUser(user.Id))!.MustChangePassword);
        await _auth.UpdateUser(user, "a-new-password", mustChangePassword: false, actorId: null);
        Assert.False((await _auth.GetUser(user.Id))!.MustChangePassword);
    }

    [Fact]
    public async Task Expired_sessions_do_not_sign_in_and_are_swept_at_the_next_sign_in()
    {
        var user = await _auth.CreateUser("frank", null, "a-password", UserRole.User, null);
        var expired = new UserSession { Token = Guid.NewGuid(), UserId = user.Id, ExpiryDate = DateTime.UtcNow.AddMinutes(-1), CreateDate = DateTime.UtcNow };
        var stale = new UserSession { Token = Guid.NewGuid(), UserId = user.Id, ExpiryDate = DateTime.UtcNow.AddDays(-2), CreateDate = DateTime.UtcNow };
        _db.UserSessions.AddRange(expired, stale);
        await _db.SaveChangesAsync();

        Assert.Null(await _auth.GetUserBySession(expired.Token));
        await _auth.CreateSession(user);

        Assert.Equal(1, await _db.UserSessions.CountAsync()); // only the new one is left
    }

    [Fact]
    public async Task A_signed_out_session_stops_working_at_once()
    {
        var user = await _auth.CreateUser("grace", null, "a-password", UserRole.User, null);
        var session = await _auth.CreateSession(user);
        Assert.NotNull(await _auth.GetUserBySession(session.Token)); // now cached

        await _auth.InvalidateSession(session.Token);

        Assert.Null(await _auth.GetUserBySession(session.Token));
    }

    [Fact]
    public async Task Replacing_all_users_replaces_the_sessions_too()
    {
        var user = await _auth.CreateUser("heidi", null, "a-password", UserRole.Admin, null);
        var session = await _auth.CreateSession(user);

        await _auth.ReplaceAllUsers([new RestoredUser("ivan", "ivan@example.org", BCrypt.Net.BCrypt.HashPassword("pw-for-ivan", 4), UserRole.Admin, MustChangePassword: false)], actorId: null);

        Assert.Null(await _auth.GetUserBySession(session.Token));
        Assert.NotNull(await _auth.ValidateCredentials("ivan@example.org", "pw-for-ivan"));
        Assert.Equal(1, await _auth.CountAdmins());
    }
}
