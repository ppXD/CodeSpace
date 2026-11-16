using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// A name without '@' matched more than one live account, and the presented password matched at least
/// one of them. A name is a convenience identifier only while one account carries it; an address
/// remains the account's own identifier.
/// </summary>
public sealed class SignInNameAmbiguousException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Unauthenticated;

    public string Code => FailureCodes.SignInNameAmbiguous;

    public string? ClientMessage =>
        "This name is used by more than one account. Sign in with the email address for the account you want to use.";

    public SignInNameAmbiguousException() : base("The sign-in name is ambiguous.")
    {
    }
}
