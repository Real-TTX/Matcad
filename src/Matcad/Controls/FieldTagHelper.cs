using System.Net;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Matcad.Controls;

/// <summary>
/// A form field of the toolkit: label above, the control (the child content), hint below and the error message at the field.
/// The space for the error line is always there, so the form does not jump when a message appears (rule FORM-03).
/// <code>
/// &lt;mat-field label="Name" asp-for="Input.Name" hint="Shown in the list" required&gt;
///     &lt;input asp-for="Input.Name" class="mat-input" /&gt;
/// &lt;/mat-field&gt;
/// </code>
/// </summary>
[HtmlTargetElement("mat-field")]
public sealed class FieldTagHelper : TagHelper
{
    public string? Label { get; set; }

    public string? Hint { get; set; }

    /// <summary>Marks the field as required with the star of the toolkit; the page's own checks decide what is valid.</summary>
    public bool Required { get; set; }

    /// <summary>The id of the control, for a field that is not bound with <c>asp-for</c>.</summary>
    [HtmlAttributeName("for-id")]
    public string? ForId { get; set; }

    /// <summary>Bind with <c>asp-for</c> to point the label at the control and to show the error the server found for it.</summary>
    [HtmlAttributeName("asp-for")]
    public ModelExpression? For { get; set; }

    [HtmlAttributeNotBound]
    [ViewContext]
    public ViewContext ViewContext { get; set; } = default!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var child = await output.GetChildContentAsync();

        var id = For is null ? ForId : TagBuilder.CreateSanitizedId(ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name), "_");
        var error = For is null ? null : FirstError(ViewContext.ViewData.ModelState, ViewContext.ViewData.TemplateInfo.GetFullHtmlFieldName(For.Name));

        var classes = "mat-field";
        if (error is not null) classes += " is-invalid";
        if (output.Attributes["class"]?.Value?.ToString() is { Length: > 0 } extra) classes += " " + extra;

        output.TagName = "div";
        output.Attributes.SetAttribute("class", classes);

        var builder = new HtmlContentBuilder();
        if (!string.IsNullOrEmpty(Label))
        {
            var forAttr = id is null ? string.Empty : $" for=\"{id}\"";
            var star = Required ? " <span class=\"mat-req\" aria-hidden=\"true\">*</span>" : string.Empty;
            builder.AppendHtml($"<label class=\"mat-label\"{forAttr}>{WebUtility.HtmlEncode(Label)}{star}</label>");
        }

        builder.AppendHtml(child);

        if (!string.IsNullOrEmpty(Hint))
            builder.AppendHtml($"<span class=\"mat-hint\">{WebUtility.HtmlEncode(Hint)}</span>");

        var errorId = id is null ? string.Empty : $" id=\"{id}-err\"";
        builder.AppendHtml($"<span class=\"mat-error\"{errorId} aria-live=\"polite\">{WebUtility.HtmlEncode(error)}</span>");

        output.Content.SetHtmlContent(builder);
    }

    private static string? FirstError(ModelStateDictionary state, string key)
        => state.TryGetValue(key, out var entry) && entry.Errors.Count > 0 ? entry.Errors[0].ErrorMessage : null;
}
