using Matcad.Controls;
using Matcad.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages;

/// <summary>Request-log statistics with filtering (time range, host, status class, method, path); the numbers come from <see cref="RequestLogQueries"/>.</summary>
public class StatisticsModel : PageModel
{
    private readonly RequestLogQueries _log;
    public StatisticsModel(RequestLogQueries log) => _log = log;

    [BindProperty(SupportsGet = true)] public string Range { get; set; } = "24h";
    [BindProperty(SupportsGet = true)] public string Host { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Status { get; set; } = "";   // "", "2","3","4","5"
    [BindProperty(SupportsGet = true)] public string Method { get; set; } = "";
    [BindProperty(SupportsGet = true)] public string Q { get; set; } = "";        // path contains

    /// <summary>The path search and the filters (rule LIST-03); everything is sent as one GET request.</summary>
    public ToolbarModel Toolbar { get; private set; } = null!;

    public TrafficStatistics Stats { get; private set; } = new();
    public long Total => Stats.Total;
    public long Errors => Stats.C4xx + Stats.C5xx;
    public double ErrorRate => Total > 0 ? Errors * 100.0 / Total : 0;
    public double AvgDurationMs => Stats.AvgDurationMs;
    public int DistinctHosts => Stats.DistinctHosts;
    public int DistinctClients => Stats.DistinctClients;
    public int C2xx => Stats.C2xx;
    public int C3xx => Stats.C3xx;
    public int C4xx => Stats.C4xx;
    public int C5xx => Stats.C5xx;
    public IReadOnlyList<TrafficBucket> Series => Stats.Series;
    public string SeriesUnit => Stats.SeriesUnit;
    public IReadOnlyList<TrafficCount> TopHosts => Stats.TopHosts;
    public IReadOnlyList<TrafficCount> TopPaths => Stats.TopPaths;
    public IReadOnlyList<TrafficCount> TopClients => Stats.TopClients;
    public IReadOnlyList<TrafficRequest> RecentRows => Stats.Recent;

    public static readonly string[] MethodOptions = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };

    public async Task OnGetAsync()
    {
        // The filter dialog sends an empty value for "Last 24 hours", the default.
        if (string.IsNullOrEmpty(Range)) Range = "24h";
        Stats = await _log.StatisticsAsync(new TrafficFilter(Range, Host, Status, Method, Q), HttpContext.RequestAborted);
        Toolbar = BuildToolbar();
    }

    private ToolbarModel BuildToolbar() => new()
    {
        Id = "stats",
        Action = "/Statistics",
        Label = "Statistics",
        Search = Q,
        SearchPlaceholder = "Path contains",
        SearchField = true,
        Noun = "requests",
        Total = (int)Math.Min(Total, int.MaxValue),
        Matching = (int)Math.Min(Total, int.MaxValue),
        Filters =
        [
            new FilterField("range", "Time", [new("1h", "Last hour"), new("7d", "Last 7 days"), new("30d", "Last 30 days"), new("all", "All time")], Range == "24h" ? null : Range) { AllLabel = "Last 24 hours" },
            new FilterField("host", "Host", Stats.Hosts.Select(h => new FilterOption(h, h)).ToList(), string.IsNullOrEmpty(Host) ? null : Host),
            new FilterField("status", "Status", [new("2", "2xx"), new("3", "3xx"), new("4", "4xx"), new("5", "5xx")], string.IsNullOrEmpty(Status) ? null : Status),
            new FilterField("method", "Method", MethodOptions.Select(m => new FilterOption(m, m)).ToList(), string.IsNullOrEmpty(Method) ? null : Method),
        ],
    };
}
