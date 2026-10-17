namespace Matcad.Services;

/// <summary>
/// The container's own health check (Docker <c>HEALTHCHECK</c>): <c>dotnet Matcad.dll --healthcheck</c> asks <c>/api/health</c> on localhost
/// (the image has no curl). 200 for <c>ok</c> and <c>degraded</c> (Caddy unreachable is degraded: restarting Matcad would not help), 503 for <c>down</c>.
/// </summary>
public static class HealthCheck
{
    public static async Task<int> RunAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var port = Environment.GetEnvironmentVariable("Matcad__Port") ?? "4433";
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/api/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
