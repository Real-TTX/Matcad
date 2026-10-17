using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Matcad.Controls;

/// <summary>
/// Draws an icon of the toolkit's sprite: <c>&lt;icon name="save" /&gt;</c>. It is the same markup as <see cref="Ui.Icon"/>;
/// size and colour follow the CSS, so icons sit naturally inside buttons and text.
/// </summary>
[HtmlTargetElement("icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed class IconTagHelper : TagHelper
{
    /// <summary>Icon name from <c>matui-icons.svg</c> (for example "save" or "trash").</summary>
    public string Name { get; set; } = string.Empty;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        var existingClass = output.Attributes["class"]?.Value?.ToString();
        output.TagName = null;
        output.Content.SetHtmlContent(Ui.Icon(Name, existingClass));
    }
}
