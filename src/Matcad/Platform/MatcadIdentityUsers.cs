using System.Globalization;
using Matcad.Auth;
using Matcad.Services;
using MatPlatform;

namespace Matcad.Platform;

/// <summary>
/// Matcad's side of the sign-in through MatIdentity (MatPlatform <see cref="IMatIdentityUsers"/>): the person is mapped to a local user by
/// <see cref="AuthService.SignInExternal"/> (by <c>sub</c>, else a new user; the claim <c>role</c> when it is there) and signed in with
/// Matcad's own session, keeping the ID token for the sign-out. An unlinked account whose user name is the person's address is not entered:
/// the answer is <see cref="MatIdentitySignInResult.LinkRequired"/> and the sign-in page links it after its password.
/// The session keeps MatIdentity's <c>sid</c>; its back-channel logout (<see cref="IMatIdentitySessions"/>) deletes the matching sessions
/// (<see cref="AuthService.EndExternalSessions"/>), so their next request is signed out.
/// </summary>
public sealed class MatcadIdentityUsers(AuthService auth) : IMatIdentityUsers, IMatIdentitySessions
{
    public Task EndSessionsAsync(string? subject, string? sessionId, CancellationToken ct) => auth.EndExternalSessions(subject, sessionId, ct);

    public async Task<MatIdentitySignInResult> SignInAsync(MatIdentityPerson person, HttpContext http, CancellationToken ct)
    {
        var result = await auth.SignInExternal(
            new ExternalIdentity(person.Subject, person.Email, person.Name, person.PreferredUserName, person.Role));

        if (result.LinkUserId is { } linkUserId)
            return MatIdentitySignInResult.LinkRequired(linkUserId.ToString(CultureInfo.InvariantCulture));

        if (result.UserId is not { } userId || await auth.GetUser(userId) is not { } user)
            return MatIdentitySignInResult.Refused(result.Error ?? "refused");

        var session = await auth.CreateSession(user, person.IdToken, person.SessionId);
        SessionCookie.Append(http, session);
        return MatIdentitySignInResult.Success();
    }
}
