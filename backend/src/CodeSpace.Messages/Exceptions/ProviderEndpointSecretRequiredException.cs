using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when an edit moves a git provider connection's endpoint — the scheme, host or port of its API or base URL —
/// without entering its stored OAuth client secret again. The secret is write-only and is sent with every token
/// exchange, refresh and revocation, to endpoints derived from the base URL; kept across a repoint, it would go to the
/// new host on the next sign-in. Remedy: send the client secret in the same update as the new endpoint (an OAuth app
/// belongs to one host, so this is normally the new host's app's secret, with its client ID).
/// </summary>
public sealed class ProviderEndpointSecretRequiredException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Invalid;

    public string Code => FailureCodes.ProviderEndpointSecretRequired;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["providerInstanceId"] = ProviderInstanceId };

    public Guid ProviderInstanceId { get; }

    public ProviderEndpointSecretRequiredException(Guid providerInstanceId)
        : base("Changing this provider's host (its scheme, host or port) needs the OAuth client secret entered again in the same update — a stored secret is only ever sent to the host it was entered for.")
    {
        ProviderInstanceId = providerInstanceId;
    }
}
