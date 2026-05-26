namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Throw when sign-in fails for any reason (user not found, wrong password, soft-deleted,
/// missing password hash). The message is deliberately generic — exposing which side of
/// the pair was wrong gives attackers an email-enumeration oracle.
/// </summary>
public sealed class InvalidCredentialsException : Exception
{
    public InvalidCredentialsException() : base("Invalid email or password.") { }
}
