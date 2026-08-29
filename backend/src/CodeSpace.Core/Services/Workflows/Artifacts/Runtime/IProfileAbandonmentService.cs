using CodeSpace.Core.DependencyInjection;
using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// Closes the records a storage profile still holds, one bounded batch at a time, for a destination that can no
/// longer serve them.
///
/// <para>Bounded and repeatable rather than one long job: each placement is its own fenced claim-prove-settle, so a
/// call that dies halfway leaves every placement it did not reach exactly as it was, and the next call continues.
/// That makes resumption a property of the ledger rather than of a job row nobody would reconcile.</para>
///
/// <para>It proves nothing itself. Every placement is settled only if a live HEAD says the destination cannot serve
/// it, which is <c>IArtifactCasPurgeCoordinator.AbandonAsync</c>'s job — this service decides WHICH placements to
/// ask about, never whether the answer was good enough.</para>
/// </summary>
public interface IProfileAbandonmentService : IScopedDependency
{
    Task<ProfileAbandonmentSummary> AbandonAsync(Guid teamId, Guid actorId, Guid profileId, int batchSize, CancellationToken cancellationToken);
}
