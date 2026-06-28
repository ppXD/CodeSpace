using System.Text.Json;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// Single-run detail. <see cref="SourceType"/> is an open string and
/// <see cref="NormalizedPayload"/> is sourced from the upstream <c>workflow_run_request</c> row.
/// </summary>
public sealed record WorkflowRunDetail
{
    public required Guid Id { get; init; }

    /// <summary>Parent workflow id for an authored run. <c>null</c> for a snapshot run (it has no parent workflow).</summary>
    public Guid? WorkflowId { get; init; }

    /// <summary>Pinned version for an authored run. <c>null</c> for a snapshot run.</summary>
    public int? WorkflowVersion { get; init; }

    /// <summary>Open-string source identifier (from request.source_type). e.g. "manual", "provider.github.pull_request".</summary>
    public required string SourceType { get; init; }

    /// <summary>The run this one forked from — set for a replay / rerun (its <see cref="SourceType"/> is "replay" / "rerun"). The UI threads the lineage off it ("Rerun of {parent}").</summary>
    public Guid? ParentRunId { get; init; }

    /// <summary>Normalised payload the engine sees as <c>{{trigger.*}}</c>. Sourced from request.normalized_payload_json.</summary>
    public required JsonElement NormalizedPayload { get; init; }

    public required WorkflowRunStatus Status { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Run-creation time (≈ run.queued), immutable. The wall-clock "how long did this run take" is CreatedDate →
    /// CompletedAt — NOT StartedAt → CompletedAt: StartedAt is re-stamped on every suspend→resume re-dispatch (the
    /// stuck-run reconciler relies on that per-dispatch reset), so it collapses to ~0s for any agent run that parked.
    /// </summary>
    public required DateTimeOffset CreatedDate { get; init; }

    public required IReadOnlyList<WorkflowRunNodeSummary> Nodes { get; init; }

    /// <summary>
    /// The EXACT graph this run executed — the version-pinned snapshot (an authored run's frozen
    /// <c>workflow_version</c> JSON, or a snapshot run's inline definition), NOT the workflow's current
    /// definition. So a run rendered on the canvas reflects how it ACTUALLY ran even after the workflow is
    /// later edited — keeping replay / audit honest. <c>null</c> only when that snapshot can't be loaded
    /// (a missing version row / corrupt JSON), in which case the UI falls back gracefully.
    /// </summary>
    public WorkflowDefinition? Definition { get; init; }

    /// <summary>
    /// What this run produced — filled by the last successful Terminal node's resolved Inputs
    /// (which map to the workflow's declared Outputs). Empty object for failed / cancelled
    /// runs OR workflows with no declared Outputs. Mirrors <c>workflow_run.outputs_jsonb</c>.
    /// </summary>
    public required JsonElement Outputs { get; init; }

    /// <summary>
    /// The outstanding wait when the run is Suspended — tells the UI WHY it's paused and what
    /// affordance to show (approve/reject buttons for an Approval, a "waiting until…" hint for a
    /// Timer). <c>null</c> unless the run is parked on a pending wait.
    /// </summary>
    public WorkflowRunWaitInfo? PendingWait { get; init; }
}

/// <summary>The pending wait a Suspended run is parked on. Drives the run-detail resume affordance.</summary>
public sealed record WorkflowRunWaitInfo
{
    /// <summary>The node that suspended.</summary>
    public required string NodeId { get; init; }

    /// <summary>One of <c>WorkflowWaitKinds</c> — Timer / Approval / Callback.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// The correlation token. For a Callback wait it's the bearer secret the UI builds the
    /// callback URL from (the run owner shares it with the external system).
    /// </summary>
    public required string Token { get; init; }

    /// <summary>When the scheduled resume fires (Timer only); null for Approval / Callback.</summary>
    public DateTimeOffset? WakeAt { get; init; }

    /// <summary>The node's suspend payload (e.g. an approval <c>prompt</c>). Empty object when none.</summary>
    public required JsonElement Payload { get; init; }
}

public sealed record WorkflowRunNodeSummary
{
    public required string NodeId { get; init; }
    public required string IterationKey { get; init; }

    /// <summary>
    /// The <c>TypeKey</c> of the container that OWNS this row's innermost iteration — e.g.
    /// <c>"flow.map"</c> for a map element-branch body node, <c>"flow.loop"</c> for a loop body node.
    /// <c>null</c> for a top-level (non-iterated) row. The engine builds both a loop body key
    /// (<c>"&lt;loopId&gt;#&lt;i&gt;"</c>) and a map branch key (<c>"&lt;mapId&gt;#&lt;i&gt;"</c>) with the SAME
    /// shape, so <see cref="IterationKey"/> alone can't tell them apart — this disambiguates, letting the
    /// run-detail UI badge / roll up ONLY map fan-outs and leave loop iterations as plain rows. Resolved
    /// from the run's version-pinned definition by looking up the iteration key's leaf container id.
    /// </summary>
    public string? ContainerKind { get; init; }

    public required NodeStatus Status { get; init; }
    public required JsonElement Inputs { get; init; }
    public required JsonElement Outputs { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// For a <c>flow.subworkflow</c> node — the id of the child run it spawned (the Subworkflow
    /// wait's token). Lets the run-detail UI embed / link the child run inline for this step in
    /// any state (suspended while the child runs, or after it completed). <c>null</c> for every
    /// other node. Sourced from the <c>workflow_run_wait</c> row, which persists post-resolution.
    /// </summary>
    public string? ChildRunId { get; init; }

    /// <summary>
    /// For an <c>agent.code</c> node — the id of the agent run it spawned (the AgentRun wait's token). Lets
    /// the run-detail UI stream that run's live event timeline + status inline for this step, in any state
    /// (suspended while the agent works, or after it finished). <c>null</c> for every other node. Sourced
    /// from the <c>workflow_run_wait</c> row, which persists post-resolution.
    /// </summary>
    public string? AgentRunId { get; init; }

    /// <summary>
    /// Whether a from-node rerun (<c>POST /runs/{id}/rerun-from-node</c>) would be ACCEPTED with this node as the
    /// target — computed by the SAME three gates the rerun endpoint enforces: the forward closure holds no suspendable
    /// / container node, AND every kept upstream cell that ran settled reusably (Success / Skipped / Failure-with-error
    /// -edge). Lets the UI offer "Rerun from here" ONLY where the backend will actually accept it, instead of surfacing
    /// a button that 422s on click. Run-state dependent (the reusability gate), so a node can flip rerunnable once a
    /// failed sibling settles. Always <c>false</c> for an iterated (container-body) row — only a top-level node is a
    /// from-node rerun target.
    /// </summary>
    public required bool RerunnableFromHere { get; init; }
}
