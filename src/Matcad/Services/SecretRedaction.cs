using System.Text.Json;
using System.Text.Json.Nodes;

namespace Matcad.Services;

/// <summary>
/// Makes a Caddy config fit to show on a page. The config that Caddy runs has to carry the secrets of the access properties of
/// a route (the value a required header is compared with, the values of the headers sent to an upstream), but the Settings page
/// shows that config to read and copy: there the values are replaced, so no page ever prints one.
/// </summary>
public static class SecretRedaction
{
    public const string Hidden = "(hidden)";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The config with the secret values replaced, indented. Text that is not valid JSON is not passed on.</summary>
    public static string Redact(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            Walk(node);
            return node?.ToJsonString(Indented) ?? "";
        }
        catch (JsonException)
        {
            return "// The configuration could not be read, so it is not shown.";
        }
    }

    private static void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                // The guard: { "not": [ { "header": { "X-Name": [ "<secret>" ] } } ] }
                if (obj["not"] is JsonArray nots)
                    foreach (var matcher in nots.OfType<JsonObject>())
                        if (matcher["header"] is JsonObject header) HideAll(header, keep: null);
                // The proxy: { "handler": "reverse_proxy", "headers": { "request": { "set": { "X-Name": [ "<secret>" ] } } } }.
                // The forward-auth call (it has handle_response) only passes on request data, and Host is no secret.
                if (obj["handler"]?.GetValue<string>() == "reverse_proxy" && obj["handle_response"] == null
                    && obj["headers"]?["request"]?["set"] is JsonObject set)
                    HideAll(set, keep: "Host");
                foreach (var child in obj.Select(p => p.Value).ToList()) Walk(child);
                break;
            case JsonArray array:
                foreach (var child in array) Walk(child);
                break;
        }
    }

    private static void HideAll(JsonObject headers, string? keep)
    {
        foreach (var name in headers.Select(p => p.Key).ToList())
            if (!string.Equals(name, keep, StringComparison.OrdinalIgnoreCase))
                headers[name] = new JsonArray(Hidden);
    }
}
