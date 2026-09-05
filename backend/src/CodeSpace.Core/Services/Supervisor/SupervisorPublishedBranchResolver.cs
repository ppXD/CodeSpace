using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<SupervisorPublishedBranchResolver> _logger;

    public SupervisorPublishedBranchResolver(CodeSpaceDbContext db, IPublishManifestStore manifests, ILogger<SupervisorPublishedBranchResolver> logger)
    {
        _db = db;
        _manifests = manifests;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SupervisorRepositoryBranch>> ResolveAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<SupervisorPriorDecision> priorDecisions, Guid? primaryRepositoryId, CancellationToken cancellationToken)
    {
        var window = SupervisorPlanWindow.Read(priorDecisions);

        // The ONE carry-over trigger, shared with the merge rung: when the active generation has no mergeable result
        // of its own, EVERY rung below reads the whole tape instead of the window — the ladder itself is unchanged,
        // so an earlier generation's integrated head still outranks that generation's individual contributors.
        var acrossGenerations = SupervisorMergeContributors.ActiveGenerationHasNoMergeableResult(priorDecisions);
        var decisions = acrossGenerations ? priorDecisions : window.Decisions;

        var repositoryBranches = SupervisorOutcome.ReadFinalRepositoryBranchesWithin(decisions);

        if (repositoryBranches.Count > 0) return repositoryBranches;

        var integratedBranch = SupervisorOutcome.ReadFinalIntegratedBranchWithin(decisions);

        if (!string.IsNullOrEmpty(integratedBranch))
            return await ResolveSingleIntegratedBranchAsync(workflowRunId, teamId, integratedBranch, primaryRepositoryId, cancellationToken).ConfigureAwait(false);

        // An integrity-failed resolver is the run's newest staging frontier. Its own branch is correctly withheld
        // above, but falling through to the generic manifest rung would publish an OLDER contributor branch from
        // before the attempted reconciliation — precisely the conflicted/incomplete head resolve was meant to
        // replace. Only a later authoritative merge/resolve may clear this barrier (and would have returned above).
        // Read over the WHOLE tape, since the rung it guards is now allowed to read there too.
        if (SupervisorOutcome.HasActiveResolveContributorIntegrityBarrier(priorDecisions)) return Array.Empty<SupervisorRepositoryBranch>();

        return await ResolveLedgerDirectAsync(workflowRunId, teamId, priorDecisions, window, acrossGenerations, cancellationToken).ConfigureAwait(false);
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
    ///
    /// <para>CONSERVATION across a plan-generation boundary (the merge rung's own fix, applied on this rung): a
    /// re-plan issued AFTER the wave finished slices the window past every spawn, leaving the active staging set
    /// EMPTY — not null — so the filter below discarded every pushed manifest and publish resolved 0 targets on a run
    /// with three accepted, pushed branches. A generation with no mergeable result of its own
    /// (<see cref="SupervisorMergeContributors.ActiveGenerationHasNoMergeableResult"/> — the SAME trigger the merge
    /// rung fires on) therefore falls back to the same settled-work floor the merge carries over
    /// (<see cref="SupervisorMergeContributors.SettledAcrossGenerations"/>); a generation with one of its own is
    /// authoritative exactly as before, and a plan-less legacy tape never enters the fallback at all.</para>
    ///
    /// <para>The carry-over is GATED on the run having no integrated branch anywhere on the tape
    /// (<see cref="SupervisorOutcome.AnyMergeIntegratedABranch"/>). A contributor branch is a PARTIAL head — one
    /// agent's own work, newest-per-alias — so shipping one past a generation that already integrated cleanly would
    /// open a PR on a subset of work the run had already combined. When the gate is closed this rung publishes
    /// nothing, which is the same posture the integrity barrier above takes: silence beats a partial head. Only when
    /// no merge ever integrated is a contributor's own pushed branch genuinely the run's published artifact — and
    /// there an already-merged id is deliberately NOT excluded, the same posture this rung already takes toward
    /// merged contributors inside the active generation.</para>
    /// </summary>
    private async Task<IReadOnlyList<SupervisorRepositoryBranch>> ResolveLedgerDirectAsync(Guid workflowRunId, Guid teamId, IReadOnlyList<SupervisorPriorDecision> priorDecisions, SupervisorPlanWindowSlice window, bool acrossGenerations, CancellationToken cancellationToken)
    {
        var manifests = await _manifests.ListForWorkflowRunAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        var staged = window.IsPlanBounded
            ? window.Decisions.Where(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind)).SelectMany(d => SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson)).ToHashSet()
            : null;

        var carriedOver = staged is not null && acrossGenerations && !SupervisorOutcome.AnyMergeIntegratedABranch(priorDecisions)
            ? SupervisorMergeContributors.SettledAcrossGenerations(priorDecisions)
            : null;

        var activeAgentRunIds = carriedOver is null ? staged : carriedOver.ToHashSet();

        var agentManifests = manifests
            .Where(m => m.Kind == PublishManifestKind.Agent && m.AgentRunId is not null && (activeAgentRunIds is null || activeAgentRunIds.Contains(m.AgentRunId.Value)))
            .ToList();

        if (agentManifests.Count == 0) return Array.Empty<SupervisorRepositoryBranch>();

        var publishedAgentIds = agentManifests
            .GroupBy(m => m.AgentRunId!.Value)
            .Where(g => g.All(m => m.PublishStateValue == PublishState.Pushed || m.PullRequestNumber is not null))
            .Select(g => g.Key)
            .ToHashSet();

        var rejected = SupervisorOutcome.WithheldAgentRunIds(window.Decisions);

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

        if (carriedOver is { Count: > 0 })
            _logger.LogInformation("Supervisor publish carried over {CarriedOver} succeeded result(s) from earlier plan generation(s) into {Count} target(s) — the active plan generation staged none", carriedOver.Count, branches.Count);

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
