using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// The reset token does not name a usable reset — expired, already spent, never real, or issued for
/// an account that has since been switched off.
///
/// <para>One type for all four, for the same reason invitations collapse theirs: distinguishing them
/// tells whoever is guessing which guesses were once real.</para>
/// </summary>
public sealed class PasswordResetNotUsableException : Exception, IFailure
{
    public PasswordResetNotUsableException() : base("The password reset token is not usable.") { }

    public FailureKind Kind => FailureKind.NotFound;

    public string Code => FailureCodes.PasswordResetNotUsable;

    public string? ClientMessage => "This password reset link is no longer valid. Ask for a new one.";
}
