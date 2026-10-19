using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Failures;

/// <summary>
/// Which provider failure one cell of a run — a node at an iteration key — is recorded as having failed on. The engine asks it when
/// a container (a map, a loop) ends on the unhandled failure of a node in its body, so the container's own <c>node.failed</c> record
/// can carry the failure of the cell that ended it: <see cref="IRunFailureReader"/> answers only for the node that ended the RUN,
/// and for a run that a container ended that node is the container, which spawned no agent run and threw nothing itself. A rerun asks it
/// too, for each failed cell it replays into its fork, so the fork's record of the cell carries what the original run's did.
/// </summary>
public interface ICellFailureReader
{
    /// <summary>
    /// The failure the cell recorded, or null: it recorded none, or it did not fail on a provider connection. What the cell's own latest
    /// <c>node.failed</c> record carries wins; failing that, the account of the agent run that last worked in that very cell — never another
    /// iteration's, since a branch that failed on a sentence alone says nothing of its sibling's agent run.
    /// </summary>
    Task<PersistedFailure?> ReadAsync(Guid runId, Guid teamId, string nodeId, string iterationKey, CancellationToken cancellationToken);
}
