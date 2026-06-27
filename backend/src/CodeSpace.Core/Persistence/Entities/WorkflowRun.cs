using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One execution. An AUTHORED run is pinned to <see cref="WorkflowVersion"/> via composite FK
/// (WorkflowId, WorkflowVersion) so the engine reads the EXACT JSON that was live when
/// this run started — never the latest one. Status moves Pending → Running → Success/Failure;
/// Cancelled is operator-initiated mid-flight.
///
/// <para>A SNAPSHOT run (dynamic-workflows substrate) instead carries its own frozen definition
/// inline via <see cref="DefinitionSnapshotJson"/> + <see cref="DefinitionSnapshotHash"/> and
/// leaves <see cref="WorkflowId"/> / <see cref="WorkflowVersion"/> null — there is NO Workflow
/// row and NO WorkflowVersion row behind it. The engine forks on
/// <see cref="DefinitionSnapshotJson"/>: null ⇒ the authored (WorkflowId, Version) lookup;
/// non-null ⇒ the inline snapshot. Everything downstream walks the loaded definition unchanged.</para>
///
/// Source-of-run metadata (kind / payload) lives on <see cref="WorkflowRunRequest"/>. Every
/// run row carries a back-pointer via <see cref="RunRequestId"/>; the engine resolves the
/// payload by joining through the request.
/// </summary>
public class WorkflowRun : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>Parent workflow id for an authored run. NULL for a snapshot run (it carries its own <see cref="DefinitionSnapshotJson"/>).</summary>
    public Guid? WorkflowId { get; set; }

    /// <summary>Pinned version for an authored run. NULL for a snapshot run.</summary>
    public int? WorkflowVersion { get; set; }

    /// <summary>
    /// The inline FROZEN <c>WorkflowDefinition</c> JSON this run executes when it is NOT backed by a
    /// <see cref="WorkflowVersion"/>. NULL for authored runs (they load definition_json from their
    /// pinned version). When non-null the engine deserialises + walks this JSON directly and creates
    /// NO Workflow / WorkflowVersion row. The dynamic-workflows substrate.
    /// </summary>
    public string? DefinitionSnapshotJson { get; set; }

    /// <summary>
    /// SHA-256 canonical hash of <see cref="DefinitionSnapshotJson"/> (same <c>DefinitionHash.Compute</c>
    /// a <see cref="WorkflowVersion"/> row carries). The engine recomputes + compares it at load time;
    /// a mismatch throws the same tamper exception as a drifted authored version. NULL for authored runs.
    /// </summary>
    public string? DefinitionSnapshotHash { get; set; }

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

    /// <summary>
    /// Denormalised from <see cref="WorkflowRunRequest"/>.<c>SourceType</c> at row insert — an open
    /// string (<c>manual</c> / <c>replay</c> / <c>schedule.cron</c> / <c>workflow.child</c> /
    /// <c>provider.github.pull_request</c> / …). The team runs index filters + orders on this without
    /// joining the request, and the partial keyset index excludes child-workflow runs by it. Set at the
    /// two run-creation sites (<c>RunStarter</c>, <c>RunFromSnapshotStarter</c>); <c>required</c> so the
    /// compiler enforces every creation site populates it.
    /// </summary>
    public required string SourceType { get; set; }

    /// <summary>
    /// Denormalised from <see cref="WorkflowRunRequest"/>.<c>ActorId</c> at row insert — the user who launched this run.
    /// NULL for a webhook / system run with no user actor (the request's <c>ActorId</c> is itself nullable). Lets the
    /// runs index filter by launcher without joining the request; recheck-tier filter (no dedicated index yet).
    /// </summary>
    public Guid? ActorId { get; set; }

    /// <summary>
    /// Coarse semantic origin — a Postgres GENERATED column derived from <see cref="SourceType"/> (workflow / task /
    /// event / replay / schedule / child / api / other; see <c>RunKinds</c>). Read-only: the DB computes it, EF never
    /// writes it. Filter by run kind without classifying client-side.
    /// </summary>
    public string RunKind { get; private set; } = "";

    /// <summary>
    /// The projection / coordination MODE of a task run (single-agent / plan-map-synth / supervisor / …; an open string,
    /// see <c>TaskProjectionKinds</c>). NULL for an authored / non-task run. Denormalised from the route's projection
    /// kind at the snapshot creation site — it is NOT derivable from a column (it lives in the snapshot node graph).
    /// </summary>
    public string? ProjectionKind { get; set; }

    /// <summary>
    /// Launch-time SCOPE: the repositories this run was launched against (multi-repo) — a point-in-time snapshot set at
    /// the snapshot/task creation site. NOT the repos the run actually touched (that is the future
    /// <c>touched_repository_ids</c> from the Changes projector). Empty for an authored workflow run (its repos live in
    /// the definition). <c>uuid[]</c> + GIN; the repo filter is an array-overlap probe. <see cref="ScopeProjectIds"/> is
    /// derived from these via <c>project_repository</c> at launch.
    /// </summary>
    public List<Guid> ScopeRepositoryIds { get; set; } = [];

    /// <summary>Launch-time SCOPE: the projects the run's <see cref="ScopeRepositoryIds"/> belonged to AT LAUNCH (a repo may be in many projects), a point-in-time snapshot. Empty for an authored run.</summary>
    public List<Guid> ScopeProjectIds { get; set; } = [];

    public WorkflowRunStatus Status { get; set; } = WorkflowRunStatus.Pending;
    public string? Error { get; set; }

    /// <summary>
    /// Phase 3.0 — set by <see cref="Services.Workflows.Dispatch.WorkflowRunDispatcher"/>
    /// when CAS Pending → Enqueued succeeds. NULL while in Pending or after a reconciler
    /// reverts to Pending. The stuck-Enqueued reconciler sweep reads THIS column rather than
    /// <c>LastModifiedDate</c> — <c>ExecuteUpdateAsync</c> does not invoke EF's audit hook,
    /// so a row that sits in Pending then transitions to Enqueued would otherwise look
    /// instantly stale because its audit timestamp still reflects creation time.
    /// </summary>
    public DateTimeOffset? EnqueuedAt { get; set; }

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

    /// <summary>
    /// The lineage ROOT — the original run a replay/rerun chain descends from. NULL means "I am my own
    /// root" (a first-time run, or the original being forked from), so the group key is <c>RootRunId ?? Id</c>.
    /// A fork inherits its parent's root (<c>parent.RootRunId ?? parent.Id</c>); the team Runs index collapses
    /// every run sharing a root into ONE entry (its latest attempt). FK-free bare column, same stance as
    /// <see cref="ParentRunId"/>.
    /// </summary>
    public Guid? RootRunId { get; set; }

    /// <summary>
    /// For a rerun-from-node / map-branch rerun fork, the node id this attempt re-ran from (the map node for a branch
    /// rerun). NULL for a first run or a whole-run replay. Drives the per-node rerun history in the run detail — a node
    /// knows which attempts re-ran it without diffing snapshots.
    /// </summary>
    public string? RerunFromNodeId { get; set; }

    /// <summary>
    /// FK-free pointer (same stance as <see cref="ParentRunId"/> — a bare lineage column, no enforced FK / nav)
    /// to the owning <c>WorkSession</c> — the long-term work-context thread this run is ONE turn of. NULL for a
    /// session-less run, which is every run until the session layer starts binding them (so the default is
    /// byte-identical to pre-session behaviour). Written at the two run-creation sites from a pre-resolved
    /// <c>SessionAssignment</c>; the binding rides the run→AgentRun FK to every child unit automatically.
    /// </summary>
    public Guid? SessionId { get; set; }

    /// <summary>
    /// 1-based ordinal of this run within its <see cref="SessionId"/> session — but ONLY a TOP-LEVEL,
    /// user-visible turn gets a new index. A child / sub-workflow / replay / rerun-from-node INHERITS the
    /// <see cref="SessionId"/> yet consumes NO new turn (it hangs off the timeline via <see cref="ParentRunId"/>),
    /// so its turn index stays NULL. NULL also whenever <see cref="SessionId"/> is NULL.
    /// </summary>
    public int? SessionTurnIndex { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    /// <summary>Nav to the parent workflow for an authored run. NULL for a snapshot run (no parent workflow).</summary>
    public Workflow? Workflow { get; set; }

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
