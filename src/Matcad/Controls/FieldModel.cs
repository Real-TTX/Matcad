namespace Matcad.Controls;

/// <summary>
/// Model for the reusable form-field control (_Field.cshtml). Gives every CRUD
/// form the same label / input / hint layout. Supports text-like inputs,
/// checkboxes, and single-select dropdowns.
/// </summary>
public class FieldModel
{
    public string Name { get; set; } = "";
    public string Label { get; set; } = "";
    public string Type { get; set; } = "text"; // text, password, number, email, url, checkbox, select, textarea
    public string? Value { get; set; }
    public bool Checked { get; set; }
    public string? Hint { get; set; }
    public bool Required { get; set; }
    public string? Placeholder { get; set; }
    public bool Autofocus { get; set; }
    /// <summary>Options for Type == "select": value -> text.</summary>
    public List<(string Value, string Text)> Options { get; set; } = new();

    public FieldModel() { }
    public FieldModel(string name, string label, string? value = null, string type = "text")
    {
        Name = name; Label = label; Value = value; Type = type;
    }
}
