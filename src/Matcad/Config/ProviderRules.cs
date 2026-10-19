using System.Text.RegularExpressions;

namespace Matcad.Config;

/// <summary>
/// The rules for a DNS provider, shared by the pages and the REST API. The type is the name of a Caddy DNS module and the credentials
/// become the keys of the module's JSON object, so both are kept to the characters such a name can have; "name" is the key Caddy uses
/// for the module itself and cannot be a credential.
/// </summary>
public static partial class ProviderRules
{
    /// <summary>The key of the module name in Caddy's DNS provider object.</summary>
    public const string ModuleKey = "name";

    /// <summary>An error message, or null when the type and the credential keys are usable. Never names a value.</summary>
    public static string? Check(string? module, IEnumerable<string> credentialKeys)
    {
        if (string.IsNullOrWhiteSpace(module)) return "Please choose a type (the Caddy DNS module, for example netcup).";
        if (!ModuleName().IsMatch(module.Trim())) return "The DNS module name may only contain a-z, 0-9 and _ (for example cloudflare).";
        foreach (var key in credentialKeys)
        {
            if (!CredentialKey().IsMatch(key)) return "A credential key may only contain letters, digits and _ (for example api_token).";
            if (key.Equals(ModuleKey, StringComparison.OrdinalIgnoreCase)) return "\"name\" is reserved for the module and cannot be a credential.";
        }
        return null;
    }

    /// <summary>Whether a credential of this type is a secret (never shown again). Credentials of a custom module all are.</summary>
    public static bool IsSecret(string module, string key) =>
        ProviderTypes.Find(module)?.Fields.FirstOrDefault(f => f.Key == key)?.Secret ?? true;

    /// <summary>A blank value keeps the stored one of the same key (when the provider keeps its type): the pages never send a secret
    /// back to the browser, so an untouched field arrives empty.</summary>
    public static Dictionary<string, string> KeepStored(string module, Dictionary<string, string> posted, ProviderConfig? stored)
    {
        if (stored == null || !string.Equals(stored.Type, module, StringComparison.OrdinalIgnoreCase)) return posted;
        var result = new Dictionary<string, string>(posted);
        foreach (var (key, value) in posted)
            if (value.Length == 0 && stored.Credentials.TryGetValue(key, out var saved)) result[key] = saved;
        return result;
    }

    [GeneratedRegex("^[a-z0-9_]{1,64}$")]
    private static partial Regex ModuleName();

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$")]
    private static partial Regex CredentialKey();
}
