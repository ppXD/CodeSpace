using System.Text.Json.Serialization;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Users;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Auth;

/// <summary>
/// Anonymous: no marker interface. Handler accepts either an email or a name in
/// <see cref="Name"/> (case-insensitive match against user.email OR user.name) and
/// mints a JWT on success.
///
/// Carries <see cref="IBypassPasswordRotationGuard"/> because sign-in is meant to be
/// usable even when the caller is presenting a stale JWT for a user still flagged
/// password_must_change. Without the bypass, "I want to sign in fresh" would 403
/// whenever a previous-session token was still in localStorage.
/// </summary>
public sealed record SignInCommand : ICommand<SignInResponse>, IBypassPasswordRotationGuard
{
    /// <summary>Identifier the user typed — may be an email or a display name.</summary>
    public required string Name { get; init; }
    public required string Password { get; init; }
}

public sealed record SignInResponse
{
    /// <summary>
    /// The session, for a caller that keeps it itself — an API client, the CLI. Absent from the wire when the request
    /// was authenticated by the browser's session cookie: the response hands that caller its session as the cookie
    /// alone, because a body is readable by any script running in the page.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public required string? Token { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required MeResponse User { get; init; }
}
