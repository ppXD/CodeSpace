using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when the caller is authenticated but their password is flagged for rotation.
/// Maps to HTTP 403 with code <c>password_rotation_required</c>; the SPA branches on the
/// code and routes the user to /change-password.
/// </summary>
public sealed class PasswordRotationRequiredException : Exception, IFailure
{
    // Forbidden, not PreconditionRequired: this blocks EVERY request until the password is rotated,
    // rather than naming a remedy for this one. Clients tell it apart by Code, which is why the
    // status can stay the 403 they already branch on.
    public FailureKind Kind => FailureKind.Forbidden;

    public string Code => FailureCodes.PasswordRotationRequired;

    public string? ClientMessage => "Your password must be changed before continuing.";

    public PasswordRotationRequiredException() : base("Password rotation required before continuing.") { }
}
