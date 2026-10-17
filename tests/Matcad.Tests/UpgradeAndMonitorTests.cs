using Matcad.Config;
using Matcad.Data;
using Matcad.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

/// <summary>
/// The two columns of the sign-in through MatIdentity come with the migration <c>MatIdentitySignIn</c>: a new database has them, a database of the
/// migrations before gets them, and a pre-migration build that already carried them (EnsureCreated plus the old column upgrade) keeps their values.
/// </summary>
public sealed class DatabaseUpgradeTests
{
    [Fact]
    public async Task A_new_database_has_the_columns_and_the_subject_is_unique()
    {
        using var database = new TempDatabase();
        await using (var db = database.CreateContext())
            await DatabaseBootstrapper.InitializeAsync(db, NullLogger.Instance);

        await using var check = database.CreateContext();
        check.Users.Add(new User { Username = "a", PasswordHash = "", ExternalSubject = "sub-1" });
        check.Users.Add(new User { Username = "b", PasswordHash = "x" });
        check.Users.Add(new User { Username = "c", PasswordHash = "x" }); // several users without a subject are fine
        await check.SaveChangesAsync();
        check.UserSessions.Add(new UserSession { Token = Guid.NewGuid(), UserId = check.Users.Single(u => u.Username == "a").Id, IdToken = "id-token" });
        await check.SaveChangesAsync();

        check.Users.Add(new User { Username = "d", PasswordHash = "x", ExternalSubject = "sub-1" });
        await Assert.ThrowsAsync<DbUpdateException>(() => check.SaveChangesAsync());
        Assert.Contains(database.Schema(), s => s.Name == "IxUserExternalSubject");
    }

