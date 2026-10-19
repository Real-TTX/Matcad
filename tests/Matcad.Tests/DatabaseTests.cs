using System.Text.RegularExpressions;
using Matcad.Data;
using Matcad.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

/// <summary>A database file in a temp folder, with helpers to run SQL against it.</summary>
public sealed class TempDatabase : IDisposable
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "matcad-tests-" + Guid.NewGuid().ToString("N")[..8]);
    public string FilePath => System.IO.Path.Combine(Folder, "matcad.db");

    public TempDatabase() => Directory.CreateDirectory(Folder);

    public MatcadDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MatcadDbContext>().UseSqlite($"Data Source={FilePath};Pooling=False").Options);

    public void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={FilePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public List<(string Type, string Name, string Sql)> Schema()
    {
        using var connection = new SqliteConnection($"Data Source={FilePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL ORDER BY name";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, string)>();
        while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Folder, recursive: true); } catch (IOException) { /* temp folder, the OS cleans up */ }
    }
}

public class DatabaseTests
{
    /// <summary>The schema Matcad 0.5.0 created with EnsureCreated (dumped from that version).</summary>
    private const string LegacySchema = """
        CREATE TABLE "RequestLogs" ("Id" INTEGER NOT NULL CONSTRAINT "PK_RequestLogs" PRIMARY KEY AUTOINCREMENT, "Host" TEXT NOT NULL, "Path" TEXT NOT NULL,
            "Method" TEXT NOT NULL, "Status" INTEGER NOT NULL, "RemoteIp" TEXT NOT NULL, "DurationMs" REAL NOT NULL, "Timestamp" TEXT NOT NULL,
            "CreateDate" TEXT NOT NULL, "CreateUserId" INTEGER NULL, "UpdateDate" TEXT NULL, "UpdateUserId" INTEGER NULL);
        CREATE TABLE "Users" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT, "Username" TEXT NOT NULL, "PasswordHash" TEXT NOT NULL,
            "Role" INTEGER NOT NULL, "CreateDate" TEXT NOT NULL, "CreateUserId" INTEGER NULL, "UpdateDate" TEXT NULL, "UpdateUserId" INTEGER NULL);
        CREATE TABLE "UserSessions" ("Id" INTEGER NOT NULL CONSTRAINT "PK_UserSessions" PRIMARY KEY AUTOINCREMENT, "Token" TEXT NOT NULL, "UserId" INTEGER NOT NULL,
            "ExpiryDate" TEXT NOT NULL, "CreateDate" TEXT NOT NULL, "CreateUserId" INTEGER NULL, "UpdateDate" TEXT NULL, "UpdateUserId" INTEGER NULL,
            CONSTRAINT "FK_UserSessions_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE);
        CREATE INDEX "IX_RequestLogs_Host_Timestamp" ON "RequestLogs" ("Host", "Timestamp");
        CREATE INDEX "IX_RequestLogs_Timestamp" ON "RequestLogs" ("Timestamp");
        CREATE UNIQUE INDEX "IX_UserSessions_Token" ON "UserSessions" ("Token");
        CREATE INDEX "IX_UserSessions_UserId" ON "UserSessions" ("UserId");
        CREATE UNIQUE INDEX "IX_Users_Username" ON "Users" ("Username");
        """;

    private static async Task InitializeAsync(TempDatabase database)
    {
        using var db = database.CreateContext();
        await DatabaseBootstrapper.InitializeAsync(db, NullLogger.Instance);
    }

    [Fact]
    public async Task A_new_database_gets_the_current_schema_with_PascalCase_names_only()
    {
        using var database = new TempDatabase();

        await InitializeAsync(database);

        var schema = database.Schema();
        Assert.Equal(["AuditEntry", "RequestLog", "User", "UserSession"],
            schema.Where(s => s.Type == "table" && !s.Name.StartsWith("__") && s.Name != "sqlite_sequence").Select(s => s.Name).Order());

        // Every table, column, index and constraint name of the app: letters and digits, starting with a capital.
        var pascal = new Regex("^[A-Z][A-Za-z0-9]*$");
        foreach (var (type, name, sql) in schema.Where(s => !s.Name.StartsWith("__") && s.Name != "sqlite_sequence" && !s.Name.StartsWith("sqlite_autoindex")))
        {
            Assert.Matches(pascal, name);
            foreach (Match m in Regex.Matches(sql, "(?:CONSTRAINT|CREATE TABLE|ON|\\()\\s*\"([^\"]+)\"|^\\s*\"([^\"]+)\"", RegexOptions.Multiline))
            {
                var identifier = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                Assert.True(pascal.IsMatch(identifier), $"{type} {name}: '{identifier}' is not PascalCase");
            }
        }
    }

