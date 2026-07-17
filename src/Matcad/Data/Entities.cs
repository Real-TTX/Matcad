namespace Matcad.Data;

/// <summary>
/// Base class for all relational (SQLite) records. Every record carries audit
/// information as required by the project database conventions.
/// The primary key is always <see cref="Id"/> (BIGINT / long).
/// </summary>
public abstract class AuditedEntity
{
    public long Id { get; set; }
    public DateTime CreateDate { get; set; }
    public long? CreateUserId { get; set; }
    public DateTime? UpdateDate { get; set; }
    public long? UpdateUserId { get; set; }
}

public enum UserRole
{
    User = 0,
    Admin = 1
}

public class User : AuditedEntity
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.User;
}

/// <summary>
/// A login session. The <see cref="Token"/> is the security-relevant key that
/// is stored in the client cookie; <see cref="AuditedEntity.Id"/> stays internal.
/// Sessions live in SQLite, so they survive a container restart.
/// </summary>
public class UserSession : AuditedEntity
{
    public Guid Token { get; set; }
    public long UserId { get; set; }
    public DateTime ExpiryDate { get; set; }
    public User? User { get; set; }
}

/// <summary>
/// One ingested Caddy access-log line. Used for statistics and the
/// real-time / last-access views. High-volume table with rolling retention.
/// </summary>
public class RequestLog : AuditedEntity
{
    public string Host { get; set; } = "";
    public string Path { get; set; } = "";
    public string Method { get; set; } = "";
    public int Status { get; set; }
    public string RemoteIp { get; set; } = "";
    public double DurationMs { get; set; }
    public DateTime Timestamp { get; set; }
}
