using Matcad.Config;
using Microsoft.AspNetCore.Http;

namespace Matcad.Controls;

/// <summary>Input of the shared DNS-provider form fields (_ProviderFields.cshtml): the type picker and the credentials of each type.
/// A stored secret is never put into the page: its field stays empty and says that a value is saved (an empty field keeps it).</summary>
public class ProviderFieldsModel
{
    /// <summary>Name of the posted form field that carries the chosen type.</summary>
    public string TypeFieldName { get; set; } = "Type";
    public string Type { get; set; } = "";
    public string CustomModule { get; set; } = "";
    /// <summary>Values to show in the fields of the known types, keyed by "type:key": stored values that are no secret, and what was
    /// just typed when the form comes back (a test or a refused save).</summary>
    public Dictionary<string, string> Values { get; set; } = new();
    /// <summary>"type:key" of the credentials that have a stored value (shown as "Saved - leave empty to keep").</summary>
    public HashSet<string> Saved { get; set; } = new();
    /// <summary>Rows of the custom type: the key, the value to show (only what was just typed) and whether a value is stored.</summary>
    public List<(string Key, string Value, bool Saved)> CustomRows { get; set; } = new();

    /// <summary>The fields for a stored provider: non-secret values shown, secrets only marked as saved.</summary>
    public static ProviderFieldsModel ForStored(string typeFieldName, ProviderConfig? provider, string fallbackType)
    {
        var model = new ProviderFieldsModel { TypeFieldName = typeFieldName, Type = fallbackType };
        if (provider == null) return model;
        if (ProviderTypes.Find(provider.Type) != null)
        {
            model.Type = provider.Type;
            foreach (var (key, value) in provider.Credentials)
            {
                if (value.Length > 0 && ProviderRules.IsSecret(provider.Type, key)) model.Saved.Add($"{provider.Type}:{key}");
                else model.Values[$"{provider.Type}:{key}"] = value;
            }
        }
        else
        {
            model.Type = ProviderTypes.CustomId;
            model.CustomModule = provider.Type;
            model.CustomRows = provider.Credentials.Select(kv => (kv.Key, "", kv.Value.Length > 0)).ToList();
        }
        return model;
    }

    /// <summary>Reads the module name and the credentials out of a posted form (blank values stay blank; see <see cref="ProviderRules.KeepStored"/>).</summary>
    public static (string Module, Dictionary<string, string> Credentials, string? Error) Read(IFormCollection form, string type)
    {
        var credentials = new Dictionary<string, string>();
        if (type == ProviderTypes.CustomId)
        {
            var module = form["CustomModule"].ToString().Trim();
            var keys = form["CustomKeys"];
            var values = form["CustomValues"];
            for (var i = 0; i < keys.Count; i++)
            {
                var key = keys[i]?.Trim();
                if (string.IsNullOrEmpty(key)) continue;
                credentials[key] = i < values.Count ? values[i] ?? "" : "";
            }
            return (module, credentials, ProviderRules.Check(module, credentials.Keys));
        }
        var known = ProviderTypes.Find(type);
        if (known == null) return ("", credentials, "Please choose a type.");
        foreach (var field in known.Fields)
            credentials[field.Key] = form[$"cred_{known.Id}_{field.Key}"].ToString();
        return (known.Id, credentials, null);
    }

    /// <summary>The fields after a POST: what was typed comes back, and what is stored stays marked as saved.</summary>
    public static ProviderFieldsModel ForPosted(string typeFieldName, string type, string module, Dictionary<string, string> posted, ProviderConfig? stored)
    {
        var model = new ProviderFieldsModel { TypeFieldName = typeFieldName, Type = type, CustomModule = type == ProviderTypes.CustomId ? module : "" };
        var sameType = stored != null && string.Equals(stored.Type, module, StringComparison.OrdinalIgnoreCase);
        bool IsSaved(string key) => sameType && stored!.Credentials.TryGetValue(key, out var v) && v.Length > 0;
        if (type == ProviderTypes.CustomId)
            model.CustomRows = posted.Select(kv => (kv.Key, kv.Value, IsSaved(kv.Key))).ToList();
        else
            foreach (var (key, value) in posted)
            {
                if (value.Length > 0 || !ProviderRules.IsSecret(module, key)) model.Values[$"{module}:{key}"] = value;
                else if (IsSaved(key)) model.Saved.Add($"{module}:{key}");
            }
        return model;
    }
}
