using System.Reflection;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcat.Pages;

public class IndexModel : PageModel
{
    public string Version { get; private set; } = "";

    public void OnGet()
    {
        Version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "local";
    }
}
