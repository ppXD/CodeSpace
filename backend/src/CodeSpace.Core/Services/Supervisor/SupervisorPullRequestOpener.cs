using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// DC-2b — the repo-agnostic "open a PR against this run's published branch(es)" core, extracted out of
/// <see cref="Sessions.Room.RoomPullRequestService"/> (PR-6) so a SECOND caller — the supervisor's own
/// server-authored <c>publish</c> decision (<see cref="SupervisorDeliveryGate"/>) — can open a PR from a LIVE
/// turn context, mirroring how <see cref="ISupervisorPublishedBranchResolver"/> itself grew a
/// <c>primaryRepositoryId</c> parameter in DC-3 for the identical pre-terminal need. Resolves WHAT to open
/// against off the SAME shared resolver both callers already share — never a second, competing notion of "the
/// run's branch" — and never throws for "nothing to open": empty-target and no-resolvable-repository are both
/// ORDINARY results a caller inspects, so a live-turn caller can fold either into a diagnosed outcome instead of
/// crashing the turn. <see cref="Sessions.Room.RoomPullRequestService"/> is the one THROWING caller (its terminal-only,
/// human-facing contract needs a 400, not a silent empty result) — it maps this method's result back into its own
/// specific exceptions.
/// </summary>
public interface ISupervisorPullRequestOpener
{
    /// <param name="primaryRepositoryId">The run's LIVE single configured repository (pre-terminal — <c>context.AgentProfile?.RepositoryId</c>), forwarded to the shared resolver. Null for a post-terminal caller (the resolver then falls back to the terminal <c>OutputsJson</c>).</param>
    /// <param name="targetBranchOverride">DC-2a's <c>DeliverySpec.TargetBranch</c> when the operator (or an approved plan) pinned one — overrides EVERY target's own resolved default branch. Null/blank ⇒ each repo's own default stands (byte-identical to pre-DC-2b).</param>
    /// <param name="currentTurnStopSummary">The rejected stop's OWN summary (<see cref="SupervisorPublishPayload.StopSummary"/>) when this call is DC-2b's live-turn substitution — checked BEFORE scanning the tape for an older persisted stop, since THIS turn's stop was substituted away and never reached <paramref name="priorDecisions"/>. Null for a post-terminal caller (Room), which always finds the run's TRUE final stop on the tape directly.</param>
    Task<RoomPullRequestResult> OpenAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<SupervisorPriorDecision> priorDecisions, Guid? primaryRepositoryId, string? targetBranchOverride, string? currentTurnStopSummary, Guid? actorUserId, CancellationToken cancellationToken);
}

