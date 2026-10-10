using Microsoft.EntityFrameworkCore;

namespace Matcad.Data;

/// <summary>
/// Brings the SQLite database to the current schema at startup: EF migrations for new and existing
/// databases, plus a one-time upgrade of databases created by Matcad 0.5.0 and earlier (created with
/// <c>EnsureCreated</c>, plural table names, no migration history, underscore index names).
/// </summary>
public static class DatabaseBootstrapper
{
    // Fixed statements (no values from outside), in the order the tables can be renamed and dropped.
    private static readonly string[] RenameLegacyTables =
    [
        "ALTER TABLE \"Users\" RENAME TO \"LegacyUsers\"",
        "ALTER TABLE \"UserSessions\" RENAME TO \"LegacyUserSessions\"",
        "ALTER TABLE \"RequestLogs\" RENAME TO \"LegacyRequestLogs\""
    ];
    private static readonly string[] DropLegacyTables =
    [
        "DROP TABLE \"LegacyRequestLogs\"",
        "DROP TABLE \"LegacyUserSessions\"",
        "DROP TABLE \"LegacyUsers\""
    ];

    public static async Task InitializeAsync(MatcadDbContext db, ILogger logger, CancellationToken ct = default)
    {
        // WAL keeps the high-volume request log from blocking readers; the mode is stored in the database file.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);

        // Step 1: move old tables out of the way, so the migration can create the new ones.
        if (await TableExistsAsync(db, "Users", ct))
        {
            logger.LogInformation("Upgrading the database created by an older Matcad version.");
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            foreach (var statement in RenameLegacyTables)
                await db.Database.ExecuteSqlRawAsync(statement, ct);
            await tx.CommitAsync(ct);
        }

        await db.Database.MigrateAsync(ct);

        // Step 2: copy the data over. Runs again after a crash between the steps (the legacy tables are still there).
        if (await TableExistsAsync(db, "LegacyUsers", ct))
            await CopyLegacyDataAsync(db, logger, ct);
    }

    private static async Task CopyLegacyDataAsync(MatcadDbContext db, ILogger logger, CancellationToken ct)
    {
        var legacyUsers = await db.Database
            .SqlQueryRaw<LegacyUserRow>("SELECT Id, Username, PasswordHash, Role, CreateDate, CreateUserId, UpdateDate, UpdateUserId FROM LegacyUsers")
            .ToListAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Usernames used to be case-sensitive; the new unique index is not. Keep both accounts if they only differ in case.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var legacy in legacyUsers)
        {
            var username = legacy.Username;
            if (!taken.Add(username))
            {
                username = $"{legacy.Username}-{legacy.Id}";
                taken.Add(username);
                logger.LogWarning("User '{Username}' clashes with another user that differs only in case; renamed to '{New}'.", legacy.Username, username);
            }

            // The default admin/admin account has to pick a new password now.
            var mustChange = Services.PasswordHashing.Verify("admin", legacy.PasswordHash);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO "User" (Id, Username, PasswordHash, Role, MustChangePassword, CreateDate, CreateUserId, UpdateDate, UpdateUserId)
                VALUES ({legacy.Id}, {username}, {legacy.PasswordHash}, {legacy.Role}, {mustChange}, {legacy.CreateDate}, {legacy.CreateUserId}, {legacy.UpdateDate}, {legacy.UpdateUserId})
                """,
                ct);
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO UserSession (Id, Token, UserId, ExpiryDate, CreateDate, CreateUserId, UpdateDate, UpdateUserId)
            SELECT Id, Token, UserId, ExpiryDate, CreateDate, CreateUserId, UpdateDate, UpdateUserId FROM LegacyUserSessions
            """, ct);
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO RequestLog (Id, Host, Path, Method, Status, RemoteIp, DurationMs, Timestamp, CreateDate, CreateUserId, UpdateDate, UpdateUserId)
            SELECT Id, Host, Path, Method, Status, RemoteIp, DurationMs, Timestamp, CreateDate, CreateUserId, UpdateDate, UpdateUserId FROM LegacyRequestLogs
            """, ct);

        foreach (var statement in DropLegacyTables)
            await db.Database.ExecuteSqlRawAsync(statement, ct);

        await tx.CommitAsync(ct);
        logger.LogInformation("Database upgrade finished ({Users} users).", legacyUsers.Count);
    }

    private static async Task<bool> TableExistsAsync(MatcadDbContext db, string name, CancellationToken ct) =>
        await db.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {0}", name)
            .SingleAsync(ct) > 0;

    private sealed record LegacyUserRow(
        long Id, string Username, string PasswordHash, int Role,
        DateTime CreateDate, long? CreateUserId, DateTime? UpdateDate, long? UpdateUserId);
}
