using System.Text;
using System.Text.Json;
using Matcad.Config;
using Matcad.Data;
using Microsoft.EntityFrameworkCore;

namespace Matcad.Services;

/// <summary>
/// Tails the Caddy JSON access log on the shared volume, parses each line into
/// a <see cref="RequestLog"/> (SQLite) and publishes it to the live stream.
/// Handles log rotation and applies rolling retention.
/// </summary>
public class LogIngestService : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly LogBroadcaster _broadcaster;
    private readonly ConfigStore _store;
    private readonly ILogger<LogIngestService> _log;
    private readonly string _logPath;
    private readonly string _statePath;

    private long _offset;
    private DateTime _lastRetention = DateTime.MinValue;

    public LogIngestService(IServiceScopeFactory scopes, LogBroadcaster broadcaster,
        ConfigStore store, IConfiguration cfg, ILogger<LogIngestService> log)
    {
        _scopes = scopes;
        _broadcaster = broadcaster;
        _store = store;
        _log = log;
        var dir = (cfg["Matcad:CaddyLogDir"] ?? "/caddy-logs").TrimEnd('/');
        _logPath = $"{dir}/access.log";
        // Persist the tail position on the data volume so a restart resumes where
        // it left off instead of re-ingesting the whole file (duplicate rows).
        _statePath = System.IO.Path.Combine((cfg["Matcad:DataDir"] ?? "/data").TrimEnd('/'), "logingest.offset");
        if (File.Exists(_statePath) && long.TryParse(File.ReadAllText(_statePath).Trim(), out var saved))
            _offset = saved;
    }

    private void SaveOffset()
    {
        try { File.WriteAllText(_statePath, _offset.ToString()); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not persist log ingest offset"); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Log ingest watching {Path}", _logPath);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TailOnce(stoppingToken);
                await ApplyRetention(stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Log ingest cycle failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
        }
    }

    internal async Task TailOnce(CancellationToken ct)
    {
        if (!File.Exists(_logPath)) return;

        using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = fs.Length;
        if (length < _offset)
        {
            // File was rotated/truncated -> start over.
            _offset = 0;
            SaveOffset();
        }
        if (length == _offset) return;

        fs.Seek(_offset, SeekOrigin.Begin);
        var buffer = new byte[length - _offset];
        var read = await fs.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, ct);

        // Only whole lines count: a line Caddy is still writing is read again next time. Working on bytes keeps
        // the saved offset exact, also when a multi-byte character is cut in half or Matcad restarts mid-line.
        var complete = read == 0 ? 0 : Array.LastIndexOf(buffer, (byte)'\n', read - 1) + 1;
        if (complete == 0) return;

        var entries = new List<(RequestLog Row, LogEntry Dto)>();
        foreach (var line in Encoding.UTF8.GetString(buffer, 0, complete).Split('\n'))
        {
            var parsed = Parse(line);
            if (parsed != null) entries.Add(parsed.Value);
        }

        if (entries.Count > 0)
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MatcadDbContext>();
            db.RequestLogs.AddRange(entries.Select(e => e.Row));
            await db.SaveChangesAsync(ct);
        }

        // Move on only after the rows are committed, so a failed insert reads the batch again instead of losing it.
        _offset += complete;
        SaveOffset();
        foreach (var e in entries) _broadcaster.Publish(e.Dto);
    }

    internal static (RequestLog, LogEntry)? Parse(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] != '{') return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("request", out var req)) return null;

            var ts = root.TryGetProperty("ts", out var tsEl) ? tsEl.GetDouble() : 0;
            var timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)(ts * 1000)).UtcDateTime;
            var host = GetString(req, "host");
            var uri = GetString(req, "uri");
            var method = GetString(req, "method");
            var ip = req.TryGetProperty("client_ip", out _) ? GetString(req, "client_ip") : GetString(req, "remote_ip");
            var status = root.TryGetProperty("status", out var st) ? st.GetInt32() : 0;
            var durationMs = (root.TryGetProperty("duration", out var d) ? d.GetDouble() : 0) * 1000;

            var row = new RequestLog
            {
                Timestamp = timestamp, Host = host, Path = uri, Method = method,
                Status = status, RemoteIp = ip, DurationMs = durationMs,
                CreateDate = DateTime.UtcNow
            };
            var dto = new LogEntry(timestamp, host, method, uri, status, ip, durationMs);
            return (row, dto);
        }
        catch
        {
            return null;
        }
    }

    private static string GetString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    internal async Task ApplyRetention(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastRetention < TimeSpan.FromHours(1)) return;
        _lastRetention = DateTime.UtcNow;

        var settings = _store.Settings;
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MatcadDbContext>();

        // 1) Age-based: drop entries older than the retention window.
        var days = Math.Max(1, settings.LogRetentionDays);
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var byAge = await db.RequestLogs.Where(r => r.Timestamp < cutoff).ExecuteDeleteAsync(ct);
        if (byAge > 0) _log.LogInformation("Log retention removed {Count} rows older than {Days}d", byAge, days);

        // 2) Hard row cap: keep only the newest N rows (Id is monotonic).
        var max = settings.LogRetentionMaxRows;
        if (max > 0)
        {
            var total = await db.RequestLogs.LongCountAsync(ct);
            if (total > max)
            {
                var thresholdId = await db.RequestLogs
                    .OrderByDescending(r => r.Id).Skip((int)Math.Min(max, int.MaxValue))
                    .Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);
                if (thresholdId is > 0)
                {
                    var byCap = await db.RequestLogs.Where(r => r.Id <= thresholdId).ExecuteDeleteAsync(ct);
                    if (byCap > 0) _log.LogInformation("Log retention removed {Count} rows over the {Max}-row cap", byCap, max);
                }
            }
        }
    }
}
