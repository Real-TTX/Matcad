using Matcad.Data;
using Microsoft.EntityFrameworkCore;

namespace Matcad.Services;

/// <summary>The numbers of the dashboard.</summary>
public sealed record DashboardTraffic(int Total, int Last24h, int ClientErrors24h, int ServerErrors24h, IReadOnlyDictionary<string, DateTime> LastAccessByHost);

/// <summary>What the statistics page filters by. Range: 1h, 24h, 7d, 30d or all; Status: the first digit of the status code.</summary>
public sealed record TrafficFilter(string Range, string? Host, string? Status, string? Method, string? PathContains);

public sealed record TrafficBucket(DateTime At, string Label, int Count);
public sealed record TrafficCount(string Key, int Value);
public sealed record TrafficRequest(DateTime At, string Host, string Method, string Path, int Status, double DurationMs, string RemoteIp);

/// <summary>The statistics of the filtered request log.</summary>
public sealed class TrafficStatistics
{
    public long Total { get; init; }
    public int C2xx { get; init; }
    public int C3xx { get; init; }
    public int C4xx { get; init; }
    public int C5xx { get; init; }
    public double AvgDurationMs { get; init; }
    public int DistinctHosts { get; init; }
    public int DistinctClients { get; init; }
    public IReadOnlyList<TrafficBucket> Series { get; init; } = [];
    public string SeriesUnit { get; init; } = "hour";
    public IReadOnlyList<TrafficCount> TopHosts { get; init; } = [];
    public IReadOnlyList<TrafficCount> TopPaths { get; init; } = [];
    public IReadOnlyList<TrafficCount> TopClients { get; init; } = [];
    public IReadOnlyList<TrafficRequest> Recent { get; init; } = [];
    /// <summary>The hosts in the log (for the host filter).</summary>
    public IReadOnlyList<string> Hosts { get; init; } = [];
}

/// <summary>
/// The questions the pages ask of the request log. All aggregation runs in SQL so it scales to large log tables; the pages
/// themselves never touch the database.
/// </summary>
public sealed class RequestLogQueries
{
    private readonly MatcadDbContext _db;
    public RequestLogQueries(MatcadDbContext db) => _db = db;

