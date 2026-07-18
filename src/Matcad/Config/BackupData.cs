namespace Matcad.Config;

/// <summary>
/// A full Matcad backup: all JSON config plus the local user accounts (with their
/// bcrypt hashes). Sessions, request logs and Data-Protection keys are host-local
/// and intentionally not included.
/// </summary>
public class BackupData
{
    public int Version { get; set; } = 1;
    public DateTime ExportedAt { get; set; }
    public MatcadSettings Settings { get; set; } = new();
    public List<ProviderConfig> Providers { get; set; } = new();
    public List<RouteConfig> Routes { get; set; } = new();
    public List<AuthenticationConfig> Authentications { get; set; } = new();
    public Dictionary<string, long> Sequences { get; set; } = new();
    public List<BackupUser> Users { get; set; } = new();
}

public class BackupUser
{
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = "User";
}
