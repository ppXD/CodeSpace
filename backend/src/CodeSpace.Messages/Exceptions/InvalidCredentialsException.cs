using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Throw when sign-in fails for any reason (user not found, wrong password, soft-deleted,
/// missing password hash). The message is deliberately generic — exposing which side of
/// the pair was wrong gives attackers an email-enumeration oracle.
/// </summary>
public sealed class InvalidCredentialsException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Unauthenticated;

    public string Code => FailureCodes.InvalidCredentials;

    /// <summary>Never says which half was wrong — a distinct message for an unknown email is an enumeration oracle.</summary>
    public string? ClientMessage => "Invalid email or password.";

    public InvalidCredentialsException() : base("Invalid email or password.") { }
}
