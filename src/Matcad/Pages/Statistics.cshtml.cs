using Matcad.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Matcad.Pages;

/// <summary>Request-log statistics with filtering (time range, host, status class,
/// method, path). All aggregation runs in SQL so it scales to large log tables.</summary>
public class StatisticsModel : PageModel
{
    private readonly MatcadDbContext _db;
    public StatisticsModel(MatcadDbContext db) => _db = db;

    [BindProperty(SupportsGet = true)] public string Range { get; set; } = "24h";
    [BindProperty(SupportsGet = true)] public string Host { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Status { get; set; } = "";   // "", "2","3","4","5"
    [BindProperty(SupportsGet = true)] public string Method { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Q { get; set; } = "";        // path contains

    public long Total { get; private set; }
    public long Errors { get; private set; }
    public double ErrorRate => Total > 0 ? Errors * 100.0 / Total : 0;
    public double AvgDurationMs { get; private set; }
    public int DistinctHosts { get; private set; }
    public int DistinctClients { get; private set; }

    public int C2xx { get; private set; }
    public int C3xx { get; private set; }
    public int C4xx { get; private set; }
    public int C5xx { get; private set; }

    public List<Bucket> Series { get; private set; } = new();
    public string SeriesUnit { get; private set; } = "hour";
    public List<Count> TopHosts { get; private set; } = new();
    public List<Count> TopPaths { get; private set; } = new();
    public List<Recent> RecentRows { get; private set; } = new();

    public List<string> HostOptions { get; private set; } = new();
    public static readonly string[] MethodOptions = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    public record Bucket(DateTime At, string Label, int Count);
    public record Count(string Key, int Value);
    public record Recent(DateTime At, string Host, string Method, string Path, int Status, double DurationMs, string RemoteIp);

    public async Task OnGetAsync()
    {
        var from = Range switch
        {
            "1h" => DateTime.UtcNow.AddHours(-1),
            "24h" => DateTime.UtcNow.AddHours(-24),
            "7d" => DateTime.UtcNow.AddDays(-7),
            "30d" => DateTime.UtcNow.AddDays(-30),
            _ => (DateTime?)null // "all"
        };

        HostOptions = await _db.RequestLogs.AsNoTracking()
            .Select(r => r.Host).Distinct().OrderBy(h => h).Take(300).ToListAsync();

        IQueryable<RequestLog> q = _db.RequestLogs.AsNoTracking();
        if (from is { } f) q = q.Where(r => r.Timestamp >= f);
        if (!string.IsNullOrWhiteSpace(Host)) q = q.Where(r => r.Host == Host);
        if (!string.IsNullOrWhiteSpace(Method)) q = q.Where(r => r.Method == Method);
        if (!string.IsNullOrWhiteSpace(Q)) q = q.Where(r => r.Path.Contains(Q));
        q = Status switch
        {
            "2" => q.Where(r => r.Status >= 200 && r.Status < 300),
            "3" => q.Where(r => r.Status >= 300 && r.Status < 400),
            "4" => q.Where(r => r.Status >= 400 && r.Status < 500),
            "5" => q.Where(r => r.Status >= 500),
            _ => q
        };

        Total = await q.LongCountAsync();
        if (Total == 0) return;

        C2xx = await q.CountAsync(r => r.Status >= 200 && r.Status < 300);
        C3xx = await q.CountAsync(r => r.Status >= 300 && r.Status < 400);
        C4xx = await q.CountAsync(r => r.Status >= 400 && r.Status < 500);
        C5xx = await q.CountAsync(r => r.Status >= 500);
        Errors = C4xx + C5xx;
        AvgDurationMs = await q.Select(r => (double?)r.DurationMs).AverageAsync() ?? 0;
        DistinctHosts = await q.Select(r => r.Host).Distinct().CountAsync();
        DistinctClients = await q.Select(r => r.RemoteIp).Distinct().CountAsync();

        TopHosts = (await q.GroupBy(r => r.Host)
            .Select(g => new { g.Key, C = g.Count() })
            .OrderByDescending(x => x.C).Take(10).ToListAsync())
            .Select(x => new Count(x.Key, x.C)).ToList();
        TopPaths = (await q.GroupBy(r => r.Path)
            .Select(g => new { g.Key, C = g.Count() })
            .OrderByDescending(x => x.C).Take(10).ToListAsync())
            .Select(x => new Count(x.Key, x.C)).ToList();

        RecentRows = (await q.OrderByDescending(r => r.Timestamp).Take(100)
            .Select(r => new { r.Timestamp, r.Host, r.Method, r.Path, r.Status, r.DurationMs, r.RemoteIp })
            .ToListAsync())
            .Select(r => new Recent(r.Timestamp, r.Host, r.Method, r.Path, r.Status, r.DurationMs, r.RemoteIp)).ToList();

        await BuildSeries(q, from);
    }

    private async Task BuildSeries(IQueryable<RequestLog> q, DateTime? from)
    {
        // Pick a bucket granularity that yields a readable number of bars.
        var now = DateTime.UtcNow;
        var start = from ?? await q.MinAsync(r => r.Timestamp);
        var span = now - start;

        if (span <= TimeSpan.FromHours(1.5))
        {
            SeriesUnit = "minute";
            var raw = await q.GroupBy(r => new { r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day, r.Timestamp.Hour, r.Timestamp.Minute })
                .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, g.Key.Minute, C = g.Count() })
                .ToListAsync();
            var map = raw.ToDictionary(x => new DateTime(x.Year, x.Month, x.Day, x.Hour, x.Minute, 0), x => x.C);
            FillBuckets(map, start, now, TimeSpan.FromMinutes(1), "HH:mm");
        }
        else if (span <= TimeSpan.FromDays(2))
        {
            SeriesUnit = "hour";
            var raw = await q.GroupBy(r => new { r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day, r.Timestamp.Hour })
                .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, C = g.Count() })
                .ToListAsync();
            var map = raw.ToDictionary(x => new DateTime(x.Year, x.Month, x.Day, x.Hour, 0, 0), x => x.C);
            FillBuckets(map, start, now, TimeSpan.FromHours(1), "HH:mm");
        }
        else
        {
            SeriesUnit = "day";
            var raw = await q.GroupBy(r => new { r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day })
                .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, C = g.Count() })
                .ToListAsync();
            var map = raw.ToDictionary(x => new DateTime(x.Year, x.Month, x.Day), x => x.C);
            FillBuckets(map, start.Date, now.Date, TimeSpan.FromDays(1), "MM-dd");
        }
    }

    private void FillBuckets(Dictionary<DateTime, int> map, DateTime start, DateTime end, TimeSpan step, string fmt)
    {
        // Truncate start to the step boundary and cap the number of bars.
        var b = step == TimeSpan.FromMinutes(1) ? new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0)
              : step == TimeSpan.FromHours(1) ? new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0)
              : start.Date;
        var list = new List<Bucket>();
        for (var t = b; t <= end && list.Count < 400; t = t.Add(step))
            list.Add(new Bucket(t, t.ToString(fmt), map.TryGetValue(t, out var c) ? c : 0));
        Series = list;
    }
}
