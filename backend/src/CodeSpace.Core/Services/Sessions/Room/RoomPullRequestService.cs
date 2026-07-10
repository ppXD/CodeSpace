using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <inheritdoc cref="IRoomPullRequestService"/>
/// <remarks>
/// Resolves WHAT to open a PR against off the SAME reader the I3 publish gate + the Room's own publish-state
/// projection use (<see cref="ISupervisorPublishedBranchResolver"/>, DC-2 — merge-derived OR ledger-direct) — never
/// a second, competing notion of "the run's branch". Idempotent PER (alias, branch): a repeat call for an alias
/// whose recorded <see cref="PublishManifest.Branch"/> matches the CURRENT target reuses that PR rather than
/// opening a duplicate — but a later turn's merge advancing the same alias to a genuinely different (turn-scoped)
/// branch is treated as needing a FRESH PR, never silently pointed at the earlier turn's now-abandoned one. A
/// single-repo run is folded into the SAME multi-repo <see cref="IChangeSetService"/> path as a one-element set —
/// one honesty-invariant code path for both shapes, not two.
/// </remarks>
public sealed class RoomPullRequestService : IRoomPullRequestService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ISupervisorDecisionLog _ledger;
    private readonly IPublishManifestStore _manifests;
    private readonly ISupervisorPublishedBranchResolver _publishedBranches;
    private readonly IChangeSetService _changeSets;

    public RoomPullRequestService(CodeSpaceDbContext db, ISupervisorDecisionLog ledger, IPublishManifestStore manifests, ISupervisorPublishedBranchResolver publishedBranches, IChangeSetService changeSets)
    {
        _db = db;
        _ledger = ledger;
        _manifests = manifests;
        _publishedBranches = publishedBranches;
        _changeSets = changeSets;
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

        var targets = await ResolveTargetsAsync(priorDecisions, workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        if (targets.Count == 0)
            throw new InvalidOperationException("This run has no published branch to open a pull request from.");

        var degraded = targets.Where(t => t.RepositoryId is null)
            .Select(t => new RoomPullRequestOpened { Alias = t.Alias, Disposition = RoomPullRequestDisposition.Failed, Error = "no resolvable repository id for this repo" })
            .ToList();

        var resolvable = targets.Where(t => t.RepositoryId is not null).ToList();

        var existing = await _manifests.ListForWorkflowRunAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);
        var openedByAlias = existing.Where(m => m.Kind == PublishManifestKind.Integration && m.PullRequestUrl is { Length: > 0 }).ToDictionary(m => m.RepositoryAlias);

        // The idempotency key is (alias, branch) — NOT alias alone. An integration branch is turn-scoped
        // (codespace/integration/{runId}/turn{N}), so a LATER merge in the SAME run genuinely produces a
        // DIFFERENT branch for the SAME alias; reusing an earlier turn's PR here would silently point the user at
        // abandoned work while claiming the run's CURRENT frontier is already handled. A branch mismatch is
        // treated as "needs opening" — the upsert below overwrites the stale row with the fresh branch + PR.
        bool AlreadyOpenForCurrentBranch(SupervisorRepositoryBranch t) => openedByAlias.TryGetValue(t.Alias, out var manifest) && manifest.Branch == t.SourceBranch;

        var alreadyOpened = resolvable.Where(AlreadyOpenForCurrentBranch)
            .Select(t => ProjectAlreadyOpened(t, openedByAlias[t.Alias]))
            .ToList();

        var toOpen = resolvable.Where(t => !AlreadyOpenForCurrentBranch(t)).ToList();

        if (toOpen.Count == 0)
            return new RoomPullRequestResult { PullRequests = degraded.Concat(alreadyOpened).ToList() };

        var (title, body) = SupervisorOutcome.DeriveTitleAndBody(priorDecisions);

        var spec = new ChangeSetPullRequestSpec
        {
            Title = title,
            Body = body,
            Repositories = toOpen.Select(t => new ChangeSetPullRequest { RepositoryId = t.RepositoryId!.Value, SourceBranch = t.SourceBranch, TargetBranch = t.TargetBranch }).ToList(),
        };

        var result = await _changeSets.OpenPullRequestsAsync(teamId, spec, actorUserId, cancellationToken).ConfigureAwait(false);

        var freshlyOpened = new List<RoomPullRequestOpened>(toOpen.Count);

        for (var i = 0; i < toOpen.Count; i++)
            freshlyOpened.Add(await ProjectAndRecordAsync(workflowRunId, teamId, toOpen[i], result.PullRequests[i], cancellationToken).ConfigureAwait(false));

        return new RoomPullRequestResult { PullRequests = degraded.Concat(alreadyOpened).Concat(freshlyOpened).ToList() };
    }

    /// <summary>The run's published repositories to open a PR against — multi-repo's per-repo heads, the single-repo integrated branch, or (DC-2) a ledger-direct pushed branch with no merge at all. Empty (not thrown) when the run published NOTHING — the caller decides whether that's an error.</summary>
    private Task<IReadOnlyList<SupervisorRepositoryBranch>> ResolveTargetsAsync(IReadOnlyList<SupervisorPriorDecision> priorDecisions, Guid workflowRunId, Guid teamId, CancellationToken cancellationToken) =>
        _publishedBranches.ResolveAsync(workflowRunId, teamId, priorDecisions, cancellationToken);

    private static RoomPullRequestOpened ProjectAlreadyOpened(SupervisorRepositoryBranch target, PublishManifest manifest) => new()
    {
        RepositoryId = target.RepositoryId,
        Alias = target.Alias,
        Disposition = RoomPullRequestDisposition.AlreadyOpened,
        Number = manifest.PullRequestNumber,
        Url = manifest.PullRequestUrl,
    };

    /// <summary>Project one ChangeSet outcome into the Room's shape, and — only for a genuinely OPENED PR — record it onto the alias's PublishManifest row so a repeat call reuses it (I2: no second, competing notion of "did this alias already get a PR").</summary>
    private async Task<RoomPullRequestOpened> ProjectAndRecordAsync(Guid workflowRunId, Guid teamId, SupervisorRepositoryBranch target, ChangeSetPullRequestOutcome outcome, CancellationToken cancellationToken)
    {
        if (outcome.Disposition != ChangeSetPullRequestDisposition.Opened)
            return new RoomPullRequestOpened
            {
                RepositoryId = target.RepositoryId,
                Alias = target.Alias,
                Disposition = outcome.Disposition == ChangeSetPullRequestDisposition.Skipped ? RoomPullRequestDisposition.Skipped : RoomPullRequestDisposition.Failed,
                Error = outcome.Error,
            };

        await _manifests.UpsertForIntegrationAsync(new PublishManifestUpsert
        {
            TeamId = teamId,
            WorkflowRunId = workflowRunId,
            RepositoryAlias = target.Alias,
            RepositoryId = target.RepositoryId,
            Branch = target.SourceBranch,
            PublishStateValue = PublishState.Pushed,
            PullRequestNumber = outcome.Number,
            PullRequestUrl = outcome.Url,
        }, cancellationToken).ConfigureAwait(false);

        return new RoomPullRequestOpened { RepositoryId = target.RepositoryId, Alias = target.Alias, Disposition = RoomPullRequestDisposition.Opened, Number = outcome.Number, Url = outcome.Url };
    }
}
