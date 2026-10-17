using Matcad.Config;

namespace Matcad.Auth;

/// <summary>
/// Sends a signed-in user to the page they have to visit first: a new password (default admin, admin-set
/// passwords), and for the administrator the first-run setup wizard until it is finished.
/// </summary>
public class AccountGuardMiddleware
{
    // Pages the user must still reach: the pages that fix the problem, sign-out, and what the app itself calls
    // (the APIs answer JSON, never a redirect to a page; the callbacks of MatIdentity finish the sign-in and sign-out).
    private static readonly string[] Exempt =
        ["/Setup", "/Account", "/Login", "/Logout", "/Auth/", "/healthz", "/api/", "/Error", "/StatusCode",
         MatPlatform.MatIdentitySignIn.CallbackPath, MatPlatform.MatIdentitySignIn.SignedOutCallbackPath];

    private readonly RequestDelegate _next;
    public AccountGuardMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ConfigStore store)
    {
        var path = context.Request.Path.Value ?? "";
        if (context.User.Identity?.IsAuthenticated == true &&
            !path.Equals("/api", StringComparison.OrdinalIgnoreCase) &&
            !Exempt.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            var setupDone = store.Settings.SetupCompleted;
            if (context.User.MustChangePassword())
            {
                context.Response.Redirect(context.User.IsAdmin() && !setupDone ? "/Setup?step=account" : "/Account/Profile");
                return;
            }
            if (context.User.IsAdmin() && !setupDone)
            {
                context.Response.Redirect("/Setup");
                return;
            }
        }
        await _next(context);
    }
}
