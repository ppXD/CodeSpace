using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Users;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Auth;

/// <summary>
/// Rotates the caller's password. Verifies <c>CurrentPassword</c> against the stored
/// hash, persists <c>NewPassword</c>, and clears the password_must_change flag.
///
/// Tagged <see cref="IBypassPasswordRotationGuard"/> because this is the one command
/// that must run even while the user's rotation flag is set.
/// </summary>
public sealed record ChangePasswordCommand : ICommand<ChangePasswordResponse>, IRequireAuthenticatedUser, IBypassPasswordRotationGuard
{
    public required string CurrentPassword { get; init; }
    public required string NewPassword { get; init; }
}

public sealed record ChangePasswordResponse
{
    public required MeResponse User { get; init; }

    /// <summary>
    /// A token minted under the NEW security stamp. Changing a password ends every session for the
    /// account, including the one that asked — without handing this back, the act of rotating would
    /// sign the rotator out.
    /// </summary>
    public required string Token { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}
