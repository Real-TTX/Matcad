using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matcad.Config;

namespace Matcad.Services;

/// <summary>
/// Talks to the Caddy admin API. Regenerates the full config from the current
/// Matcad state and pushes it via POST /load. Call <see cref="ApplyAsync"/>
/// whenever routes/providers/authentications change and once on startup.
/// </summary>
public class CaddyService
{
    private readonly IHttpClientFactory _http;
    private readonly CaddyConfigGenerator _generator;
    private readonly ConfigStore _store;
    private readonly IConfiguration _cfg;
    private readonly ILogger<CaddyService> _log;
    private readonly SemaphoreSlim _pushLock = new(1, 1);

    public CaddyService(IHttpClientFactory http, CaddyConfigGenerator generator,
        ConfigStore store, IConfiguration cfg, ILogger<CaddyService> log)
    {
        _http = http;
        _generator = generator;
        _store = store;
        _cfg = cfg;
        _log = log;
    }

    public string AdminUrl
    {
        get
        {
            var fromSettings = _store.Settings.CaddyAdminUrl;
            if (!string.IsNullOrWhiteSpace(fromSettings)) return fromSettings.TrimEnd('/');
            return (_cfg["Matcad:Caddy:AdminUrl"] ?? "http://caddy:2019").TrimEnd('/');
        }
    }

    public string BuildJson() => BuildNode().ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    /// <summary>The generated config with the optional raw-passthrough JSON deep-merged in.</summary>
    private JsonNode BuildNode()
    {
        var node = JsonSerializer.SerializeToNode(_generator.Build()) ?? new JsonObject();
        var raw = _store.Settings.RawCaddyJson;
        if (!string.IsNullOrWhiteSpace(raw) && node is JsonObject target)
        {
            try
            {
                if (JsonNode.Parse(raw) is JsonObject source) DeepMerge(target, source);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Invalid raw Caddy JSON; ignoring it"); }
        }
        // The answer for unknown hosts has no matcher: it goes last, after the routes of the extra JSON, or it would hide them.
        if (_generator.BuildUnknownHostRoute() is { } unknown && node["apps"]?["http"]?["servers"]?["matcad"]?["routes"] is JsonArray routes)
            routes.Add(JsonSerializer.SerializeToNode(unknown));
        return node;
    }

    // Objects merge recursively; arrays concatenate; scalars are overwritten.
    private static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source)
        {
            if (target[key] is JsonObject to && value is JsonObject so) DeepMerge(to, so);
            else if (target[key] is JsonArray ta && value is JsonArray sa)
                foreach (var item in sa) ta.Add(item?.DeepClone());
            else target[key] = value?.DeepClone();
        }
    }

    /// <summary>The outcome of the last push of the config (null = none yet since the start), for the platform status.</summary>
    public CaddyApplyResult? LastApply { get; private set; }

    /// <summary>Regenerates and pushes the config. Returns (success, error?).</summary>
    public async Task<(bool Ok, string? Error)> ApplyAsync(CancellationToken ct = default)
    {
        // One push at a time, each built when it is its turn: two quick changes cannot reach Caddy in reverse order
        // (and LastApply is the outcome of the push that really came last).
        await _pushLock.WaitAsync(ct);
        try
        {
            var result = await PushAsync(ct);
            LastApply = new CaddyApplyResult(DateTimeOffset.Now, result.Ok, result.Error);
            return result;
        }
        finally
        {
            _pushLock.Release();
        }
    }

    /// <summary>True when Caddy's admin API answers (a short timeout: the platform's status must not hang).</summary>
    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            using var resp = await client.GetAsync($"{AdminUrl}/config/", HttpCompletionOption.ResponseHeadersRead, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            return false;
        }
    }

    // Called under _pushLock only.
    private async Task<(bool Ok, string? Error)> PushAsync(CancellationToken ct)
    {
        try
        {
            var json = BuildJson();
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(15);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await client.PostAsync($"{AdminUrl}/load", content, ct);
            if (resp.IsSuccessStatusCode)
            {
                _log.LogInformation("Pushed config to Caddy ({Routes} routes).", _store.Routes.Count);
                return (true, null);
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            _log.LogError("Caddy rejected config ({Status}): {Body}", resp.StatusCode, body);
            return (false, $"{(int)resp.StatusCode}: {body}");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to reach Caddy admin API at {Url}", AdminUrl);
            return (false, ex.Message);
        }
    }

    public async Task<string?> GetRunningConfigAsync(CancellationToken ct = default)
    {
        try
        {
            var client = _http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            return await client.GetStringAsync($"{AdminUrl}/config/", ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read running Caddy config.");
            return null;
        }
    }
}

/// <summary>When Matcad last pushed its config to Caddy and whether Caddy took it.</summary>
public sealed record CaddyApplyResult(DateTimeOffset At, bool Ok, string? Error);
