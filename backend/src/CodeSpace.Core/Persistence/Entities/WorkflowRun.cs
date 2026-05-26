using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One execution. Pinned to <see cref="WorkflowVersion"/> via composite FK
/// (WorkflowId, WorkflowVersion) so the engine reads the EXACT JSON that was live when
/// this run started — never the latest one. Status moves Pending → Running → Success/Failure;
/// Cancelled is operator-initiated mid-flight.
///
/// Source-of-run metadata (kind / payload) lives on <see cref="WorkflowRunRequest"/>. Every
/// run row carries a back-pointer via <see cref="RunRequestId"/>; the engine resolves the
/// payload by joining through the request.
/// </summary>
public class WorkflowRun : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public int WorkflowVersion { get; set; }

    /// <summary>
    /// Denormalised from <c>workflow.team_id</c> at row insert. Every team-scoped query filters
    /// on this directly (no JOIN to workflow). Matches the denormalisation pattern used by
    /// <c>Variable</c> / <c>WorkflowRunRequest</c> / <c>WorkflowArtifact</c>.
    /// </summary>
    public Guid TeamId { get; set; }

    /// <summary>
    /// FK to the <see cref="WorkflowRunRequest"/> that produced this run. Every run traces
    /// back through exactly one request. The run-detail UI joins here for source / actor /
    /// raw payload.
    /// </summary>
    public Guid RunRequestId { get; set; }

    public WorkflowRunStatus Status { get; set; } = WorkflowRunStatus.Pending;
    public string? Error { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// The run's declared outputs — filled by the (last) successful Terminal node's resolved
    /// Inputs map. Empty object when no outputs are declared OR the run didn't reach a
    /// Terminal. External callers read this to get "what did this run produce".
    /// </summary>
    public string OutputsJson { get; set; } = "{}";

    /// <summary>Set when a Suspended node is resumed; engine restarts walking from this node id.</summary>
    public string? ResumedFromNodeId { get; set; }

    /// <summary>
    /// Copy of <c>WorkflowVersion.DefinitionHash</c> captured at run start. Replay verifies
    /// this against the current version-row hash; mismatch throws <c>ReleaseTamperedException</c>.
    /// Empty string for legacy runs predating hashing.
    /// </summary>
    public string ReleaseHashAtRun { get; set; } = string.Empty;

    /// <summary>
    /// For re-runs, points back to the original run that was replayed. NULL for first-time
    /// runs. Drives the run-detail UI's "Replayed from #N" lineage.
    /// </summary>
    public Guid? ParentRunId { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    public Workflow Workflow { get; set; } = default!;

    /// <summary>Nav property to the upstream request. Always present (FK is NOT NULL).</summary>
    public WorkflowRunRequest RunRequest { get; set; } = default!;

    /// <summary>
    /// Npgsql xmin-backed optimistic concurrency token. PostgreSQL stamps every row with a
    /// transaction id on every UPDATE; mapping it as a concurrency token causes EF to add
    /// <c>WHERE xmin = $loaded_xmin</c> to every update, so when two engine workers race the
    /// same run (e.g. duplicate Hangfire delivery of the same job), the second one's SaveChanges throws
    /// <c>DbUpdateConcurrencyException</c> and the engine treats it as "another worker won;
    /// skip". Eliminates duplicate ledger records + status double-transition.
    /// </summary>
    public uint Xmin { get; set; }
}
