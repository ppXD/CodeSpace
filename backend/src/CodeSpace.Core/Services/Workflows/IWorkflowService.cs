using System.Text.Json;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Workflow CRUD + execution kickoff. Rule 16 — every handler under
/// CodeSpace.Core.Handlers/{Command,Query}Handlers/Workflows is a thin shell delegating here.
/// </summary>
public interface IWorkflowService
{
    Task<IReadOnlyList<WorkflowSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken);
    Task<WorkflowDetail?> GetAsync(Guid workflowId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves a workflow by EITHER its GUID (legacy link) or its team-unique slug (canonical clean
    /// URL), reusing <see cref="GetAsync"/> for the full detail load + team-scope. Null on miss / not-team.
    /// </summary>
    Task<WorkflowDetail?> GetByRefAsync(string idOrSlug, Guid teamId, CancellationToken cancellationToken);
    Task<Guid> CreateAsync(Guid teamId, string name, string? description, WorkflowDefinition definition, IReadOnlyList<WorkflowActivationInput> activations, bool enabled, CancellationToken cancellationToken);
    Task UpdateAsync(Guid workflowId, Guid teamId, string name, string? description, WorkflowDefinition definition, IReadOnlyList<WorkflowActivationInput> activations, CancellationToken cancellationToken);
    Task DeleteAsync(Guid workflowId, Guid teamId, CancellationToken cancellationToken);
    Task SetEnabledAsync(Guid workflowId, Guid teamId, bool enabled, CancellationToken cancellationToken);

    /// <summary>
    /// Creates a <c>workflow_run_request</c> (source_type="manual", actor=user) and then a
    /// <c>workflow_run</c> pointing at it. Returns the run id so the SPA can navigate.
    /// </summary>
    Task<Guid> RunManuallyAsync(Guid workflowId, Guid teamId, Guid actorUserId, JsonElement? payload, CancellationToken cancellationToken);

    /// <summary>
    /// Clones an existing run as a fresh replay. Creates a new <c>workflow_run_request</c>
    /// (source_type="replay", causation_id = original.request_id) and a <c>workflow_run</c>
    /// that reuses the original's workflow_version, release_hash, and variable snapshot rows.
    /// Returns the new run's id. Throws <see cref="KeyNotFoundException"/> if the original
    /// isn't in the caller's team.
    /// </summary>
    Task<Guid> ReplayRunAsync(Guid originalRunId, Guid teamId, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Re-run a prior run STARTING FROM <paramref name="fromNodeId"/> (D7): forks a new run that REUSES the
    /// upstream cells (pre-seeded from the original) and re-runs the chosen node + its transitive downstream.
    /// Returns the new run's id. Refuses (before any write): a cross-team / unknown / container-internal
    /// from-node, a re-run closure containing an effectful node (slice-1 fail-closed), or an upstream node that
    /// didn't settle reusably in the original.
    /// </summary>
    Task<Guid> RerunFromNodeAsync(Guid originalRunId, string fromNodeId, Guid teamId, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Re-run ONE branch of a top-level flow.map (D7): forks a new run that REUSES the N-1 sibling branches
    /// (pre-seeded from the original) and re-runs the chosen branch + the map's downstream (the synthesizer
    /// re-runs over the new aggregate). Returns the new run's id. Refuses (before any write): a cross-team /
    /// unknown / non-top-level / non-map target, a branch index out of range, an original map that didn't
    /// complete successfully, or a branch body containing a side-effecting / suspendable / nested-container node.
    /// <paramref name="operationId"/> is an optional client-minted idempotency token (reused on a double-submit /
    /// HTTP retry → the SAME prior fork is returned, never a second one; a new token per genuine re-rerun).
    /// </summary>
    Task<Guid> RerunMapBranchAsync(Guid originalRunId, string mapNodeId, int branchIndex, Guid teamId, Guid actorUserId, Guid? operationId, CancellationToken cancellationToken);

    /// <summary>
    /// Re-run a SET of a top-level map's branches in ONE forked run (the generic primitive the single-branch rerun is the
    /// <c>|branchIndices| == 1</c> case of): the chosen branches re-run fresh, every other reusable sibling is replayed,
    /// the map re-aggregates. Same fail-closed gates as <see cref="RerunMapBranchAsync"/>, plus an empty set is rejected.
    /// <paramref name="operationId"/> is the optional client-minted idempotency token (see <see cref="RerunMapBranchAsync"/>).
    /// <para>MUST run inside an ambient transaction (the mediator's <c>TransactionalBehavior</c> provides one for the
    /// command path). It stages the fork BEFORE acquiring the per-branch rerun lease, so a lease conflict throws after
    /// the fork rows are flushed — only the surrounding transaction's rollback undoes them. A caller that bypasses the
    /// command pipeline would leave a committed orphan fork on a concurrent-rerun conflict.</para>
    /// </summary>
    Task<Guid> RerunMapBranchesAsync(Guid originalRunId, string mapNodeId, IReadOnlySet<int> branchIndices, Guid teamId, Guid actorUserId, Guid? operationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowRunSummary>> ListRunsAsync(Guid workflowId, Guid teamId, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// The team's runs index — every top-level run the team owns (any source), newest first, narrowed by
    /// <paramref name="filter"/> (any subset of its dimensions; <see cref="RunListFilter.None"/> for all). Keyset-paginated:
    /// <paramref name="cursor"/> is the opaque token from the previous page (null = first page), <paramref name="limit"/>
    /// is clamped to <see cref="WorkflowService.MaxRunsPageSize"/>. Returns the page + a next cursor (null on the last page).
    /// </summary>
    Task<RunPage> ListTeamRunsAsync(Guid teamId, RunListFilter filter, string? cursor, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// The same team runs index as <see cref="ListTeamRunsAsync"/>, but OFFSET-paginated for numbered pages: returns
    /// page <paramref name="page"/> (1-based) of <paramref name="pageSize"/> rows plus the total count matching the
    /// filter, so the caller can render "page X of Y" and jump to any page. <paramref name="pageSize"/> is clamped to
    /// <see cref="WorkflowService.MaxRunsPageSize"/>; a page past the end returns an empty list with the true total.
    /// </summary>
    Task<RunPage> ListTeamRunsPageAsync(Guid teamId, RunListFilter filter, int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>
    /// True scoped counts for the runs cockpit cards — live / failed / suspended / suspended-needing-review / today —
    /// each a COUNT over the team's runs narrowed by <paramref name="filter"/> (the bar's scope), NOT a sample of a
    /// loaded page, so "no filter" is the genuine superset. <paramref name="todayStart"/> is the caller's local
    /// start-of-day for the today count. Mirrors the same base query + dimensions as the runs index.
    /// </summary>
    Task<RunSummary> SummarizeTeamRunsAsync(Guid teamId, RunListFilter filter, DateTimeOffset todayStart, CancellationToken cancellationToken);

    /// <summary>The run's detail. <paramref name="mergeLineage"/> (default) shows the LINEAGE-MERGED picture — for each (node, iteration) cell the latest attempt that ran it, so a rerun-from-node shows reused + re-run branches together. Pass <c>false</c> to scope STRICTLY to this run's own cells (the Session Room's per-attempt view — each attempt must show ONLY its own flow, never the latest attempt's merged in).</summary>
    Task<WorkflowRunDetail?> GetRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken, bool mergeLineage = true);

    /// <summary>
    /// Resolves a run by EITHER its GUID (legacy link) or its team-scoped run number (canonical clean
    /// URL, e.g. <c>/runs/1042</c>), reusing <see cref="GetRunAsync"/> for the detail load + team-scope.
    /// Null on miss / not-team.
    /// </summary>
    Task<WorkflowRunDetail?> GetRunByRefAsync(string idOrNumber, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// The attempt ladder of the lineage <paramref name="runId"/> belongs to — the original plus every replay/rerun
    /// fork sharing its <c>RootRunId ?? Id</c> key, oldest first, 1-based and latest-flagged. Team-scoped; a foreign /
    /// absent run returns null (the controller 404-conflates).
    /// </summary>
    Task<RunAttemptsResponse?> ListRunAttemptsAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// The per-CELL attempt history — every lineage attempt that ran the <paramref name="nodeId"/>/<paramref name="iterationKey"/>
    /// cell (a node, or one map branch), oldest first with its agent run + outcome. Lets a re-run node show each earlier
    /// run, not only the merged latest. Team-scoped; a foreign / absent run returns null.
    /// </summary>
    Task<CellAttemptsResponse?> ListCellAttemptsAsync(Guid runId, string nodeId, string iterationKey, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Resolve a pending <c>Approval</c> wait on a Suspended run with a human decision
    /// (approved + optional comment) and resume it. Tenancy: the run's workflow must belong to
    /// the caller's team (<see cref="KeyNotFoundException"/> conflated with not-yours). Returns
    /// false when the run has no pending approval wait — already resolved, not suspended, or
    /// parked on a different signal (timer / callback).
    /// </summary>
    Task<bool> ApproveRunAsync(Guid runId, Guid teamId, Guid actorUserId, bool approved, string? comment, CancellationToken cancellationToken);

    /// <summary>
    /// Operator override: force-resolve a STRANDED signal-driven wait — a <c>Timer</c> whose scheduled wake was dropped
    /// (fired now with the standard wake marker) or a <c>Callback</c> whose external system never posts (resolved with
    /// <paramref name="payloadJson"/> as the body, or empty) — so the run un-strands, reusing the same idempotent
    /// resolve-first CAS every real resume signal funnels through. TEAM-SCOPED: a foreign / absent run or wait throws
    /// <see cref="KeyNotFoundException"/> (404). Returns <see cref="ReissueWaitOutcome.UnsupportedKind"/> for a
    /// decision- / completion-driven wait (those resolve via their own verb or a reconciler backstop),
    /// <see cref="ReissueWaitOutcome.AlreadyResolved"/> when a real signal / deadline / concurrent reissue won the CAS,
    /// and <see cref="ReissueWaitOutcome.Reissued"/> on success (which also writes a <c>wait.reissued</c> audit record).
    /// </summary>
    Task<ReissueWaitOutcome> ReissueWaitAsync(Guid runId, Guid waitId, Guid teamId, Guid actorUserId, string? payloadJson, CancellationToken cancellationToken);

    /// <summary>
    /// Operator-triggered cancel: CAS the run from any non-terminal state (Pending/Enqueued/Running/Suspended)
    /// → <c>Cancelled</c>, then tear the whole thing down — resolve its still-pending waits (closed as moot; the
    /// wait-status domain has no <c>Cancelled</c> value), KILL-WAVE its
    /// branch agent runs (Queued via <c>CancelQueuedAsync</c>, Running via <c>CancelRunningAsync</c> + a durable
    /// process kill), and cancel its staged non-terminal sub-workflow children — and emit a <c>run.cancelled</c>
    /// ledger record. The teardown is best-effort, so one failed kill never aborts the cancel; the reconciler's
    /// parent-run-terminal guard re-cleans anything missed.
    ///
    /// <para>TEAM-SCOPED + fail-closed: returns <c>null</c> when the run isn't <paramref name="teamId"/>'s (a
    /// foreign id leaks neither existence nor a spurious success). An already-terminal run is an idempotent no-op
    /// — the returned <c>CancelRunOutcome</c> reports <c>Cancelled=false</c> with the run's existing terminal
    /// status. Does NOT change the host-driven <c>OperationCanceledException</c> path the engine already has.</para>
    /// </summary>
    Task<CancelRunOutcome?> CancelRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Continue a STRANDED Suspended run (Suspended with NO pending wait) on demand — the user-triggered, single-run
    /// twin of the reconciler's stranded-Suspended re-dispatch. CAS Suspended → Pending then post-commit dispatch,
    /// driving the SAME continuation the ≤2-min sweep would. TEAM-SCOPED: a foreign run throws
    /// <see cref="KeyNotFoundException"/> (404). Returns <c>true</c> if this call drove it; <c>false</c> when the run
    /// is terminal / Running, still parked on a pending wait (use <c>/resume</c>), or the CAS lost to a concurrent
    /// continue. Idempotent + race-safe (the dispatcher's Pending → Enqueued CAS is the double-dispatch guard).
    /// </summary>
    Task<bool> ContinueRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);

    IReadOnlyList<NodeManifestDto> ListNodeManifests();

    /// <summary>
    /// Canonical list of engine-injected <c>sys.*</c> variables — fixed per release. Feeds
    /// the editor's read-only System tab + the {{}} autocomplete picker so frontend doesn't
    /// have to mirror a parallel list. Sourced from <c>SystemScopeKeys.Descriptors</c>.
    /// </summary>
    IReadOnlyList<SystemVariableDto> ListSystemVariables();
}
