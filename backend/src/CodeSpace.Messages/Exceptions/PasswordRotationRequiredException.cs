namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when the caller is authenticated but their password is flagged for rotation.
/// Maps to HTTP 403 with code <c>password_rotation_required</c>; the SPA branches on the
/// code and routes the user to /change-password.
/// </summary>
public sealed class PasswordRotationRequiredException : Exception
{
    public PasswordRotationRequiredException() : base("Password rotation required before continuing.") { }
}
