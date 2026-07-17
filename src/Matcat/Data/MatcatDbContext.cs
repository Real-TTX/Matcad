using Microsoft.EntityFrameworkCore;

namespace Matcat.Data;

public class MatcatDbContext : DbContext
{
    public MatcatDbContext(DbContextOptions<MatcatDbContext> options) : base(options) { }

    // Table names are PascalCase per project convention.
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<RequestLog> RequestLogs => Set<RequestLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.ToTable("Users");
            e.HasIndex(x => x.Username).IsUnique();
        });

        b.Entity<UserSession>(e =>
        {
            e.ToTable("UserSessions");
            e.HasIndex(x => x.Token).IsUnique();
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
        });

        b.Entity<RequestLog>(e =>
        {
            e.ToTable("RequestLogs");
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.Host);
        });
    }
}
