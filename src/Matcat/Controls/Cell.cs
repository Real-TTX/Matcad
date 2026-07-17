using System.Net;
using Microsoft.AspNetCore.Html;

namespace Matcat.Controls;

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

    /// <summary>A text link.</summary>
    public static IHtmlContent Link(string href, string text) =>
        new HtmlString($"<a href=\"{E(href)}\">{E(text)}</a>");

    /// <summary>An external link that opens in a new tab.</summary>
    public static IHtmlContent ExternalLink(string href, string text) =>
        new HtmlString($"<a href=\"{E(href)}\" target=\"_blank\" rel=\"noopener\">{E(text)}</a>");
}
