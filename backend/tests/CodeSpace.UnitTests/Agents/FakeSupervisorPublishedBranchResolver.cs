using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Test double for <see cref="ISupervisorPublishedBranchResolver"/>: mirrors the REAL resolver's tape-first reads
/// (<see cref="SupervisorOutcome.ReadFinalRepositoryBranches"/> / <see cref="SupervisorOutcome.ReadFinalIntegratedBranch"/>
/// — both PURE, no DB) so a pure turn-loop unit test's merge-derived scenario resolves identically through this
/// double; only the ledger-direct fallback (which needs a real <c>PublishManifest</c> + <c>Repository</c> DB read)
/// stays empty, since no unit test here seeds a manifest for this resolver to find. Used by pure turn-loop unit
/// tests that construct <see cref="SupervisorTurnService"/> directly and never touch real Postgres — both DC-3's
/// terminal-output enrichment AND DC-2d's stop-acceptance target resolution read through this.
/// </summary>
internal sealed class FakeSupervisorPublishedBranchResolver : ISupervisorPublishedBranchResolver
{
    public Task<IReadOnlyList<SupervisorRepositoryBranch>> ResolveAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<SupervisorPriorDecision> priorDecisions, Guid? primaryRepositoryId, CancellationToken cancellationToken)
    {
        var repositoryBranches = SupervisorOutcome.ReadFinalRepositoryBranches(priorDecisions);

        if (repositoryBranches.Count > 0) return Task.FromResult(repositoryBranches);

        var integratedBranch = SupervisorOutcome.ReadFinalIntegratedBranch(priorDecisions);

        if (string.IsNullOrEmpty(integratedBranch) || primaryRepositoryId is null)
            return Task.FromResult<IReadOnlyList<SupervisorRepositoryBranch>>(Array.Empty<SupervisorRepositoryBranch>());

        return Task.FromResult<IReadOnlyList<SupervisorRepositoryBranch>>(new[]
        {
            new SupervisorRepositoryBranch { RepositoryId = primaryRepositoryId, Alias = "primary", SourceBranch = integratedBranch, TargetBranch = "" },
        });
    }
}
