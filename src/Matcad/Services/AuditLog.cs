using Matcad.Auth;
using Matcad.Config;
using Matcad.Data;
using Microsoft.EntityFrameworkCore;

namespace Matcad.Services;

/// <summary>A page of the audit log.</summary>
public sealed record AuditPage(IReadOnlyList<AuditEntry> Entries, int Total, IReadOnlyList<string> Kinds);

/// <summary>
/// Who changed what in Matcad. The configuration reports its changes (<see cref="ConfigStore.Changed"/>), the user accounts through
/// <see cref="AuthService"/>; the actor comes from the request that made the change. Only names are kept, never a value.
/// A year of entries is kept.
/// </summary>
public sealed class AuditLog
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(365);

    private readonly IServiceScopeFactory _scopes;
    private readonly IHttpContextAccessor _http;
    private readonly ILogger<AuditLog> _log;
    private int _writes;

    public AuditLog(IServiceScopeFactory scopes, IHttpContextAccessor http, ILogger<AuditLog> log)
    {
        _scopes = scopes;
        _http = http;
        _log = log;
    }

    /// <summary>Starts recording the changes of the configuration.</summary>
    public void Attach(ConfigStore store) => store.Changed += change => Record(change.Kind, change.Action, change.Subject);

    /// <summary>The one who made the current change: the signed-in user, "API" for a call with the API key, "Matcad" for the app itself.</summary>
    private (string Name, long? UserId) Actor()
    {
        var context = _http.HttpContext;
        if (context == null) return ("Matcad", null);
        if (context.Request.Path.StartsWithSegments("/api")) return ("API", null);
        return context.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name } ? (name, context.User.GetUserId()) : ("anonymous", null);
    }

    public void Record(string kind, string action, string subject)
    {
        try
        {
            var (name, userId) = Actor();
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MatcadDbContext>();
            db.AuditEntries.Add(new AuditEntry
            {
                Actor = Cut(name, 256), Kind = Cut(kind, 64), Action = Cut(action, 64), Subject = Cut(subject, 512),
                CreateDate = DateTime.UtcNow, CreateUserId = userId
            });
            db.SaveChanges();
            // Now and then, drop what is older than the retention.
            if (Interlocked.Increment(ref _writes) % 100 == 1)
            {
                var before = DateTime.UtcNow - Retention;
                db.AuditEntries.Where(e => e.CreateDate < before).ExecuteDelete();
            }
        }
        catch (Exception ex)
        {
            // The change itself happened; a missing line in the log must not undo or block it.
            _log.LogWarning(ex, "Could not write the audit entry for {Kind} {Action}", kind, action);
        }
    }

    public async Task<AuditPage> ListAsync(string? search, string? kind, int take, CancellationToken ct = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatcadDbContext>();
        var kinds = await db.AuditEntries.AsNoTracking().Select(e => e.Kind).Distinct().OrderBy(k => k).ToListAsync(ct);
        IQueryable<AuditEntry> q = db.AuditEntries.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(kind)) q = q.Where(e => e.Kind == kind);
        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(e => e.Subject.Contains(search) || e.Actor.Contains(search));
        var total = await q.CountAsync(ct);
        var entries = await q.OrderByDescending(e => e.CreateDate).ThenByDescending(e => e.Id).Take(take).ToListAsync(ct);
        return new AuditPage(entries, total, kinds);
    }

    private static string Cut(string text, int max) => text.Length > max ? text[..max] : text;
}