    [Fact]
    public async Task A_database_of_an_older_version_keeps_its_data_and_gets_the_new_schema()
    {
        using var database = new TempDatabase();
        var adminHash = BCrypt.Net.BCrypt.HashPassword("admin", 4);
        var bobHash = BCrypt.Net.BCrypt.HashPassword("something-else", 4);
        database.Execute(LegacySchema);
        database.Execute($"""
            INSERT INTO "Users" (Id, Username, PasswordHash, Role, CreateDate) VALUES
              (1, 'admin', '{adminHash}', 1, '2026-01-01 10:00:00'),
              (2, 'Bob', '{bobHash}', 0, '2026-01-02 10:00:00'),
              (3, 'bob', '{bobHash}', 0, '2026-01-03 10:00:00');
            INSERT INTO "UserSessions" (Id, Token, UserId, ExpiryDate, CreateDate) VALUES (1, '11111111-1111-1111-1111-111111111111', 2, '2099-01-01 00:00:00', '2026-01-02 10:00:00');
            INSERT INTO "RequestLogs" (Id, Host, Path, Method, Status, RemoteIp, DurationMs, Timestamp, CreateDate) VALUES
              (1, 'a.example.com', '/', 'GET', 200, '1.2.3.4', 1.5, '2026-01-02 10:00:00', '2026-01-02 10:00:01'),
              (2, 'a.example.com', '/x', 'POST', 500, '1.2.3.4', 2.5, '2026-01-02 10:00:02', '2026-01-02 10:00:03');
            """);

        await InitializeAsync(database);

        using var db = database.CreateContext();
        var users = await db.Users.OrderBy(u => u.Id).ToListAsync();
        Assert.Equal(["admin", "Bob", "bob-3"], users.Select(u => u.Username)); // the second "bob" only differed in case
        Assert.Equal([true, false, false], users.Select(u => u.MustChangePassword)); // admin / admin has to change now
        Assert.Equal(UserRole.Admin, users[0].Role);
        Assert.Equal(1, await db.UserSessions.CountAsync());
        Assert.Equal(2, await db.RequestLogs.CountAsync());
        Assert.Equal(500, (await db.RequestLogs.SingleAsync(r => r.Id == 2)).Status);

        var names = database.Schema().Select(s => s.Name).ToList();
        Assert.DoesNotContain(names, n => n.Contains("Legacy") || n is "Users" or "UserSessions" or "RequestLogs" || n.StartsWith("IX_") || n.StartsWith("PK_Users"));

        // Starting again changes nothing.
        await InitializeAsync(database);
        using var again = database.CreateContext();
        Assert.Equal(3, await again.Users.CountAsync());
    }

    [Fact]
    public async Task Usernames_and_emails_are_unique_regardless_of_case()
    {
        using var database = new TempDatabase();
        await InitializeAsync(database);
        using var db = database.CreateContext();
        db.Users.Add(new User { Username = "Alice", Email = "Alice@Example.org", PasswordHash = "x" });
        await db.SaveChangesAsync();

        db.Users.Add(new User { Username = "alice", PasswordHash = "x" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.Users.Add(new User { Username = "other", Email = "alice@example.org", PasswordHash = "x" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_a_user_deletes_the_sessions()
    {
        using var database = new TempDatabase();
        await InitializeAsync(database);
        using var db = database.CreateContext();
        var user = new User { Username = "carol", PasswordHash = "x" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.UserSessions.Add(new UserSession { Token = Guid.NewGuid(), UserId = user.Id, ExpiryDate = DateTime.UtcNow.AddDays(1) });
        await db.SaveChangesAsync();

        db.Users.Remove(user);
        await db.SaveChangesAsync();

        Assert.Equal(0, await db.UserSessions.CountAsync());
    }
}
