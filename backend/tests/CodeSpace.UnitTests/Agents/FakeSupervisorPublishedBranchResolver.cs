using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Test double for <see cref="ISupervisorPublishedBranchResolver"/>: always resolves to empty. Used by pure
/// turn-loop unit tests that construct <see cref="SupervisorTurnService"/> directly and never touch real
/// Postgres — DC-3's terminal-output enrichment reads through this whenever a scenario's stop finds no
/// merge-derived branch, so it must return a real (empty) result rather than a <c>null!</c> placeholder.
/// </summary>
internal sealed class FakeSupervisorPublishedBranchResolver : ISupervisorPublishedBranchResolver
{
    public Task<IReadOnlyList<SupervisorRepositoryBranch>> ResolveAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<SupervisorPriorDecision> priorDecisions, Guid? primaryRepositoryId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SupervisorRepositoryBranch>>(Array.Empty<SupervisorRepositoryBranch>());
}
