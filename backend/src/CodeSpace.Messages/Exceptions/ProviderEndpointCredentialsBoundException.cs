using CodeSpace.Messages.Failures;

namespace CodeSpace.Messages.Exceptions;

/// <summary>
/// Thrown when an edit moves a git provider connection's endpoint — the scheme, host or port of its API or base URL —
/// while credentials are still connected through it. A connected token is write-only: nobody can read it back, so the
/// only thing that could carry it to a new host is a repoint that kept it, and every repository read, git node, webhook
/// registration and OAuth refresh after that would present it there. The tokens belong to the members who connected
/// them, so the editor cannot re-enter them the way a model key is re-entered; remedy: disconnect them first (each
/// member reconnects against the new host), or add the new host as a provider of its own.
/// </summary>
public sealed class ProviderEndpointCredentialsBoundException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.PreconditionRequired;

    public string Code => FailureCodes.ProviderEndpointCredentialsBound;

    public IReadOnlyDictionary<string, object?>? Details => new Dictionary<string, object?> { ["providerInstanceId"] = ProviderInstanceId, ["boundCredentialCount"] = BoundCredentialCount };

    public Guid ProviderInstanceId { get; }

    public int BoundCredentialCount { get; }

    public ProviderEndpointCredentialsBoundException(Guid providerInstanceId, int boundCredentialCount)
        : base($"Changing this provider's host (its scheme, host or port) is refused while {boundCredentialCount} connected credential(s) would follow it — a connected token is only ever sent to the host it was connected for. Disconnect them first (members reconnect against the new host), or add the new host as its own provider.")
    {
        ProviderInstanceId = providerInstanceId;
        BoundCredentialCount = boundCredentialCount;
    }
}
