using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// A generic, run-neutral filter for the runs index. EVERY field is optional and composes (AND) on top of the
/// team-scoped, child-excluded base — so ONE filter type, ONE query serves every runs surface: a workflow page
/// supplies <see cref="WorkflowId"/>, a "failed" view supplies <see cref="Status"/>, a date view supplies
/// <see cref="Since"/>/<see cref="Until"/>, the cockpit supplies none. Adding a dimension backed by a column already
/// on <c>workflow_run</c> is one field here plus one predicate at the single apply site — no per-surface query. A
/// dimension on another table (e.g. the request's actor) first needs a <c>source_type</c>-style denormalisation onto
/// the run to keep the query JOIN-free and index-driven; it is NOT a one-liner.
///
/// <para>Index coverage (honest — not every combination is an index seek):
/// <list type="bullet">
/// <item>EMPTY / date-window: index seek. The base team query and <see cref="Since"/>/<see cref="Until"/> ride
///   <c>idx_workflow_run_team_keyset (team_id, created_date DESC, id DESC)</c> — the date bound is a range on the
///   index's 2nd column, so a window is a bounded ordered seek, not a scan.</item>
/// <item><see cref="WorkflowId"/>: index seek via the dedicated <c>idx_workflow_run_workflow_keyset</c>.</item>
/// <item>BARE <see cref="Status"/> / <see cref="SourceType"/> (no <see cref="WorkflowId"/>): NOT a leading-column
///   seek — an ordered scan of the team keyset index with a post-scan recheck. Fine for recent / shallow pages;
///   the upgrade for a high-volume status- or source-filtered surface is a
///   <c>(team_id, status|source_type, created_date DESC, id DESC)</c> partial index, added WHEN that surface ships
///   (not pre-built — there is no such caller today).</item>
/// </list>
/// Every supported combination is keyset-pageable regardless of which tier it lands in.</para>
/// </summary>
public sealed record RunListFilter
{
    /// <summary>Only runs of this authored workflow. Null = any source (includes snapshot / task runs with no workflow).</summary>
    public Guid? WorkflowId { get; init; }

    /// <summary>
    /// Only runs in ANY of these lifecycle states (null / empty = any state). A SET, not a single value, so a surface
    /// can ask for the non-terminal "active" group (Pending, Enqueued, Running, Suspended) in ONE query rather than N —
    /// a single status is just a one-element set. Translates to SQL <c>status = ANY(...)</c>, an index recheck on the
    /// keyset scan (same access tier as a single status; see the index-coverage note above).
    /// </summary>
    public IReadOnlyList<WorkflowRunStatus>? Statuses { get; init; }

    /// <summary>Only runs from this open <c>source_type</c> token (e.g. <c>manual</c>, <c>schedule.cron</c>, <c>provider.github.pull_request</c>).</summary>
    public string? SourceType { get; init; }

    /// <summary>Inclusive lower bound on <c>created_date</c> — only runs created at or after this instant.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Exclusive upper bound on <c>created_date</c> — only runs created strictly before this instant.</summary>
    public DateTimeOffset? Until { get; init; }

    /// <summary>The empty filter — no constraints; the base team index, newest first.</summary>
    public static RunListFilter None { get; } = new();
}
