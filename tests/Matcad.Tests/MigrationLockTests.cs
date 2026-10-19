using Matcad.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

/// <summary>
/// A process stopped while it migrates leaves EF Core's lock row behind, and the next start would wait for it forever (an update recreates the
/// container seconds after it started).
/// </summary>
public class MigrationLockTests
{
    private const string StaleLock =
        "CREATE TABLE \"__EFMigrationsLock\" (\"Id\" INTEGER NOT NULL PRIMARY KEY, \"Timestamp\" TEXT NOT NULL); " +
        "INSERT INTO \"__EFMigrationsLock\" VALUES (1, '2026-10-18 13:28:48');";

    [Fact]
    public async Task A_lock_left_behind_is_removed_and_the_migration_runs()
    {
        using var database = new TempDatabase();
        database.Execute(StaleLock);

        await using var db = database.CreateContext();
        Assert.True(await MigrationLock.ReleaseStaleAsync(db.Database));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await db.Database.MigrateAsync(timeout.Token);
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        Assert.False(await MigrationLock.ReleaseStaleAsync(db.Database));
    }

    [Fact]
    public async Task A_new_database_has_nothing_to_release()
    {
        using var database = new TempDatabase();
        await using var db = database.CreateContext();
        Assert.False(await MigrationLock.ReleaseStaleAsync(db.Database));
    }

    [Fact]
    public async Task The_startup_does_not_hang_on_a_stale_lock()
    {
        using var database = new TempDatabase();
        database.Execute(StaleLock);

        await using (var db = database.CreateContext())
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await DatabaseBootstrapper.InitializeAsync(db, NullLogger.Instance, timeout.Token);
        }

        // A second start (already migrated) finds the table empty and goes on.
        await using var again = database.CreateContext();
        Assert.False(await MigrationLock.ReleaseStaleAsync(again.Database));
        Assert.Empty(await again.Database.GetPendingMigrationsAsync());
    }
}
