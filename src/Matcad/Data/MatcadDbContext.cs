using Microsoft.EntityFrameworkCore;

namespace Matcad.Data;

/// <summary>
/// Table, column, index and constraint names are PascalCase (no underscores);
/// tables are singular. Keys and indexes are named explicitly because the EF
/// defaults ("PK_Users", "IX_Users_Username") contain underscores.
/// </summary>
public class MatcadDbContext : DbContext
{
    public MatcadDbContext(DbContextOptions<MatcadDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("User");
            e.HasKey(x => x.Id).HasName("PkUser");
            // NOCASE: "Admin" and "admin" are the same login, and so are e-mail addresses that differ in case.
            e.Property(x => x.Username).HasMaxLength(128).UseCollation("NOCASE");
            e.Property(x => x.Email).HasMaxLength(256).UseCollation("NOCASE");
            e.Property(x => x.PasswordHash).HasMaxLength(100);
            e.HasIndex(x => x.Username).IsUnique().HasDatabaseName("IxUserUsername");
            e.HasIndex(x => x.Email).IsUnique().HasDatabaseName("IxUserEmail");
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("UserSession");
            e.HasKey(x => x.Id).HasName("PkUserSession");
            e.HasIndex(x => x.Token).IsUnique().HasDatabaseName("IxUserSessionToken");
            e.HasIndex(x => x.ExpiryDate).HasDatabaseName("IxUserSessionExpiryDate");
            e.HasIndex(x => x.UserId).HasDatabaseName("IxUserSessionUserId");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
                .HasConstraintName("FkUserSessionUser").OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<RequestLog>(e =>
        {
            e.ToTable("RequestLog");
            e.HasKey(x => x.Id).HasName("PkRequestLog");
            e.HasIndex(x => x.Timestamp).HasDatabaseName("IxRequestLogTimestamp");
            // "Latest access per host" and host-filtered statistics.
            e.HasIndex(x => new { x.Host, x.Timestamp }).HasDatabaseName("IxRequestLogHostTimestamp");
        });
    }
}

/// <summary>Used by the EF tools only (<c>dotnet ef migrations add</c>); the app itself is built by Program.cs.</summary>
public sealed class MatcadDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<MatcadDbContext>
{
    public MatcadDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MatcadDbContext>().UseSqlite("Data Source=matcad-design.db").Options);
}