    [Fact]
    public async Task A_database_of_the_first_migration_gets_the_columns_and_keeps_its_users()
    {
        using var database = new TempDatabase();
        await using (var db = database.CreateContext())
            await db.Database.MigrateAsync("20261010163550_InitialCreate");
        database.Execute("""INSERT INTO "User" (Username, PasswordHash, Role, MustChangePassword, CreateDate) VALUES ('admin', 'x', 1, 0, '2026-01-01 00:00:00');""");

        await using (var db = database.CreateContext())
        {
            await DatabaseBootstrapper.InitializeAsync(db, NullLogger.Instance);
            await DatabaseBootstrapper.InitializeAsync(db, NullLogger.Instance); // idempotent
        }

        await using var check = database.CreateContext();
        var admin = check.Users.Single();
        Assert.Equal("admin", admin.Username);
        Assert.Null(admin.ExternalSubject);
        Assert.Empty(await check.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task A_pre_migration_build_with_the_sign_in_keeps_the_links_and_id_tokens()
    {
        using var database = new TempDatabase();
        // The schema of the platform branch before the migrations: EnsureCreated plus the columns its upgrader added.
        database.Execute("""
            CREATE TABLE "Users" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT, "CreateDate" TEXT NOT NULL,
                "CreateUserId" INTEGER NULL, "UpdateDate" TEXT NULL, "UpdateUserId" INTEGER NULL, "Username" TEXT NOT NULL,
                "PasswordHash" TEXT NOT NULL, "Role" INTEGER NOT NULL, "ExternalSubject" TEXT NULL);
            CREATE UNIQUE INDEX "IX_Users_Username" ON "Users" ("Username");
            CREATE UNIQUE INDEX "IX_Users_ExternalSubject" ON "Users" ("ExternalSubject");
            CREATE TABLE "UserSessions" ("Id" INTEGER NOT NULL CONSTRAINT "PK_UserSessions" PRIMARY KEY AUTOINCREMENT, "CreateDate" TEXT NOT NULL,
                "CreateUserId" INTEGER NULL, "UpdateDate" TEXT NULL, "UpdateUserId" INTEGER NULL, "Token" TEXT NOT NULL, "UserId" INTEGER NOT NULL,
                "ExpiryDate" TEXT NOT NULL, "IdToken" TEXT NULL);
            CREATE TABLE "RequestLogs" ("Id" INTEGER NOT NULL CONSTRAINT "PK_RequestLogs" PRIMARY KEY AUTOINCREMENT, "Host" TEXT NOT NULL, "Path" TEXT NOT NULL,
                "Method" TEXT NOT NULL, "Status" INTEGER NOT NULL, "RemoteIp" TEXT NOT NULL, "DurationMs" REAL NOT NULL, "Timestamp" TEXT NOT NULL,
                "CreateDate" TEXT NOT NULL, "CreateUserId" INTEGER NULL, "UpdateDate" TEXT NULL, "UpdateUserId" INTEGER NULL);
            INSERT INTO "Users" (Id, CreateDate, Username, PasswordHash, Role, ExternalSubject) VALUES
              (1, '2026-01-01 00:00:00', 'admin', 'x', 1, NULL),
              (2, '2026-01-01 00:00:00', 'nina', '', 0, 'sub-nina');
            INSERT INTO "UserSessions" (Id, CreateDate, Token, UserId, ExpiryDate, IdToken) VALUES
              (1, '2026-01-01 00:00:00', '22222222-2222-2222-2222-222222222222', 2, '2099-01-01 00:00:00', 'id-token-nina');
            """);

        await using (var db = database.CreateContext())
            await DatabaseBootstrapper.InitializeAsync(db, NullLogger.Instance);

        await using var check = database.CreateContext();
        Assert.Equal([null, "sub-nina"], check.Users.OrderBy(u => u.Id).Select(u => u.ExternalSubject).ToList());
        Assert.Equal("id-token-nina", check.UserSessions.Single().IdToken);
        Assert.DoesNotContain(database.Schema(), s => s.Name.StartsWith("Legacy") || s.Name == "IX_Users_ExternalSubject");
    }
}

/// <summary>Which names the certificate check asks Caddy for.</summary>
public sealed class ProxyMonitorTests
{
    [Fact]
    public void One_name_per_certificate_without_internal_or_port_bound_hosts()
    {
        var routes = new List<RouteConfig>
        {
            new() { Host = "*.apps.example.com", Wildcard = true, ProviderId = 1, Enabled = true },
            new() { Host = "a.apps.example.com", Upstream = "http://a", Enabled = true },
            new() { Host = "site.example.org", Upstream = "http://s", Enabled = true },
            new() { Host = "off.example.org", Upstream = "http://o", Enabled = false },
            new() { Host = "localhost", Upstream = "http://l", Enabled = true },
            new() { Host = "port.example.org", Upstream = "http://p", ListenPort = 8081, Enabled = true },
            new() { Host = "*.empty.example.net", Wildcard = true, ProviderId = 1, Enabled = true },
        };

        var hosts = ProxyMonitor.ProbeHosts(routes, [new ProviderConfig { Id = 1, Name = "dns" }]);

        Assert.Equal(["a.apps.example.com", "matcad-check.empty.example.net", "site.example.org"], hosts.Order());
    }
}

/// <summary>A backup carries the link with MatIdentity, and a user that MatIdentity created (no password) can be restored.</summary>
public sealed class BackupLinkTests
{
    private static BackupUser Admin() => new() { Username = "admin", PasswordHash = BCrypt.Net.BCrypt.HashPassword("pw-of-the-admin", 4), Role = "Admin" };

    [Fact]
    public async Task A_linked_user_without_a_password_survives_a_restore()
    {
        var (users, error) = BackupRestore.CheckUsers([Admin(), new BackupUser { Username = "nina", PasswordHash = "", Role = "User", ExternalSubject = "sub-nina" }]);
        Assert.Null(error);

        using var database = new TempDatabase();
        await using var db = database.CreateContext();
        await DatabaseBootstrapper.InitializeAsync(db, NullLogger.Instance);
        await new AuthService(db).ReplaceAllUsers(users, actorId: null);

        var nina = await db.Users.AsNoTracking().SingleAsync(u => u.Username == "nina");
        Assert.Equal("sub-nina", nina.ExternalSubject);
        Assert.Equal("", nina.PasswordHash);
        Assert.Null(await new AuthService(db).ValidateCredentials("nina", "")); // still no local sign-in
    }

    [Fact]
    public void A_user_without_a_password_and_without_a_link_or_two_users_with_one_link_are_refused()
    {
        Assert.NotNull(BackupRestore.CheckUsers([Admin(), new BackupUser { Username = "x", PasswordHash = "", Role = "User" }]).Error);
        Assert.NotNull(BackupRestore.CheckUsers([Admin(),
            new BackupUser { Username = "a", PasswordHash = "", Role = "User", ExternalSubject = "sub-1" },
            new BackupUser { Username = "b", PasswordHash = "", Role = "User", ExternalSubject = "sub-1" }]).Error);
    }
}
