using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Matcad.Pages.Settings;

/// <summary>
/// The Connections area of the platform contract (admins only, like the whole folder): what Matcad uses (sign-in through MatIdentity), what it
/// offers (<c>web.route</c>: the apps it makes reachable) and how. Drawn by MatPlatform (<c>&lt;mat-connections /&gt;</c>), its forms post to
/// <c>/api/connections/form</c>.
/// </summary>
public class ConnectionsModel : PageModel
{
    public void OnGet()
    {
    }
}
