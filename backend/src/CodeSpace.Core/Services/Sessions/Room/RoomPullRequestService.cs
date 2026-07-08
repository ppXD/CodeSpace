using System.Text.Json;
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
/// Resolves WHAT to open a PR against off the SAME reader the I3 publish gate uses
/// (<see cref="SupervisorOutcome.ReadFinalIntegratedBranch"/> / <see cref="SupervisorOutcome.ReadFinalRepositoryBranches"/>
/// over the durable decision tape) — never a second, competing notion of "the run's branch". Idempotent PER (alias,
/// branch): a repeat call for an alias whose recorded <see cref="PublishManifest.Branch"/> matches the CURRENT
/// target reuses that PR rather than opening a duplicate — but a later turn's merge advancing the same alias to a
/// genuinely different (turn-scoped) branch is treated as needing a FRESH PR, never silently pointed at the earlier
/// turn's now-abandoned one. A single-repo run is folded into the SAME multi-repo <see cref="IChangeSetService"/>
/// path as a one-element set — one honesty-invariant code path for both shapes, not two.
/// </remarks>
public sealed class RoomPullRequestService : IRoomPullRequestService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ISupervisorDecisionLog _ledger;
    private readonly IPublishManifestStore _manifests;
    private readonly IChangeSetService _changeSets;

    public RoomPullRequestService(CodeSpaceDbContext db, ISupervisorDecisionLog ledger, IPublishManifestStore manifests, IChangeSetService changeSets)
    {
        _db = db;
        _ledger = ledger;
        _manifests = manifests;
        _changeSets = changeSets;
    }

    public async Task<RoomPullRequestResult> OpenAsync(Guid workflowRunId, Guid teamId, Guid? actorUserId, CancellationToken cancellationToken)
    {
        var run = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == workflowRunId && r.TeamId == teamId)
            .Select(r => new { r.Status, r.OutputsJson })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Run not found.");

        // A supervisor run sits Suspended BETWEEN turns even after an earlier turn's merge already pushed a real,
        // durable branch — WorkflowRun.OutputsJson (repositoryId, for the single-repo case) is written only once,
        // at the run's OWN terminal completion, so reading it any earlier would either throw a misleading "no
        // resolvable repository" or (worse) resolve against a frontier that keeps moving turn to turn. Fail loud
        // and specific here rather than let a stale read either crash confusingly or silently under-report.
        if (!WorkflowRunState.IsTerminal(run.Status))
            throw new InvalidOperationException("This run is still in progress — wait for it to finish before opening a pull request.");

        var priorDecisions = await _ledger.GetTerminalDecisionsAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        var targets = await ResolveTargetsAsync(priorDecisions, run.OutputsJson, teamId, cancellationToken).ConfigureAwait(false);

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
        bool AlreadyOpenForCurrentBranch(PublishedRepo t) => openedByAlias.TryGetValue(t.Alias, out var manifest) && manifest.Branch == t.SourceBranch;

        var alreadyOpened = resolvable.Where(AlreadyOpenForCurrentBranch)
            .Select(t => ProjectAlreadyOpened(t, openedByAlias[t.Alias]))
            .ToList();

        var toOpen = resolvable.Where(t => !AlreadyOpenForCurrentBranch(t)).ToList();

        if (toOpen.Count == 0)
            return new RoomPullRequestResult { PullRequests = degraded.Concat(alreadyOpened).ToList() };

        var (title, body) = DeriveTitleAndBody(priorDecisions);

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

    private sealed record PublishedRepo(Guid? RepositoryId, string Alias, string SourceBranch, string TargetBranch);

    /// <summary>The run's published repositories to open a PR against — multi-repo's per-repo heads when the run integrated more than one, else the single-repo integrated branch (its repository resolved from the terminal output, its base from the repository's own default branch). Empty (not thrown) when the run published NOTHING — the caller decides whether that's an error.</summary>
    private async Task<IReadOnlyList<PublishedRepo>> ResolveTargetsAsync(IReadOnlyList<SupervisorPriorDecision> priorDecisions, string outputsJson, Guid teamId, CancellationToken cancellationToken)
    {
        var repositoryBranches = SupervisorOutcome.ReadFinalRepositoryBranches(priorDecisions);

        if (repositoryBranches.Count > 0)
            return repositoryBranches.Select(b => new PublishedRepo(b.RepositoryId, b.Alias, b.SourceBranch, b.TargetBranch)).ToList();

        var integratedBranch = SupervisorOutcome.ReadFinalIntegratedBranch(priorDecisions);

        if (string.IsNullOrEmpty(integratedBranch)) return Array.Empty<PublishedRepo>();

        var repositoryId = ReadRepositoryId(outputsJson)
            ?? throw new InvalidOperationException("This run's published branch has no resolvable repository.");

        var defaultBranch = await _db.Repository.AsNoTracking()
            .Where(r => r.Id == repositoryId && r.TeamId == teamId)
            .Select(r => r.DefaultBranch)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("This run's repository could not be resolved.");

        return new[] { new PublishedRepo(repositoryId, "primary", integratedBranch, defaultBranch) };
    }

    private static RoomPullRequestOpened ProjectAlreadyOpened(PublishedRepo target, PublishManifest manifest) => new()
    {
        RepositoryId = target.RepositoryId,
        Alias = target.Alias,
        Disposition = RoomPullRequestDisposition.AlreadyOpened,
        Number = manifest.PullRequestNumber,
        Url = manifest.PullRequestUrl,
    };

    /// <summary>Project one ChangeSet outcome into the Room's shape, and — only for a genuinely OPENED PR — record it onto the alias's PublishManifest row so a repeat call reuses it (I2: no second, competing notion of "did this alias already get a PR").</summary>
    private async Task<RoomPullRequestOpened> ProjectAndRecordAsync(Guid workflowRunId, Guid teamId, PublishedRepo target, ChangeSetPullRequestOutcome outcome, CancellationToken cancellationToken)
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

    /// <summary>Title = the stop summary's first line (I3 guarantees a completed, published run has a non-empty one — the model's own account of what it did); body = the summary in full. Falls back to a generic title/no body for the rare terminal shape with no stop decision (e.g. a server-forced stop that still had published work from an earlier turn). Internal (not private) so the framing is unit-pinned directly (InternalsVisibleTo), not only through integration coverage.</summary>
    internal static (string Title, string? Body) DeriveTitleAndBody(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var stop = priorDecisions.LastOrDefault(d => d.DecisionKind == SupervisorDecisionKinds.Stop);
        var summary = stop is null ? null : SupervisorOutcome.ReadStopSummary(stop.OutcomeJson);

        if (string.IsNullOrWhiteSpace(summary)) return ("Merge agent changes", null);

        var firstLine = summary.Split('\n', 2)[0].Trim();
        var title = firstLine.Length > 100 ? firstLine[..100].TrimEnd() : firstLine;

        return (title.Length > 0 ? title : "Merge agent changes", summary);
    }

    /// <summary>The single-repo run's PRIMARY repository, echoed onto the terminal output by <c>AgentSupervisorNode.Finish</c> (config, not a computed fact — <c>integratedBranch</c> alone carries no repository of its own). Empty string (not omitted) when the run configured none; null on any parse failure.</summary>
    private static Guid? ReadRepositoryId(string outputsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(outputsJson);

            return doc.RootElement.TryGetProperty("repositoryId", out var prop) && prop.ValueKind == JsonValueKind.String && Guid.TryParse(prop.GetString(), out var id)
                ? id
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
