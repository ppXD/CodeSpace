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
    public required Guid WorkflowId { get; init; }
    public required int WorkflowVersion { get; init; }

    /// <summary>Open-string source identifier (from request.source_type). e.g. "manual", "provider.github.pull_request".</summary>
    public required string SourceType { get; init; }

    /// <summary>Normalised payload the engine sees as <c>{{trigger.*}}</c>. Sourced from request.normalized_payload_json.</summary>
    public required JsonElement NormalizedPayload { get; init; }

    public required WorkflowRunStatus Status { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required IReadOnlyList<WorkflowRunNodeSummary> Nodes { get; init; }

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

    /// <summary>When the scheduled resume fires (Timer only); null for Approval / Callback.</summary>
    public DateTimeOffset? WakeAt { get; init; }

    /// <summary>The node's suspend payload (e.g. an approval <c>prompt</c>). Empty object when none.</summary>
    public required JsonElement Payload { get; init; }
}

public sealed record WorkflowRunNodeSummary
{
    public required string NodeId { get; init; }
    public required string IterationKey { get; init; }
    public required NodeStatus Status { get; init; }
    public required JsonElement Inputs { get; init; }
    public required JsonElement Outputs { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}
