using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Supervisor;

/// <summary>
/// DC-3 — the ONE reader of "what did this run genuinely publish" every downstream surface (the Room's Open-PR
/// action, the Room's publish-state projection, the Room's delivery card, <c>AgentSupervisorNode.Finish</c>'s
/// terminal output, and the supervisor's own auto-open-PR delivery step) shares, so none of them can drift on what
/// "published" means. Tries the durable-tape, merge-derived reads FIRST
/// (<see cref="SupervisorOutcome.ReadFinalRepositoryBranches"/> / <see cref="SupervisorOutcome.ReadFinalIntegratedBranch"/>
/// — a clean integration or a verified resolve); only when NEITHER exists does it fall back to the canonical
/// <see cref="Persistence.Entities.PublishManifest"/> ledger directly (P0-5's "a single already-pushed, accepted
/// agent satisfies published with no merge required at all" case — run 96695645's own motivating scenario).
///
/// <para><see cref="ResolveAsync"/> is PRE-TERMINAL-SAFE: the single-repo merge-derived path needs a repository id
/// that <see cref="Messages.Dtos.Workflows.WorkflowRun.OutputsJson"/> carries ONLY once the run reaches terminal
/// completion (<c>AgentSupervisorNode.Finish</c> writes it). A caller with a LIVE <c>SupervisorTurnContext</c>
/// (the gate's executor, the stop-time terminal-output enrichment, DC-2d's own stop-acceptance target resolution)
/// supplies <paramref name="primaryRepositoryId"/> itself — <c>context.AgentProfile?.RepositoryId</c>. A
/// post-terminal caller (the Room, which only ever calls this
/// once <c>WorkflowRunState.IsTerminal</c>) passes null and gets the OutputsJson fallback — harmless as a no-op for
/// a pre-terminal analysis-only run that genuinely has none either way.</para>
///
/// <para>Every read is team-scoped; a repository whose default branch can't be resolved still surfaces its branch
/// with an empty <see cref="SupervisorRepositoryBranch.TargetBranch"/> (the caller's downstream
/// <c>ChangeSetService</c> already turns that into a per-repo Failed disposition rather than a thrown exception —
/// the honesty invariant every other degraded-repository case in this ladder already keeps).</para>
/// </summary>
public interface ISupervisorPublishedBranchResolver
{
    /// <param name="primaryRepositoryId">The run's single configured repository, when the caller already has a LIVE one (pre-terminal — <c>context.AgentProfile?.RepositoryId</c>). Null lets the resolver fall back to the run's terminal <c>OutputsJson</c> (post-terminal callers only).</param>
    Task<IReadOnlyList<SupervisorRepositoryBranch>> ResolveAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<SupervisorPriorDecision> priorDecisions, Guid? primaryRepositoryId, CancellationToken cancellationToken);
}

