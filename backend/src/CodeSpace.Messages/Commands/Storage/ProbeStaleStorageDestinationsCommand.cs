using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Storage;

/// <summary>
/// Re-asks the stale destinations an Active route binds writes to, or that still hold stored objects, whether they
/// work — oldest observation first, a bounded number per pass, so a population larger than one pass takes is covered
/// over the passes that follow rather than in one unbounded pass.
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
