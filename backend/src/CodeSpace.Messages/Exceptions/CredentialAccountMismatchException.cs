using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when a credential is asked to take an authorization that belongs to a different provider account than the one it
/// was connected as. A credential is one person's connection of one provider account, and everything bound through it acts
/// as that account; putting another account's token into the same row would change who those repositories, hooks and
/// act-as writes are attributed to without anyone having said so. Signing in as another account is a new connection.
/// </summary>
public sealed class CredentialAccountMismatchException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Conflict;

    public string Code => FailureCodes.CredentialAccountMismatch;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["credentialId"] = CredentialId, ["expectedAccount"] = ExpectedAccount, ["presentedAccount"] = PresentedAccount };

    public Guid CredentialId { get; }

    public string ExpectedAccount { get; }

    public string PresentedAccount { get; }

    public CredentialAccountMismatchException(Guid credentialId, string displayName, string expectedAccount, string presentedAccount)
        : base($"The credential '{displayName}' belongs to the {expectedAccount} account, and the authorization you gave is for {presentedAccount}. Re-authorizing means the same account signing in again; connect {presentedAccount} as its own credential instead, or sign in as {expectedAccount}.")
    {
        CredentialId = credentialId;
        ExpectedAccount = expectedAccount;
        PresentedAccount = presentedAccount;
    }
}
