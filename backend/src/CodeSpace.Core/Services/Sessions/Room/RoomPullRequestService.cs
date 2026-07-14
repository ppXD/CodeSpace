using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Dtos.Sessions.Room;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <inheritdoc cref="IRoomPullRequestService"/>
/// <remarks>
/// A thin Room-facing wrapper (Rule 16): resolves the run's terminal status + terminal decision tape, then
/// delegates the actual "open a PR against the published branch(es)" work to the shared, repo-agnostic
/// <see cref="ISupervisorPullRequestOpener"/> (DC-2b — extracted so the supervisor's own server-authored
/// <c>publish</c> decision can drive the SAME core from a LIVE turn context). This class owns ONLY the
/// terminal-only entry contract PR-6 established: a 400 when the run is still in progress, or when nothing
/// resolves — never a silent empty result (the opener itself never throws; that contract is THIS caller's own).
/// </remarks>
public sealed class RoomPullRequestService : IRoomPullRequestService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ISupervisorDecisionLog _ledger;
    private readonly ISupervisorPullRequestOpener _opener;
    private readonly ISessionTurnCache _cache;

    public RoomPullRequestService(CodeSpaceDbContext db, ISupervisorDecisionLog ledger, ISupervisorPullRequestOpener opener, ISessionTurnCache cache)
    {
        _db = db;
        _ledger = ledger;
        _opener = opener;
        _cache = cache;
    }

    public async Task<RoomPullRequestResult> OpenAsync(Guid workflowRunId, Guid teamId, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var run = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == workflowRunId && r.TeamId == teamId)
            .Select(r => new { r.Status })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Run not found.");

        // A supervisor run sits Suspended BETWEEN turns even after an earlier turn's merge already pushed a real,
        // durable branch — reading its published branch any earlier would resolve against a frontier that keeps
        // moving turn to turn. Fail loud and specific here rather than let a stale read either crash confusingly
        // or silently under-report.
        if (!WorkflowRunState.IsTerminal(run.Status))
            throw new InvalidOperationException("This run is still in progress — wait for it to finish before opening a pull request.");

        var priorDecisions = await _ledger.GetTerminalDecisionsAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        var result = await _opener.OpenAsync(workflowRunId, teamId, priorDecisions, primaryRepositoryId: null, targetBranchOverride: null, currentTurnStopSummary: null, actorUserId, cancellationToken).ConfigureAwait(false);

        if (result.PullRequests.Count == 0)
        {
            // Sweep-found (DC-3): the resolver returns empty BOTH when nothing was ever published AND when a
            // merge-derived branch genuinely exists but its owning repository couldn't be resolved (a degraded
            // terminal output with no repositoryId despite an integratedBranch) — the pure tape reads still
            // distinguish the two, so the message stays as specific as it was before this reader was unified.
            var publishedButUnresolvable = !string.IsNullOrEmpty(SupervisorOutcome.ReadFinalIntegratedBranch(priorDecisions))
                || SupervisorOutcome.ReadFinalRepositoryBranches(priorDecisions).Count > 0;

            throw new InvalidOperationException(publishedButUnresolvable
                ? "This run's published branch has no resolvable repository."
                : "This run has no published branch to open a pull request from.");
        }

        // Opening a PR mutates the (already-terminal) run's Room block — its publish state + delivery card now carry the
        // opened PR. Evict the cached projection so the next room read recomputes it instead of serving the pre-PR block.
        _cache.Evict(workflowRunId);

        return result;
    }
}