public sealed class SupervisorPullRequestOpener : ISupervisorPullRequestOpener, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IPublishManifestStore _manifests;
    private readonly ISupervisorPublishedBranchResolver _publishedBranches;
    private readonly IChangeSetService _changeSets;

    public SupervisorPullRequestOpener(CodeSpaceDbContext db, IPublishManifestStore manifests, ISupervisorPublishedBranchResolver publishedBranches, IChangeSetService changeSets)
    {
        _db = db;
        _manifests = manifests;
        _publishedBranches = publishedBranches;
        _changeSets = changeSets;
    }

    public async Task<RoomPullRequestResult> OpenAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<SupervisorPriorDecision> priorDecisions, Guid? primaryRepositoryId, string? targetBranchOverride, string? currentTurnStopSummary, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var resolved = await _publishedBranches.ResolveAsync(workflowRunId, teamId, priorDecisions, primaryRepositoryId, cancellationToken).ConfigureAwait(false);

        var targets = string.IsNullOrWhiteSpace(targetBranchOverride)
            ? resolved
            : resolved.Select(t => t with { TargetBranch = targetBranchOverride }).ToList();

        if (targets.Count == 0) return new RoomPullRequestResult { PullRequests = Array.Empty<RoomPullRequestOpened>() };

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

        var candidates = resolvable.Where(t => !AlreadyOpenForCurrentBranch(t)).ToList();

        if (candidates.Count == 0)
            return new RoomPullRequestResult { PullRequests = degraded.Concat(alreadyOpened).ToList() };

        // DC-2b: the repo-level publish policy override (Repository.PublishMode) — the SAME escape hatch
        // RepositoryPolicyPublishGuard enforces at agent-push time, consulted here too so a protected/compliance
        // repo never gets an auto-opened PR either. A patch-only repo is skipped, not failed — the policy is a
        // deliberate choice, not something a retry or a human re-confirmation could ever change.
        var patchOnlyIds = await PatchOnlyRepositoryIdsAsync(candidates.Select(t => t.RepositoryId!.Value), teamId, cancellationToken).ConfigureAwait(false);

        var patchOnlySkipped = candidates.Where(t => patchOnlyIds.Contains(t.RepositoryId!.Value))
            .Select(t => new RoomPullRequestOpened { RepositoryId = t.RepositoryId, Alias = t.Alias, Disposition = RoomPullRequestDisposition.Skipped, Error = "the repository requires patch-only publishing" })
            .ToList();

        var toOpen = candidates.Where(t => !patchOnlyIds.Contains(t.RepositoryId!.Value)).ToList();

        if (toOpen.Count == 0)
            return new RoomPullRequestResult { PullRequests = degraded.Concat(alreadyOpened).Concat(patchOnlySkipped).ToList() };

        var (title, body) = DeriveTitleAndBody(priorDecisions, currentTurnStopSummary);

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

        return new RoomPullRequestResult { PullRequests = degraded.Concat(alreadyOpened).Concat(patchOnlySkipped).Concat(freshlyOpened).ToList() };
    }

    private async Task<IReadOnlySet<Guid>> PatchOnlyRepositoryIdsAsync(IEnumerable<Guid> repositoryIds, Guid teamId, CancellationToken cancellationToken)
    {
        var ids = repositoryIds.Distinct().ToList();

        if (ids.Count == 0) return new HashSet<Guid>();

        return (await _db.Repository.AsNoTracking()
            .Where(r => ids.Contains(r.Id) && r.TeamId == teamId && r.PublishMode == RepositoryPublishMode.PatchOnly)
            .Select(r => r.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
    }

    private static RoomPullRequestOpened ProjectAlreadyOpened(SupervisorRepositoryBranch target, PublishManifest manifest) => new()
    {
        RepositoryId = target.RepositoryId,
        Alias = target.Alias,
        Disposition = RoomPullRequestDisposition.AlreadyOpened,
        Number = manifest.PullRequestNumber,
        Url = manifest.PullRequestUrl,
    };

    /// <summary>
    /// Project one ChangeSet outcome into the shared shape, and — only for a genuinely OPENED PR — record it onto
    /// the alias's PublishManifest row so a repeat call reuses it (I2: no second, competing notion of "did this
    /// alias already get a PR"). DC-2c: the crash window this comment used to flag (provider create succeeds,
    /// but a crash before THIS upsert lands strands a real PR every retry then reports Failed against) is closed
    /// at the PROVIDER layer instead of here — <c>GitHubRepositoryProvider</c>/<c>GitLabRepositoryProvider</c>'s
    /// own <c>OpenPullRequestAsync</c> now binds to an already-existing PR/MR for the same branch on a duplicate
    /// create failure, so a retry's outcome still reports <c>Opened</c> and reaches this upsert.
    /// </summary>
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

    /// <summary>
    /// Title = the stop summary's first line; body = the summary in full. The summary source is CHECKED FIRST as
    /// <paramref name="currentTurnStopSummary"/> (DC-2b's live-turn substitution — the model's stop for THIS turn
    /// was rejected-and-substituted, so it never reaches <paramref name="priorDecisions"/>; see
    /// <see cref="SupervisorPublishPayload.StopSummary"/>'s doc), falling back to the LATEST persisted stop on the
    /// tape otherwise (Room's post-terminal call, which always finds the run's TRUE final stop there). Falls back
    /// to a generic title/no body when NEITHER source has one (e.g. a server-forced stop with no summary at all).
    /// Internal (not private) so the framing is unit-pinned directly (InternalsVisibleTo), not only through
    /// integration coverage.
    /// </summary>
    internal static (string Title, string? Body) DeriveTitleAndBody(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string? currentTurnStopSummary = null)
    {
        var summary = currentTurnStopSummary;

        if (string.IsNullOrWhiteSpace(summary))
        {
            var stop = priorDecisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Stop);
            summary = stop is null ? null : SupervisorOutcome.ReadStopSummary(stop.OutcomeJson);
        }

        if (string.IsNullOrWhiteSpace(summary)) return ("Merge agent changes", null);

        var firstLine = summary.Split('\n', 2)[0].Trim();
        var title = firstLine.Length > 100 ? firstLine[..100].TrimEnd() : firstLine;

        return (title.Length > 0 ? title : "Merge agent changes", summary);
    }
}
