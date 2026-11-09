using Matcad.Config;
using Matcad.Services;
using MatPlatform;

namespace Matcad.Platform;

/// <summary>
/// The bindings of <c>web.route</c> for MatPlatform (<c>/api/bindings</c> of a broker and the Connections area): a binding is a route of Matcad
/// (<see cref="RouteBindings"/> does the work, this class only translates). Parameters <c>host</c> (a name of the domain) or <c>port</c> (a port of its own
/// on Caddy, 1024-65535) with the optional <c>publicHost</c> (the name or IP address of the machine); the target is the consumer's internal address; the value
/// is <c>publicUrl</c> (<c>MAT_PUBLIC_URL</c>): the address of the host, or <c>http://&lt;publicHost&gt;:&lt;port&gt;</c>, and none for a port without
/// <c>publicHost</c> (MatPlatform then answers <c>""</c>, which a consumer reads as not set). After a change the config goes to Caddy like after a change
/// through <c>/api/v1</c>: when Caddy does not take it the route stays stored (it reaches Caddy with the next push) and the binding still holds.
/// </summary>
public sealed class MatcadRouteBindings(
    ConfigStore store,
    CaddyService caddy,
    MatPlatformOptions options,
    IHttpContextAccessor http,
    TimeProvider time,
    ILogger<MatcadRouteBindings> log) : IMatBindingHandler
{
    /// <summary>The key of the one value of a binding.</summary>
    public const string PublicUrl = "publicUrl";

    public string Capability => MatCapabilities.WebRoute;

    public Task<IReadOnlyList<MatBindingInfo>> ListAsync(CancellationToken ct)
        => Task.FromResult<IReadOnlyList<MatBindingInfo>>([.. RouteBindings.List(store).Select(r =>
            new MatBindingInfo(Capability, r.Binding!.Consumer, r.Name, RouteBindings.ManagedByOf(r), Utc(r.Binding.Since)))]);

    public async Task<MatBindResult> BindAsync(MatBindRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var input = new RouteBindInput(request.Consumer.Id, request.Consumer.Name, request.Param("host", ""), request.Consumer.InternalUrl, request.ManagedBy,
            request.Param("port", ""), request.Param("publicHost", ""));
        var (route, refusal) = RouteBindings.Bind(store, input, Language(), time.GetUtcNow().UtcDateTime);
        if (refusal != null)
        {
            log.LogInformation("The route of {Consumer} was refused ({ManagedBy}): {Reason}", request.Consumer.Id, request.ManagedBy, refusal.Message);
            throw new MatBindingRefusedException(refusal.Field, refusal.Message, refusal.Conflict);
        }

        log.LogInformation("Route {Target} -> {Upstream} is the binding of {Consumer} ({ManagedBy})",
            route!.ListenPort is > 0 ? $":{route.ListenPort}" : route.Host, route.Upstream, request.Consumer.Id, request.ManagedBy);
        await ApplyAsync(request.Consumer.Id);
        // A port route whose broker named no machine has no public address: the key stays out, so the consumer gets no MAT_PUBLIC_URL
        // (MatPlatform answers "" for it) rather than a guess such as localhost.
        var values = new Dictionary<string, string?>();
        if (RouteBindings.PublicUrlOf(route) is { } publicUrl)
            values[PublicUrl] = publicUrl;
        return new MatBindResult(values);
    }

    public async Task<bool> UnbindAsync(string consumer, string? managedBy, CancellationToken ct)
    {
        if (!RouteBindings.Unbind(store, consumer, managedBy))
            return false;

        log.LogInformation("The route of {Consumer} was removed ({ManagedBy})", consumer, managedBy ?? "any");
        await ApplyAsync(consumer);
        return true;
    }

    // Like /api/v1: the change is stored either way; a Caddy that is down gets it with the next push. Not tied to the request:
    // a caller that hangs up must not leave Caddy behind the stored routes.
    private async Task ApplyAsync(string consumer)
    {
        var (ok, error) = await caddy.ApplyAsync(CancellationToken.None);
        if (!ok)
            log.LogWarning("The route of {Consumer} is stored, but Caddy did not take the config: {Error}. It gets it with the next push.", consumer, error);
    }

    // The language of the messages: the one the Connections area is drawn in for its form, else what the caller asks for (Accept-Language).
    private string Language() => http.HttpContext is not { } context ? options.DefaultLanguage
        : context.Request.Path.StartsWithSegments(MatConnectionEndpoints.FormPath) ? MatConnectionsTagHelper.LanguageOf(context, options)
        : options.LanguageOf(context);

    private static DateTimeOffset Utc(DateTime time) => new(DateTime.SpecifyKind(time, DateTimeKind.Utc));
}
