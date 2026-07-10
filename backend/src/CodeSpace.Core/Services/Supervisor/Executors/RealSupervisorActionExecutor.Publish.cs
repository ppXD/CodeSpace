using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Agents;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Supervisor.Executors;

/// <summary>
/// The SYNCHRONOUS publish half of the real executor (Rule 10 <c>.Publish.cs</c>, DC-2): open a pull request
/// against the run's genuinely published branch(es) — resolved off the SAME <see cref="ISupervisorPublishedBranchResolver"/>
/// the Room's own Open-PR action and publish-state projection use, so "what did this run publish" can never drift
/// across the server-authored path and the human-triggered one. Only ever reached via
/// <see cref="SupervisorPublishGate"/>'s substitution of a <c>stop</c> — the decision's own payload
/// (<see cref="SupervisorPublishPayload"/>) carries no input, every target is assembled from durable state.
///
/// <para>Idempotent PER (alias, branch) — the SAME check <see cref="Sessions.Room.RoomPullRequestService.OpenAsync"/>
/// already applies: <see cref="SupervisorTurnService.ExecuteUnderClaimAsync"/>'s crash-recovery path re-enters ANY
/// decision left <c>Running</c> after a crash by re-executing it from scratch (a lost begin-CAS), and opening a PR
/// is a resource-creating side effect (unlike merge's idempotent git push) — a crash between a real provider
/// create-PR call and <c>RecordTerminalAsync</c> must never double-open a PR on replay.</para>
/// </summary>
public sealed partial class RealSupervisorActionExecutor
{
    private async Task<SupervisorExecution> ExecutePublishAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
    {
        var targets = await _publishedBranches.ResolveAsync(context.SupervisorRunId, context.TeamId, context.PriorDecisions, cancellationToken).ConfigureAwait(false);

        var degraded = targets.Where(t => t.RepositoryId is null)
            .Select(t => (object)new { alias = t.Alias, error = "no resolvable repository id for this repo" })
            .ToList();

        var resolvable = targets.Where(t => t.RepositoryId is not null).ToList();
        var failed = new List<object>(degraded);

        if (resolvable.Count == 0)
        {
            _logger.LogWarning("Supervisor publish decision found no resolvable published branch to open a pull request against at turn {Turn} on node {NodeId}", context.TurnNumber, context.NodeId);

            if (failed.Count == 0) failed.Add(new { alias = "", error = "no resolvable published branch" });

            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(new { publish = new { opened = Array.Empty<object>(), failed } }, AgentJson.Options));
        }

        var existing = await _manifests.ListForWorkflowRunAsync(context.SupervisorRunId, context.TeamId, cancellationToken).ConfigureAwait(false);
        var openedByAlias = existing.Where(m => m.Kind == PublishManifestKind.Integration && m.PullRequestUrl is { Length: > 0 }).ToDictionary(m => m.RepositoryAlias);

        bool AlreadyOpenForCurrentBranch(SupervisorRepositoryBranch t) => openedByAlias.TryGetValue(t.Alias, out var manifest) && manifest.Branch == t.SourceBranch;

        var opened = resolvable.Where(AlreadyOpenForCurrentBranch)
            .Select(t => (object)new { alias = t.Alias, url = openedByAlias[t.Alias].PullRequestUrl, number = openedByAlias[t.Alias].PullRequestNumber })
            .ToList();

        var toOpen = resolvable.Where(t => !AlreadyOpenForCurrentBranch(t)).ToList();

        if (toOpen.Count == 0)
        {
            _logger.LogInformation("Supervisor publish decision found every target already opened (a crash-recovery replay, or a prior partial success) at turn {Turn} — reusing, never opening a duplicate", context.TurnNumber);

            return SupervisorExecution.Synchronous(JsonSerializer.Serialize(new { publish = new { opened, failed } }, AgentJson.Options));
        }

        var (title, body) = SupervisorOutcome.DeriveTitleAndBody(context.PriorDecisions);

        var spec = new ChangeSetPullRequestSpec
        {
            Title = title,
            Body = body,
            Repositories = toOpen.Select(t => new ChangeSetPullRequest { RepositoryId = t.RepositoryId!.Value, SourceBranch = t.SourceBranch, TargetBranch = t.TargetBranch }).ToList(),
        };

        var result = await _changeSets.OpenPullRequestsAsync(context.TeamId, spec, actorUserId: null, cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < toOpen.Count; i++)
            await ProjectOnePublishOutcomeAsync(context, toOpen[i], result.PullRequests[i], opened, failed, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Supervisor publish decision opened {Opened} pull request(s), {Failed} failed, at turn {Turn}", opened.Count, failed.Count, context.TurnNumber);

        return SupervisorExecution.Synchronous(JsonSerializer.Serialize(new { publish = new { opened, failed } }, AgentJson.Options));
    }

    /// <summary>Project one ChangeSet outcome into the publish decision's outcome shape, and — only for a genuinely OPENED PR — record it onto the alias's PublishManifest row (the SAME idempotency ledger the Room's own Open-PR action writes, I2: no second, competing notion of "did this alias already get a PR").</summary>
    private async Task ProjectOnePublishOutcomeAsync(SupervisorTurnContext context, SupervisorRepositoryBranch target, ChangeSetPullRequestOutcome outcome, List<object> opened, List<object> failed, CancellationToken cancellationToken)
    {
        if (outcome.Disposition != ChangeSetPullRequestDisposition.Opened)
        {
            failed.Add(new { alias = target.Alias, error = outcome.Error ?? "the pull request could not be opened" });
            return;
        }

        await _manifests.UpsertForIntegrationAsync(new PublishManifestUpsert
        {
            TeamId = context.TeamId,
            WorkflowRunId = context.SupervisorRunId,
            RepositoryAlias = target.Alias,
            RepositoryId = target.RepositoryId,
            Branch = target.SourceBranch,
            PublishStateValue = PublishState.Pushed,
            PullRequestNumber = outcome.Number,
            PullRequestUrl = outcome.Url,
        }, cancellationToken).ConfigureAwait(false);

        opened.Add(new { alias = target.Alias, url = outcome.Url, number = outcome.Number });
    }
}
