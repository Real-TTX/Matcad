namespace Matcad.Auth;

public static class RequestExtensions
{
    /// <summary>
    /// Whether the browser talks HTTPS. Behind Caddy the connection to Matcad is plain HTTP, so the
    /// proxy's X-Forwarded-Proto decides (it is what makes cookies "Secure" for the real client).
    /// </summary>
    public static bool IsHttpsClient(this HttpRequest request) =>
        request.IsHttps ||
        string.Equals(request.Headers["X-Forwarded-Proto"].FirstOrDefault(), "https", StringComparison.OrdinalIgnoreCase);

    public static string? ClientIp(this HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();
}
