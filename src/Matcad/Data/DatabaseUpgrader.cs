using Microsoft.EntityFrameworkCore;

namespace Matcad.Data;

/// <summary>
/// Brings an existing database up to the model. Matcad creates its database with <c>EnsureCreated</c>, which never touches a database
/// that already exists, so a column added later is added here (idempotent, run at every start after <c>EnsureCreated</c>).
/// </summary>
public static class DatabaseUpgrader
{
    public const string ExternalSubjectIndex = "IX_Users_ExternalSubject";

    public static void Upgrade(MatcadDbContext db)
    {
        // Sign-in through MatIdentity: the subject of a linked user and the ID token of a session (for the sign-out there).
        AddColumn(db, "Users", "ExternalSubject", "TEXT NULL");
        AddColumn(db, "UserSessions", "IdToken", "TEXT NULL");
        db.Database.ExecuteSqlRaw($"CREATE UNIQUE INDEX IF NOT EXISTS \"{ExternalSubjectIndex}\" ON \"Users\" (\"ExternalSubject\");");
    }

    private static void AddColumn(MatcadDbContext db, string table, string column, string type)
    {
        var exists = db.Database
            .SqlQueryRaw<string>(TableInfo(table))
            .AsEnumerable()
            .Any(name => string.Equals(name, column, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            db.Database.ExecuteSqlRaw(AddColumnSql(table, column, type));
        }
    }

    // Only the constant names above go into these statements (SQLite takes no parameters for names).
    private static string TableInfo(string table) => "SELECT name AS \"Value\" FROM pragma_table_info('" + table + "')";

    private static string AddColumnSql(string table, string column, string type) =>
        "ALTER TABLE \"" + table + "\" ADD COLUMN \"" + column + "\" " + type + ";";
}
