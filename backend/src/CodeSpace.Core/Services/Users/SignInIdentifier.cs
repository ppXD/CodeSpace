namespace CodeSpace.Core.Services.Users;

/// <summary>
/// What sign-in compares, in one place: an address and a display name are both identifiers, matched trimmed and
/// case-insensitively.
///
/// <para>Shared rather than repeated because the check that keeps a new display name unambiguous has to compare
/// exactly what sign-in will later compare. A uniqueness rule computed one way and a lookup computed another is a
/// name that passes the first and still collides in the second.</para>
/// </summary>
public static class SignInIdentifier
{
    public static string Normalize(string identifier) => identifier.Trim().ToLowerInvariant();

    /// <summary>
    /// The line between the two identifier spaces: with '@' it is an address, without it a display name. Sign-in
    /// looks an identifier up only in the space its shape names, no display name may contain '@', and no invitation
    /// may be addressed without one — so a name can never stand in for somebody's address, nor an address for
    /// somebody's name.
    /// </summary>
    public static bool LooksLikeEmail(string identifier) => identifier.Contains('@');
}
