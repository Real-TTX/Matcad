namespace Matcad.Services;

/// <summary>A person MatIdentity signed in, as the services see it (the claims <c>sub</c>, <c>email</c>, <c>name</c>, <c>preferred_username</c>, <c>role</c>).</summary>
public sealed record ExternalIdentity(string Subject, string? Email, string? Name, string? PreferredUserName, string? Role);

/// <summary>
/// The outcome of <see cref="AuthService.SignInExternal"/>: the user to sign in (<see cref="UserId"/>), or the account that is linked only after its
/// password (<see cref="LinkUserId"/>), or a refusal (<see cref="Error"/>).
/// </summary>
public sealed record ExternalSignIn(long? UserId, long? LinkUserId = null, string? Error = null)
{
    public static ExternalSignIn Refused(string error) => new(null, null, error);
}
