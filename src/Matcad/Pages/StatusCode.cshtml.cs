using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages;

/// <summary>Explains an error status (404, 403, ...) with a page of the app; the status of the original request stays.</summary>
[IgnoreAntiforgeryToken]
public class StatusCodeModel : PageModel
{
    public string Title { get; private set; } = "Something went wrong";
    public string Message { get; private set; } = "The request could not be processed.";

    public void OnGet(int? code) =>
        (Title, Message) = code switch
        {
            404 => ("Not found", "This page does not exist, or the link no longer works."),
            403 => ("Access denied", "You do not have the permissions to open this page."),
            400 => ("Bad request", "The request was not understood."),
            _ => ("Something went wrong", "The request could not be processed.")
        };
}
