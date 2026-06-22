using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// A generic, run-neutral filter for the runs index. EVERY field is optional and a LIST: values WITHIN one field are
/// OR'd (<c>= ANY(...)</c>), and the fields are AND'd together — so <c>WorkflowIds=[a,b]</c> + <c>Statuses=[Running,
/// Suspended]</c> means <c>(workflow ∈ {a,b}) AND (status ∈ {Running,Suspended})</c>. ONE filter type, ONE query
/// serves every runs surface: a workflow page supplies <see cref="WorkflowIds"/>, a "failed" view supplies
/// <see cref="Statuses"/>, a date view supplies <see cref="Since"/>/<see cref="Until"/>, the cockpit supplies none.
/// Adding a dimension backed by a column already on <c>workflow_run</c> is one field here plus one predicate at the
/// single apply site — no per-surface query. A dimension on another table (e.g. the request's actor) first needs a
/// <c>source_type</c>-style denormalisation onto the run to keep the query JOIN-free and index-driven.
///
/// <para>Index coverage (honest — not every combination is an index seek):
/// <list type="bullet">
/// <item>EMPTY / date-window: index seek. The base team query and <see cref="Since"/>/<see cref="Until"/> ride
///   <c>idx_workflow_run_team_keyset (team_id, created_date DESC, id DESC)</c> — the date bound is a range on the
///   index's 2nd column, so a window is a bounded ordered seek, not a scan.</item>
/// <item><see cref="WorkflowIds"/>: index seek via the dedicated <c>idx_workflow_run_workflow_keyset</c> — a
///   <c>workflow_id = ANY(...)</c> is a per-id index seek (BitmapOr), one bounded scan per listed workflow.</item>
/// <item>BARE <see cref="Statuses"/> / <see cref="SourceTypes"/> (no <see cref="WorkflowIds"/>): NOT a leading-column
///   seek — an ordered scan of the team keyset index with a post-scan recheck. Fine for recent / shallow pages;
///   the upgrade for a high-volume status- or source-filtered surface is a
///   <c>(team_id, status|source_type, created_date DESC, id DESC)</c> partial index, added WHEN that surface ships
///   (not pre-built — there is no such caller today).</item>
/// </list>
/// Every supported combination is keyset-pageable regardless of which tier it lands in.</para>
/// </summary>
public sealed record RunListFilter
{
    /// <summary>Only runs of any of these authored workflows (<c>workflow_id = ANY</c>). Null / empty = any source (includes snapshot / task runs with no workflow).</summary>
    public IReadOnlyList<Guid>? WorkflowIds { get; init; }

    /// <summary>
    /// Only runs in ANY of these lifecycle states (null / empty = any state). A SET so a surface can ask for the
    /// non-terminal "active" group (Pending, Enqueued, Running, Suspended) in ONE query rather than N. Translates to
    /// SQL <c>status = ANY(...)</c>, an index recheck on the keyset scan (same access tier as a single status).
    /// </summary>
    public IReadOnlyList<WorkflowRunStatus>? Statuses { get; init; }

    /// <summary>Only runs from any of these open <c>source_type</c> tokens (<c>source_type = ANY</c>; e.g. <c>manual</c>, <c>schedule.cron</c>, <c>provider.github.pull_request</c>). Null / empty = any source.</summary>
    public IReadOnlyList<string>? SourceTypes { get; init; }

    /// <summary>
    /// Only runs whose launch SCOPE touches any of these repositories (array-overlap <c>&amp;&amp;</c>, OR-within). This is the
    /// run's launch scope (which repos it was launched against — a multi-repo task touches several), a point-in-time
    /// snapshot, NOT the repos it actually changed. Null / empty = any.
    /// </summary>
    public IReadOnlyList<Guid>? RepositoryIds { get; init; }

    /// <summary>Only runs whose launch SCOPE (derived from its repos at launch) touches any of these projects (array-overlap, OR-within). Null / empty = any.</summary>
    public IReadOnlyList<Guid>? ProjectIds { get; init; }

    /// <summary>Only runs launched by any of these users (<c>actor_id = ANY</c>, OR-within). A webhook / system run (null actor) matches no actor filter. Null / empty = any.</summary>
    public IReadOnlyList<Guid>? ActorIds { get; init; }

    /// <summary>Only runs of any of these coarse origin kinds (<c>run_kind = ANY</c>, OR-within; e.g. <c>workflow</c>, <c>task</c>, <c>event</c>, <c>replay</c> — see <c>RunKinds</c>). Null / empty = any.</summary>
    public IReadOnlyList<string>? RunKinds { get; init; }

    /// <summary>Only task runs of any of these projection/coordination modes (<c>projection_kind = ANY</c>, OR-within; e.g. <c>single-agent</c>, <c>supervisor</c>). A non-task run (null projection_kind) matches none. Null / empty = any.</summary>
    public IReadOnlyList<string>? ProjectionKinds { get; init; }

    /// <summary>
    /// Only runs that USED any of these agent personas (an EXISTS over the run's agent runs, OR-within). The agent set
    /// is runtime-evolving (the supervisor spawns agents per turn), so this is NOT a launch-time run column — it matches
    /// any agent run the run spawned. A raw-harness agent (no persona) is not matched. Null / empty = any.
    /// </summary>
    public IReadOnlyList<Guid>? AgentDefinitionIds { get; init; }

    /// <summary>
    /// <c>true</c> = only runs with a PENDING decision (a parked decision the run is waiting on a human/policy to
    /// answer), <c>false</c> = only runs WITHOUT one, null = either. Narrower than <c>Suspended</c>: a run parked on a
    /// Timer/Action wait is Suspended but has no pending decision, and an agent-grain decision parks the agent without
    /// flipping the run's status. Matches either park backend (node-grain <c>workflow_run_wait</c> Decision-Pending,
    /// or agent-grain <c>tool_call_ledger</c> decision.request-AwaitingApproval via its agent run) — an EXISTS.
    /// </summary>
    public bool? HasPendingDecision { get; init; }

    /// <summary>
    /// <c>true</c> = only runs a human should act on NOW (the BROAD attention union), <c>false</c> = only runs that
    /// don't, null = either. A run needs attention when it (a) has a pending decision (<see cref="HasPendingDecision"/>),
    /// OR (b) is Suspended on a HUMAN-ACTIONABLE wait — Approval / Action (NOT a self-advancing supervisor wait, a
    /// timer, or a machine wait), OR (c) Failed and UNRESOLVED (no successful replay/rerun), OR (d) is Running but
    /// genuinely STUCK (started long ago with no recent ledger progress — the reconciler's liveness signal, not the run
    /// row's audit timestamp). Intentionally broader than the cockpit's client-side <c>suspendedNeedingReview</c>
    /// (which is Suspended-minus-queued-decision only); a UI wiring this must reconcile card-vs-zone scope rather than
    /// drop-in replace.
    /// </summary>
    public bool? NeedsAttention { get; init; }

    /// <summary>Inclusive lower bound on <c>created_date</c> — only runs created at or after this instant.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Exclusive upper bound on <c>created_date</c> — only runs created strictly before this instant.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>The empty filter — no constraints; the base team index, newest first.</summary>
    public static RunListFilter None { get; } = new();
}
