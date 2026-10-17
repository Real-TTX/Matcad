using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Matcad.Controls;

/// <summary>
/// The button row under the content of a form or a list (rules FORM-01, FORM-02): the control puts the buttons in order, positive to negative
/// (primary, then secondary and ghost, the spacer, then danger), whatever the order of the markup. Put a
/// <c>&lt;span class="mat-btn-row__spacer"&gt;&lt;/span&gt;</c> between them where the destructive button should keep its distance.
/// </summary>
[HtmlTargetElement("mat-form-buttons")]
public sealed class FormButtonsTagHelper : TagHelper
{
    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var child = await output.GetChildContentAsync();

        output.TagName = "div";
        var existingClass = output.Attributes["class"]?.Value?.ToString();
        output.Attributes.SetAttribute("class", string.IsNullOrEmpty(existingClass) ? "mat-btn-row" : $"mat-btn-row {existingClass}");
        output.Content.SetHtmlContent(child);
    }
}
