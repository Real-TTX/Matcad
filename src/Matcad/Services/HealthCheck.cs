namespace Matcad.Services;

/// <summary>The container's own health check: <c>dotnet Matcad.dll --healthcheck</c> asks <c>/healthz</c> on localhost.</summary>
public static class HealthCheck
{
    public static async Task<int> RunAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var port = Environment.GetEnvironmentVariable("Matcad__Port") ?? "4433";
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