public sealed class SupervisorPublishedBranchResolver : ISupervisorPublishedBranchResolver, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IPublishManifestStore _manifests;

    public SupervisorPublishedBranchResolver(CodeSpaceDbContext db, IPublishManifestStore manifests)
    {
        _db = db;
        _manifests = manifests;
    }

    public async Task<IReadOnlyList<SupervisorRepositoryBranch>> ResolveAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<SupervisorPriorDecision> priorDecisions, Guid? primaryRepositoryId, CancellationToken cancellationToken)
    {
        var repositoryBranches = SupervisorOutcome.ReadFinalRepositoryBranches(priorDecisions);

        if (repositoryBranches.Count > 0) return repositoryBranches;

        var integratedBranch = SupervisorOutcome.ReadFinalIntegratedBranch(priorDecisions);

        return string.IsNullOrEmpty(integratedBranch)
            ? await ResolveLedgerDirectAsync(workflowRunId, teamId, priorDecisions, cancellationToken).ConfigureAwait(false)
            : await ResolveSingleIntegratedBranchAsync(workflowRunId, teamId, integratedBranch, primaryRepositoryId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The traditional single-repo merge-derived path: the run's ONE configured primary repository, based against ITS OWN default branch. Prefers the caller's LIVE <paramref name="primaryRepositoryId"/> (pre-terminal); falls back to the run's terminal <c>OutputsJson</c> otherwise. Empty when the repository is unresolvable either way — never thrown; the caller decides whether that's an error.</summary>
    private async Task<IReadOnlyList<SupervisorRepositoryBranch>> ResolveSingleIntegratedBranchAsync(Guid workflowRunId, Guid teamId, string integratedBranch, Guid? primaryRepositoryId, CancellationToken cancellationToken)
    {
        var repositoryId = primaryRepositoryId ?? await ReadTerminalOutputRepositoryIdAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        if (repositoryId is null) return Array.Empty<SupervisorRepositoryBranch>();

        var defaultBranch = await ResolveDefaultBranchAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false);

        return new[] { new SupervisorRepositoryBranch { RepositoryId = repositoryId, Alias = "primary", SourceBranch = integratedBranch, TargetBranch = defaultBranch ?? "" } };
    }

    /// <summary>
    /// P0-5's ledger-direct fallback: the run's genuinely PUSHED (or already PR'd) Agent-kind
    /// <see cref="Persistence.Entities.PublishManifest"/> rows, all-or-nothing per agent run (a partially-published
    /// multi-repo agent is not genuinely published — mirrors <c>SupervisorTurnService.Rehydrate.FoldPublishedAgentRunIdsAsync</c>
    /// exactly), MINUS any agent an objective acceptance grade REJECTED (the same "局部綠≠整合綠" bar every other door
    /// to the head already enforces). The newest manifest row per alias wins when more than one accepted contributor
    /// wrote to the same alias across different rounds. Repository ids come DIRECTLY off each manifest row (populated
    /// at agent-completion time, independent of run terminality) — no OutputsJson dependency at all.
    /// </summary>
    private async Task<IReadOnlyList<SupervisorRepositoryBranch>> ResolveLedgerDirectAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<SupervisorPriorDecision> priorDecisions, CancellationToken cancellationToken)
    {
        var manifests = await _manifests.ListForWorkflowRunAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        var agentManifests = manifests.Where(m => m.Kind == PublishManifestKind.Agent && m.AgentRunId is not null).ToList();

        if (agentManifests.Count == 0) return Array.Empty<SupervisorRepositoryBranch>();

        var publishedAgentIds = agentManifests
            .GroupBy(m => m.AgentRunId!.Value)
            .Where(g => g.All(m => m.PublishStateValue == PublishState.Pushed || m.PullRequestNumber is not null))
            .Select(g => g.Key)
            .ToHashSet();

        var rejected = SupervisorOutcome.RejectedAgentRunIds(priorDecisions);

        var eligible = agentManifests
            .Where(m => publishedAgentIds.Contains(m.AgentRunId!.Value) && !rejected.Contains(m.AgentRunId!.Value) && !string.IsNullOrEmpty(m.Branch))
            .GroupBy(m => m.RepositoryAlias)
            .Select(g => g.OrderByDescending(m => m.CreatedDate).First())
            .ToList();

        if (eligible.Count == 0) return Array.Empty<SupervisorRepositoryBranch>();

        var branches = new List<SupervisorRepositoryBranch>(eligible.Count);

        foreach (var m in eligible)
        {
            var defaultBranch = await ResolveDefaultBranchAsync(m.RepositoryId, teamId, cancellationToken).ConfigureAwait(false);

            branches.Add(new SupervisorRepositoryBranch { RepositoryId = m.RepositoryId, Alias = m.RepositoryAlias, SourceBranch = m.Branch!, TargetBranch = defaultBranch ?? "" });
        }

        return branches;
    }

    private async Task<string?> ResolveDefaultBranchAsync(Guid? repositoryId, Guid teamId, CancellationToken cancellationToken)
    {
        if (repositoryId is null) return null;

        return await _db.Repository.AsNoTracking()
            .Where(r => r.Id == repositoryId && r.TeamId == teamId)
            .Select(r => r.DefaultBranch)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The single-repo run's PRIMARY repository, echoed onto the terminal output by <c>AgentSupervisorNode.Finish</c> — POST-TERMINAL ONLY (empty before then). Empty string (not omitted) when the run configured none; null on any parse failure or before terminal completion.</summary>
    private async Task<Guid?> ReadTerminalOutputRepositoryIdAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var outputsJson = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == workflowRunId && r.TeamId == teamId)
            .Select(r => r.OutputsJson)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(outputsJson)) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(outputsJson);

            return doc.RootElement.TryGetProperty("repositoryId", out var prop) && prop.ValueKind == System.Text.Json.JsonValueKind.String && Guid.TryParse(prop.GetString(), out var id)
                ? id
                : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
