namespace Matcad.Controls;

/// <summary>Input of the shared DNS-provider form fields (_ProviderFields.cshtml): the type picker and the credentials of each type.</summary>
public class ProviderFieldsModel
{
    /// <summary>Name of the posted form field that carries the chosen type.</summary>
    public string TypeFieldName { get; set; } = "Type";
    public string Type { get; set; } = "";
    public string CustomModule { get; set; } = "";
    /// <summary>Credentials of the known types, keyed by "type:key".</summary>
    public Dictionary<string, string> Values { get; set; } = new();
    /// <summary>Key/value pairs of the custom type.</summary>
    public Dictionary<string, string> CustomCredentials { get; set; } = new();
}
