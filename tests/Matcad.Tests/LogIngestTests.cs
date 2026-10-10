using System.Text;
using Matcad.Config;
using Matcad.Data;
using Matcad.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Matcad.Tests;

public sealed class LogIngestTests : IDisposable
{
    private readonly TempDatabase _database = new();
    private readonly string _logFile;
    private readonly ServiceProvider _services;
    private readonly LogIngestService _ingest;
    private readonly LogBroadcaster _broadcaster = new();

    public LogIngestTests()
    {
        var logDir = Path.Combine(_database.Folder, "logs");
        Directory.CreateDirectory(logDir);
        _logFile = Path.Combine(logDir, "access.log");
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Matcad:CaddyLogDir"] = logDir, ["Matcad:DataDir"] = _database.Folder, ["Matcad:ConfigDir"] = Path.Combine(_database.Folder, "config")
        }).Build();

        using (var db = _database.CreateContext())
            DatabaseBootstrapper.InitializeAsync(db, NullLogger.Instance).GetAwaiter().GetResult();

        _services = new ServiceCollection()
            .AddScoped(_ => _database.CreateContext())
            .BuildServiceProvider();
        _ingest = new LogIngestService(_services.GetRequiredService<IServiceScopeFactory>(), _broadcaster,
            new ConfigStore(cfg, NullLogger<ConfigStore>.Instance), cfg, NullLogger<LogIngestService>.Instance);
    }

    public void Dispose()
    {
        _services.Dispose();
        _database.Dispose();
    }

    private static string Line(string host, string uri, int status, double ts = 1_760_000_000.5) =>
        "{\"ts\":" + ts.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"status\":" + status +
        ",\"duration\":0.004,\"request\":{\"client_ip\":\"203.0.113.9\",\"host\":\"" + host + "\",\"method\":\"GET\",\"uri\":\"" + uri + "\"}}";

    private async Task<List<RequestLog>> RowsAsync()
    {
        using var db = _database.CreateContext();
        return await db.RequestLogs.OrderBy(r => r.Id).ToListAsync();
    }

    [Fact]
    public void A_caddy_access_log_line_becomes_a_row()
    {
        var parsed = LogIngestService.Parse(Line("app.example.com", "/x?y=1", 404));

        Assert.NotNull(parsed);
        var (row, dto) = parsed.Value;
        Assert.Equal("app.example.com", row.Host);
        Assert.Equal("/x?y=1", row.Path);
        Assert.Equal(404, row.Status);
        Assert.Equal("203.0.113.9", row.RemoteIp);
        Assert.Equal(4, row.DurationMs, precision: 3);
        Assert.Equal(row.Timestamp, dto.Timestamp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"msg\":\"no request\"}")]
    [InlineData("{ broken")]
    public void Lines_that_are_not_requests_are_skipped(string line) =>
        Assert.Null(LogIngestService.Parse(line));

    [Fact]
    public async Task Whole_lines_are_ingested_once_and_an_unfinished_line_waits()
    {
        await File.WriteAllTextAsync(_logFile, Line("a.example.com", "/1", 200) + "\n" + Line("a.example.com", "/2", 200).Substring(0, 30));

        await _ingest.TailOnce(default);
        Assert.Single(await RowsAsync());

        await _ingest.TailOnce(default); // nothing new: no duplicate
        Assert.Single(await RowsAsync());

        await File.AppendAllTextAsync(_logFile, Line("a.example.com", "/2", 200).Substring(30) + "\n");
        await _ingest.TailOnce(default);
        Assert.Equal(["/1", "/2"], (await RowsAsync()).Select(r => r.Path));
    }

    [Fact]
    public async Task A_multi_byte_character_split_between_two_reads_survives()
    {
        var bytes = Encoding.UTF8.GetBytes(Line("a.example.com", "/bücher", 200) + "\n");
        var cut = Array.IndexOf(bytes, (byte)0xC3) + 1; // between the two bytes of "ü"
        await using (var stream = new FileStream(_logFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            await stream.WriteAsync(bytes.AsMemory(0, cut));

        await _ingest.TailOnce(default);
        Assert.Empty(await RowsAsync());

        await using (var stream = new FileStream(_logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            await stream.WriteAsync(bytes.AsMemory(cut));
        await _ingest.TailOnce(default);

        Assert.Equal("/bücher", Assert.Single(await RowsAsync()).Path);
    }

    [Fact]
    public async Task A_new_instance_continues_where_the_old_one_stopped()
    {
        await File.WriteAllTextAsync(_logFile, Line("a.example.com", "/1", 200) + "\n");
        await _ingest.TailOnce(default);

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Matcad:CaddyLogDir"] = Path.GetDirectoryName(_logFile), ["Matcad:DataDir"] = _database.Folder, ["Matcad:ConfigDir"] = Path.Combine(_database.Folder, "config")
        }).Build();
        var restarted = new LogIngestService(_services.GetRequiredService<IServiceScopeFactory>(), _broadcaster,
            new ConfigStore(cfg, NullLogger<ConfigStore>.Instance), cfg, NullLogger<LogIngestService>.Instance);
        await File.AppendAllTextAsync(_logFile, Line("a.example.com", "/2", 200) + "\n");

        await restarted.TailOnce(default);

        Assert.Equal(["/1", "/2"], (await RowsAsync()).Select(r => r.Path));
    }

    [Fact]
    public async Task A_rotated_log_is_read_from_its_start()
    {
        await File.WriteAllTextAsync(_logFile, Line("a.example.com", "/old-1", 200) + "\n" + Line("a.example.com", "/old-2", 200) + "\n");
        await _ingest.TailOnce(default);

        await File.WriteAllTextAsync(_logFile, Line("a.example.com", "/new", 200) + "\n"); // smaller than before
        await _ingest.TailOnce(default);

        Assert.Equal(["/old-1", "/old-2", "/new"], (await RowsAsync()).Select(r => r.Path));
    }

    [Fact]
    public async Task New_entries_reach_the_live_stream()
    {
        var (reader, lease) = _broadcaster.Subscribe();
        using (lease)
        {
            await File.WriteAllTextAsync(_logFile, Line("a.example.com", "/live", 200) + "\n");
            await _ingest.TailOnce(default);

            Assert.True(reader.TryRead(out var entry));
            Assert.Equal("/live", entry.Path);
        }
    }

    [Fact]
    public async Task Retention_removes_old_rows_and_keeps_the_newest_within_the_cap()
    {
        using (var db = _database.CreateContext())
        {
            for (var i = 0; i < 10; i++)
                db.RequestLogs.Add(new RequestLog { Host = "h", Path = "/" + i, Timestamp = DateTime.UtcNow.AddMinutes(-i), CreateDate = DateTime.UtcNow });
            db.RequestLogs.Add(new RequestLog { Host = "h", Path = "/ancient", Timestamp = DateTime.UtcNow.AddDays(-90), CreateDate = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Matcad:CaddyLogDir"] = Path.GetDirectoryName(_logFile), ["Matcad:DataDir"] = _database.Folder, ["Matcad:ConfigDir"] = Path.Combine(_database.Folder, "config")
        }).Build();
        var store = new ConfigStore(cfg, NullLogger<ConfigStore>.Instance);
        var settings = store.Settings;
        settings.LogRetentionDays = 30;
        settings.LogRetentionMaxRows = 4;
        store.SaveSettings(settings);
        var ingest = new LogIngestService(_services.GetRequiredService<IServiceScopeFactory>(), _broadcaster, store, cfg, NullLogger<LogIngestService>.Instance);

        await ingest.ApplyRetention(default);

        var rows = await RowsAsync();
        Assert.Equal(4, rows.Count);
        Assert.DoesNotContain(rows, r => r.Path == "/ancient");
        Assert.Equal(["/6", "/7", "/8", "/9"], rows.Select(r => r.Path).Order());
    }
}