    public async Task<DashboardTraffic> DashboardAsync(IReadOnlyCollection<string> hosts, CancellationToken ct = default)
    {
        var since = DateTime.UtcNow.AddHours(-24);
        var total = await _db.RequestLogs.CountAsync(ct);
        var last24h = await _db.RequestLogs
            .Where(r => r.Timestamp >= since)
            .GroupBy(r => r.Status / 100)
            .Select(g => new { StatusClass = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        // Latest access per host - a single grouped query (scales to many hosts).
        var lastByHost = await _db.RequestLogs
            .Where(r => hosts.Contains(r.Host))
            .GroupBy(r => r.Host)
            .Select(g => new { Host = g.Key, Last = g.Max(x => x.Timestamp) })
            .ToDictionaryAsync(x => x.Host, x => x.Last, ct);
        return new DashboardTraffic(total, last24h.Sum(x => x.Count),
            last24h.Where(x => x.StatusClass == 4).Sum(x => x.Count),
            last24h.Where(x => x.StatusClass >= 5).Sum(x => x.Count),
            lastByHost);
    }

    public async Task<TrafficStatistics> StatisticsAsync(TrafficFilter filter, CancellationToken ct = default)
    {
        var from = filter.Range switch
        {
            "1h" => DateTime.UtcNow.AddHours(-1),
            "24h" => DateTime.UtcNow.AddHours(-24),
            "7d" => DateTime.UtcNow.AddDays(-7),
            "30d" => DateTime.UtcNow.AddDays(-30),
            _ => (DateTime?)null // "all"
        };

        var hosts = await _db.RequestLogs.AsNoTracking()
            .Select(r => r.Host).Distinct().OrderBy(h => h).Take(300).ToListAsync(ct);

        IQueryable<RequestLog> q = _db.RequestLogs.AsNoTracking();
        if (from is { } f) q = q.Where(r => r.Timestamp >= f);
        if (!string.IsNullOrWhiteSpace(filter.Host)) q = q.Where(r => r.Host == filter.Host);
        if (!string.IsNullOrWhiteSpace(filter.Method)) q = q.Where(r => r.Method == filter.Method);
        if (!string.IsNullOrWhiteSpace(filter.PathContains)) q = q.Where(r => r.Path.Contains(filter.PathContains));
        q = filter.Status switch
        {
            "2" => q.Where(r => r.Status >= 200 && r.Status < 300),
            "3" => q.Where(r => r.Status >= 300 && r.Status < 400),
            "4" => q.Where(r => r.Status >= 400 && r.Status < 500),
            "5" => q.Where(r => r.Status >= 500),
            _ => q
        };

        // One pass over the filtered rows gives the total and the split by status class.
        var byClass = await q.GroupBy(r => r.Status / 100)
            .Select(g => new { StatusClass = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var total = byClass.Sum(x => (long)x.Count);
        if (total == 0) return new TrafficStatistics { Hosts = hosts };

        var (series, unit) = await SeriesAsync(q, from, ct);
        return new TrafficStatistics
        {
            Total = total,
            C2xx = byClass.Where(x => x.StatusClass == 2).Sum(x => x.Count),
            C3xx = byClass.Where(x => x.StatusClass == 3).Sum(x => x.Count),
            C4xx = byClass.Where(x => x.StatusClass == 4).Sum(x => x.Count),
            C5xx = byClass.Where(x => x.StatusClass >= 5).Sum(x => x.Count),
            AvgDurationMs = await q.Select(r => (double?)r.DurationMs).AverageAsync(ct) ?? 0,
            DistinctHosts = await q.Select(r => r.Host).Distinct().CountAsync(ct),
            DistinctClients = await q.Select(r => r.RemoteIp).Distinct().CountAsync(ct),
            TopHosts = await TopAsync(q.GroupBy(r => r.Host), ct),
            TopPaths = await TopAsync(q.GroupBy(r => r.Path), ct),
            TopClients = await TopAsync(q.GroupBy(r => r.RemoteIp), ct),
            Recent = (await q.OrderByDescending(r => r.Timestamp).Take(100)
                    .Select(r => new { r.Timestamp, r.Host, r.Method, r.Path, r.Status, r.DurationMs, r.RemoteIp })
                    .ToListAsync(ct))
                .Select(r => new TrafficRequest(r.Timestamp, r.Host, r.Method, r.Path, r.Status, r.DurationMs, r.RemoteIp)).ToList(),
            Series = series,
            SeriesUnit = unit,
            Hosts = hosts,
        };
    }

    private static async Task<IReadOnlyList<TrafficCount>> TopAsync(IQueryable<IGrouping<string, RequestLog>> groups, CancellationToken ct) =>
        (await groups.Select(g => new { g.Key, C = g.Count() }).OrderByDescending(x => x.C).Take(10).ToListAsync(ct))
            .Select(x => new TrafficCount(x.Key, x.C)).ToList();

    /// <summary>Requests over time, with a bucket size that gives a readable number of bars.</summary>
    private static async Task<(List<TrafficBucket>, string)> SeriesAsync(IQueryable<RequestLog> q, DateTime? from, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var start = from ?? await q.MinAsync(r => r.Timestamp, ct);
        var span = now - start;

        if (span <= TimeSpan.FromHours(1.5))
        {
            var raw = await q.GroupBy(r => new { r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day, r.Timestamp.Hour, r.Timestamp.Minute })
                .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, g.Key.Minute, C = g.Count() })
                .ToListAsync(ct);
            var map = raw.ToDictionary(x => new DateTime(x.Year, x.Month, x.Day, x.Hour, x.Minute, 0), x => x.C);
            return (Fill(map, start, now, TimeSpan.FromMinutes(1), "HH:mm"), "minute");
        }
        if (span <= TimeSpan.FromDays(2))
        {
            var raw = await q.GroupBy(r => new { r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day, r.Timestamp.Hour })
                .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, g.Key.Hour, C = g.Count() })
                .ToListAsync(ct);
            var map = raw.ToDictionary(x => new DateTime(x.Year, x.Month, x.Day, x.Hour, 0, 0), x => x.C);
            return (Fill(map, start, now, TimeSpan.FromHours(1), "HH:mm"), "hour");
        }
        var days = await q.GroupBy(r => new { r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day })
            .Select(g => new { g.Key.Year, g.Key.Month, g.Key.Day, C = g.Count() })
            .ToListAsync(ct);
        var dayMap = days.ToDictionary(x => new DateTime(x.Year, x.Month, x.Day), x => x.C);
        return (Fill(dayMap, start.Date, now.Date, TimeSpan.FromDays(1), "MM-dd"), "day");
    }

    private static List<TrafficBucket> Fill(Dictionary<DateTime, int> map, DateTime start, DateTime end, TimeSpan step, string format)
    {
        // Truncate the start to the step boundary and cap the number of bars.
        var b = step == TimeSpan.FromMinutes(1) ? new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0)
              : step == TimeSpan.FromHours(1) ? new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0)
              : start.Date;
        var list = new List<TrafficBucket>();
        for (var t = b; t <= end && list.Count < 400; t = t.Add(step))
            list.Add(new TrafficBucket(t, t.ToString(format), map.TryGetValue(t, out var c) ? c : 0));
        return list;
    }
}
