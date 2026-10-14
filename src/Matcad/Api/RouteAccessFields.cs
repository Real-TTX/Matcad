using System.Text.Json;
using Matcad.Config;

namespace Matcad.Api;

/// <summary>The access properties of a route as a POST /routes call settles them: what is to be stored.</summary>
public record ResolvedAccess(string? UpstreamHost, List<HeaderPair> UpstreamHeaders, HeaderPair? RequireHeader, List<string> AllowedIps);

/// <summary>
/// Reads the four optional access fields of <c>POST /api/v1/routes</c> (upstreamHost, upstreamHeaders, requireHeader,
/// allowedIps). Each has three states that a bound record cannot tell apart, so they arrive as <see cref="JsonElement"/>:
/// <list type="bullet">
/// <item>missing (<see cref="JsonValueKind.Undefined"/>): the stored value stays as it is,</item>
/// <item><c>null</c>: the property is removed,</item>
/// <item>a value: it replaces the stored one.</item>
/// </list>
/// The rest of the call keeps its old "the call describes the whole route" meaning; this is only for the new fields, so a
/// client that does not know them (matOS) cannot wipe a guard another client set. A header entry without a value (or with
/// "valueSet": true, as the responses show it) keeps the stored value of the header of that name.
/// </summary>
internal static class RouteAccessFields
{
    /// <summary>Settles the fields against the route that is stored now (null for a new route). Returns an error message or null.</summary>
    public static string? Resolve(MatcadApi.RouteInput input, RouteConfig? stored, out ResolvedAccess access)
    {
        access = new ResolvedAccess(null, new(), null, new());

        string? host;
        switch (input.UpstreamHost.ValueKind)
        {
            case JsonValueKind.Undefined: host = stored?.UpstreamHost; break;
            case JsonValueKind.Null: host = null; break;
            case JsonValueKind.String: host = input.UpstreamHost.GetString(); break;
            default: return "upstreamHost must be a string or null.";
        }

        List<HeaderPair> headers;
        switch (input.UpstreamHeaders.ValueKind)
        {
            case JsonValueKind.Undefined: headers = Clone(stored?.UpstreamHeaders); break;
            case JsonValueKind.Null: headers = new(); break;
            case JsonValueKind.Array:
                headers = new();
                foreach (var item in input.UpstreamHeaders.EnumerateArray())
                {
                    var (pair, error) = ReadPair(item, "upstreamHeaders");
                    if (error != null) return error;
                    headers.Add(pair!);
                }
                var keepError = RouteGuards.KeepStoredValues(headers, stored?.UpstreamHeaders, "Upstream header");
                if (keepError != null) return keepError;
                break;
            default: return "upstreamHeaders must be an array of { \"name\", \"value\" } or null.";
        }

        HeaderPair? require;
        switch (input.RequireHeader.ValueKind)
        {
            case JsonValueKind.Undefined: require = stored?.RequireHeader is { } old ? new HeaderPair { Name = old.Name, Value = old.Value } : null; break;
            case JsonValueKind.Null: require = null; break;
            case JsonValueKind.Object:
                var (pair2, error2) = ReadPair(input.RequireHeader, "requireHeader");
                if (error2 != null) return error2;
                var keepError2 = RouteGuards.KeepStoredValues([pair2!], stored?.RequireHeader is { } s ? new[] { s } : null, "Required header");
                if (keepError2 != null) return keepError2;
                require = pair2;
                break;
            default: return "requireHeader must be an object { \"name\", \"value\" } or null.";
        }

        List<string> ips;
        switch (input.AllowedIps.ValueKind)
        {
            case JsonValueKind.Undefined: ips = stored?.AllowedIps.ToList() ?? new(); break;
            case JsonValueKind.Null: ips = new(); break;
            case JsonValueKind.Array:
                ips = new();
                foreach (var item in input.AllowedIps.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) return "allowedIps must be an array of strings or null.";
                    ips.Add(item.GetString() ?? "");
                }
                break;
            default: return "allowedIps must be an array of strings or null.";
        }

        access = new ResolvedAccess(host, headers, require, ips);
        return null;
    }

    private static (HeaderPair? Pair, string? Error) ReadPair(JsonElement item, string field)
    {
        if (item.ValueKind != JsonValueKind.Object) return (null, $"{field}: every entry must be an object with a name and a value.");
        if (!item.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
            return (null, $"{field}: every entry needs a \"name\" (a string).");
        var value = "";
        if (item.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null)
        {
            if (v.ValueKind != JsonValueKind.String) return (null, $"{field}: \"value\" must be a string.");
            value = v.GetString() ?? "";
        }
        return (new HeaderPair { Name = name.GetString()!.Trim(), Value = value }, null);
    }

    private static List<HeaderPair> Clone(List<HeaderPair>? headers) =>
        headers?.Select(h => new HeaderPair { Name = h.Name, Value = h.Value }).ToList() ?? new();
}
