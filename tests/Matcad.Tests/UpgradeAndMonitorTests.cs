using Matcad.Config;
using Matcad.Data;
using Matcad.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Matcad.Tests;

/// <summary>A database from before the sign-in through MatIdentity gets its new columns at the start, and keeps its users.</summary>
public sealed class DatabaseUpgradeTests
{
    [Fact]
    public void An_old_database_gets_the_new_columns()
    {
        var path = Path.Combine(Path.GetTempPath(), "matcad-upgrade-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var connection = new SqliteConnection("Data Source=" + path))
            {
                connection.Open();
                using var create = connection.CreateCommand();
                // The schema EnsureCreated made before (no ExternalSubject, no IdToken).
                create.CommandText = """
                    CREATE TABLE "Users" ("Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT, "CreateDate" TEXT NOT NULL,
                        "CreateUserId" INTEGER NULL, "UpdateDate" TEXT NULL, "UpdateUserId" INTEGER NULL, "Username" TEXT NOT NULL,
                        "PasswordHash" TEXT NOT NULL, "Role" INTEGER NOT NULL);
                    CREATE UNIQUE INDEX "IX_Users_Username" ON "Users" ("Username");
                    CREATE TABLE "UserSessions" ("Id" INTEGER NOT NULL CONSTRAINT "PK_UserSessions" PRIMARY KEY AUTOINCREMENT, "CreateDate" TEXT NOT NULL,
                        "CreateUserId" INTEGER NULL, "UpdateDate" TEXT NULL, "UpdateUserId" INTEGER NULL, "Token" TEXT NOT NULL, "UserId" INTEGER NOT NULL,
                        "ExpiryDate" TEXT NOT NULL);
                    INSERT INTO "Users" ("CreateDate", "Username", "PasswordHash", "Role") VALUES ('2026-01-01 00:00:00', 'admin', 'x', 1);
                    """;
                create.ExecuteNonQuery();
            }

            var options = new DbContextOptionsBuilder<MatcadDbContext>().UseSqlite("Data Source=" + path + ";Pooling=False").Options;
            using (var db = new MatcadDbContext(options))
            {
                DatabaseUpgrader.Upgrade(db);
                DatabaseUpgrader.Upgrade(db); // idempotent

                var admin = db.Users.Single();
                Assert.Null(admin.ExternalSubject);
                admin.ExternalSubject = "sub-1";
                db.SaveChanges();
                Assert.Equal("sub-1", db.Users.Single().ExternalSubject);
                Assert.Empty(db.UserSessions.Where(s => s.IdToken != null).ToList());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(path);
        }
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
