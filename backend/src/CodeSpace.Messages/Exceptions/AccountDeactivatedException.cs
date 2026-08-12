using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Sign-in refused because the account is switched off.
///
/// <para>Distinct from wrong credentials, and deliberately so: telling someone their password was
/// right but their account is off is the difference between them contacting an admin and them
/// resetting a password that was never the problem. It leaks that the address exists — but a
/// deactivated account is one an admin already knows about, and the alternative is a person locked
/// out with no idea why.</para>
/// </summary>
public sealed class AccountDeactivatedException : Exception, IFailure
{
    public AccountDeactivatedException() : base("The account is deactivated.") { }

    public FailureKind Kind => FailureKind.Forbidden;

    public string Code => FailureCodes.AccountDeactivated;

    public string? ClientMessage => "This account has been deactivated. Ask an administrator to restore it.";
}
