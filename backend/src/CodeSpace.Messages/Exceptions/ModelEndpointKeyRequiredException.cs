using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when an update moves a model credential's endpoint — its base URL's scheme, host or port — without entering
/// the API key again. A stored key is write-only: nobody can read it back, so the only thing that could carry it to a new
/// host is a repoint that kept it, and every reflection, probe and run after that would present it there. Remedy: send
/// the key in the same update as the new endpoint (or leave the endpoint where it is). A path-only change, or a keyless
/// credential, needs no key.
/// </summary>
public sealed class ModelEndpointKeyRequiredException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Invalid;

    public string Code => FailureCodes.ModelEndpointKeyRequired;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["credentialId"] = CredentialId };

    public Guid CredentialId { get; }

    public ModelEndpointKeyRequiredException(Guid credentialId)
        : base("Changing this credential's endpoint (its scheme, host or port) needs the API key entered again in the same update — a stored key is only ever sent to the endpoint it was entered for.")
    {
        CredentialId = credentialId;
    }
}
