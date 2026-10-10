using System.Net;
using Microsoft.AspNetCore.Html;

namespace Matcad.Controls;

/// <summary>
/// Helpers to build table cells safely. Text is HTML-encoded; the markup
/// helpers compose from already-encoded pieces. Used by the list pages to fill
/// <see cref="DataRow"/> cells.
/// </summary>
public static class Cell
{
    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    public static IHtmlContent Text(string? value) => new HtmlString(E(value));

    public static IHtmlContent Muted(string? value) =>
        new HtmlString($"<span class=\"text-muted\">{E(value)}</span>");

    public static IHtmlContent Raw(string html) => new HtmlString(html);

    public static IHtmlContent Badge(string text, string kind = "muted") =>
        new HtmlString($"<span class=\"badge badge-{kind}\">{E(text)}</span>");

    /// <summary>The "Managed by Mattiverse" badge for an object that another tool writes through the
    /// API (config field <c>ManagedBy</c>). Empty when the object is not managed; other values show
    /// as "Managed by &lt;value&gt;".</summary>
    public static IHtmlContent ManagedBy(string? managedBy) => new HtmlString(ManagedByMarkup(managedBy));

    /// <summary>A text cell followed by the managed-by badge (if any).</summary>
    public static IHtmlContent TextManaged(string? value, string? managedBy)
    {
        var badge = ManagedByMarkup(managedBy);
        return new HtmlString(badge.Length == 0 ? E(value) : $"{E(value)} {badge}");
    }

    private static string ManagedByMarkup(string? managedBy)
    {
        var by = managedBy?.Trim();
        if (string.IsNullOrEmpty(by)) return "";
        var name = by.Equals("mattiverse", StringComparison.OrdinalIgnoreCase) ? "Mattiverse" : by;
        return $"<span class=\"badge badge-info\" title=\"{E($"Created by {name}. Manual changes may be overwritten.")}\">" +
               $"{E($"Managed by {name}")}</span>";
    }

    /// <summary>A text link.</summary>
    public static IHtmlContent Link(string href, string text) =>
        new HtmlString($"<a href=\"{E(href)}\">{E(text)}</a>");

    /// <summary>An external link that opens in a new tab.</summary>
    public static IHtmlContent ExternalLink(string href, string text) =>
        new HtmlString($"<a href=\"{E(href)}\" target=\"_blank\" rel=\"noopener\">{E(text)}</a>");
}
