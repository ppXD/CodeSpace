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
}
