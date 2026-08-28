using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Storage;

/// <summary>
/// Re-asks every destination an Active route currently binds writes to whether it still works.
///
/// <para>No permission marker: this is dispatched by a recurring job on a processing pod, not by a caller with an
/// identity. It reads and probes across every team by design — a per-team capability would make the deployment-wide
/// question unaskable.</para>
/// </summary>
public sealed record ProbeStaleStorageDestinationsCommand : ICommand<ProbeStaleStorageDestinationsResponse>;

public sealed record ProbeStaleStorageDestinationsResponse
{
    public required int DestinationsProbed { get; init; }
}
